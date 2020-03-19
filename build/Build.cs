using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using Octokit;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using FileMode = System.IO.FileMode;
using ZipFile = System.IO.Compression.ZipFile;

[assembly: InternalsVisibleTo("WindowsServicesBuildpackTests")]
[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    [Flags]
    public enum StackType
    {
        Windows = 1,
        Linux = 2
    }
    public static int Main () => Execute<Build>(x => x.Publish);
    const string BuildpackProjectName = "WindowsServicesBuildpack";
    AbsolutePath BuildpackProjectDirectory => RootDirectory / "src" / BuildpackProjectName;
    string GetPackageZipName(string runtime) => $"{BuildpackProjectName}-{runtime}-{GitVersion.MajorMinorPatch}.zip";

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    readonly StackType Stack = StackType.Windows;
    
    [Parameter("GitHub personal access token with access to the repo")]
    string GitHubToken;

    [Parameter("Application directory against which buildpack will be applied")]
    readonly string ApplicationDirectory;

    IEnumerable<PublishTarget> PublishCombinations
    {
        get
        {
            if (Stack.HasFlag(StackType.Windows))
                yield return new PublishTarget {Framework = "net472", Runtime = "win-x64"};
        }
    }

    bool IsMultipleStacks => ((Stack & (Stack - 1)) != 0);
    
    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    string ReleaseName => $"v{GitVersion.MajorMinorPatch}";
    
    string[] LifecycleHooks = {"detect", "supply", "release", "finalize", "launch"};

    Target Clean => _ => _
        .Description("Cleans up **/bin and **/obj folders")
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
        });

    Target PublishSample => _ => _
        .Executes(async () =>
        {
            var samplesFolder = RootDirectory / "test" / "SampleService";
            MSBuildTasks.MSBuild(c => c
                .SetConfiguration(Configuration)
                .SetSolutionFile(samplesFolder / "SampleService.csproj"));
            var publishSampleDir = ArtifactsDirectory / "sampleapp";
            DeleteDirectory(publishSampleDir);
            CopyDirectoryRecursively(samplesFolder / "bin" / Configuration, publishSampleDir);
            
            var client = new GitHubClient(new ProductHeaderValue(BuildpackProjectName));
            var gitIdParts = GitRepository.Identifier.Split("/");
            var owner = gitIdParts[0];
            var repoName = gitIdParts[1];
            var latestRelease = await client.Repository.Release.GetLatest(owner, repoName);
            var latestBuildpackUrl = latestRelease.Assets.FirstOrDefault(x => x.Name.Contains("x64"))?.BrowserDownloadUrl;
            ControlFlow.NotNull(latestBuildpackUrl, "Can't find buildpack URL asset on github releases");
            
            var manifestTemplate = File.ReadAllText(samplesFolder / "manifest.yml");
            var manifestText = manifestTemplate.Replace("{{buildpackurl}}", latestBuildpackUrl);
            File.WriteAllText(publishSampleDir / "manifest.yml", manifestText);
            Logger.Block($"Sample has been compiled and can be pushed from {samplesFolder}");
        });

    Target Publish => _ => _
        .Description("Packages buildpack in Cloud Foundry expected format into /artifacts directory")
        .DependsOn(Clean)
        .Executes(async () =>
        {
            foreach (var publishCombination in PublishCombinations)
            {
                var framework = publishCombination.Framework;
                var runtime = publishCombination.Runtime;
                var packageZipName = GetPackageZipName(runtime);
                var workDirectory = TemporaryDirectory / "pack";
                EnsureCleanDirectory(TemporaryDirectory);
                var buildpackProject = Solution.GetProject(BuildpackProjectName);
                if(buildpackProject == null)
                    throw new Exception($"Unable to find project called {BuildpackProjectName} in solution {Solution.Name}");
                var publishDirectory = buildpackProject.Directory / "bin" / Configuration / framework / runtime / "publish";
                var workBinDirectory = workDirectory / "bin";

                DotNetPublish(s => s
                    .SetProject(Solution)
                    .SetConfiguration(Configuration)
                    .SetFramework(framework)
                    .SetRuntime(runtime)
                    .SetAssemblyVersion(GitVersion.AssemblySemVer)
                    .SetFileVersion(GitVersion.AssemblySemFileVer)
                    .SetInformationalVersion(GitVersion.InformationalVersion)
                );

                var lifecycleBinaries = Solution.GetProjects("Lifecycle*")
                    .Select(x => x.Directory / "bin" / Configuration / framework / runtime / "publish")
                    .SelectMany(x => Directory.GetFiles(x).Where(path => LifecycleHooks.Any(hook => Path.GetFileName(path).StartsWith(hook))));

                foreach (var lifecycleBinary in lifecycleBinaries)
                {
                    CopyFileToDirectory(lifecycleBinary, workBinDirectory, FileExistsPolicy.OverwriteIfNewer);
                }
                DeleteDirectory(publishDirectory / "app.publish");
                CopyDirectoryRecursively(publishDirectory, workBinDirectory, DirectoryExistsPolicy.Merge);
                var tempZipFile = TemporaryDirectory / packageZipName;

                ZipFile.CreateFromDirectory(workDirectory, tempZipFile, CompressionLevel.NoCompression, false);
                MakeFilesInZipUnixExecutable(tempZipFile);
                CopyFileToDirectory(tempZipFile, ArtifactsDirectory, FileExistsPolicy.Overwrite);
                Logger.Block(ArtifactsDirectory / packageZipName);
            }
        });
    
    
    Target Release => _ => _
        .Description("Creates a GitHub release (or amends existing) and uploads buildpack artifact")
        .DependsOn(Publish)
        .Requires(() => GitHubToken)
        .Executes(async () =>
        {
            foreach (var publishCombination in PublishCombinations)
            {
                var runtime = publishCombination.Runtime;
                var packageZipName = GetPackageZipName(runtime);
                if (!GitRepository.IsGitHubRepository())
                    throw new Exception("Only supported when git repo remote is github");
    
                var client = new GitHubClient(new ProductHeaderValue(BuildpackProjectName))
                {
                    Credentials = new Credentials(GitHubToken, AuthenticationType.Bearer)
                };
                var gitIdParts = GitRepository.Identifier.Split("/");
                var owner = gitIdParts[0];
                var repoName = gitIdParts[1];
    
                
                Release release;
                try
                {
                    release = await client.Repository.Release.Get(owner, repoName, ReleaseName);
                }
                catch (NotFoundException)
                {
                    var newRelease = new NewRelease(ReleaseName)
                    {
                        Name = ReleaseName,
                        Draft = false,
                        Prerelease = false
                    };
                    release = await client.Repository.Release.Create(owner, repoName, newRelease);
                }
    
                var existingAsset = release.Assets.FirstOrDefault(x => x.Name == packageZipName);
                if (existingAsset != null)
                {
                    await client.Repository.Release.DeleteAsset(owner, repoName, existingAsset.Id);
                }
    
                var zipPackageLocation = ArtifactsDirectory / packageZipName;
                var stream = File.OpenRead(zipPackageLocation);
                var releaseAssetUpload = new ReleaseAssetUpload(packageZipName, "application/zip", stream, TimeSpan.FromHours(1));
                var releaseAsset = await client.Repository.Release.UploadAsset(release, releaseAssetUpload);
    
                Logger.Block(releaseAsset.BrowserDownloadUrl);
            }
        });

    Target Detect => _ => _
        .Description("Invokes buildpack 'detect' lifecycle event")
        .Requires(() => ApplicationDirectory)
        .Requires(() => !IsMultipleStacks)
        .Executes(() =>
        {
            var framework = PublishCombinations.Single().Framework;
            try
            {
                DotNetRun(s => s
                    .SetProjectFile(Solution.GetProject("Lifecycle.Detect").Path)
                    .SetApplicationArguments(ApplicationDirectory)
                    .SetConfiguration(Configuration)
                    .SetFramework(framework));
                Logger.Block("Detect returned 'true'");
            }
            catch (ProcessException)
            {
                Logger.Block("Detect returned 'false'");
            }
        });

    Target Supply => _ => _
        .Description("Invokes buildpack 'supply' lifecycle event")
        .Requires(() => ApplicationDirectory)
        .Requires(() => !IsMultipleStacks)
        .Executes(() =>
        {
            var framework = PublishCombinations.Single().Framework;
            var home = (AbsolutePath)Path.GetTempPath() / Guid.NewGuid().ToString();
            var app = home / "app";
            var deps = home / "deps";
            var index = 0;
            var cache = home / "cache";
            CopyDirectoryRecursively(ApplicationDirectory, app);

            DotNetRun(s => s
                .SetProjectFile(Solution.GetProject("Lifecycle.Supply").Path)
                .SetApplicationArguments($"{app} {cache} {app} {deps} {index}")
                .SetConfiguration(Configuration)
                .SetFramework(framework));
            Logger.Block($"Buildpack applied. Droplet is available in {home}");

        });

    public void MakeFilesInZipUnixExecutable(AbsolutePath zipFile)
    {
        var tmpFileName = zipFile + ".tmp";
        using (var input = new ZipInputStream(File.Open(zipFile, FileMode.Open)))
        using (var output = new ZipOutputStream(File.Open(tmpFileName, FileMode.Create)))
        {
            output.SetLevel(9);
            ZipEntry entry;
		
            while ((entry = input.GetNextEntry()) != null)
            {
                var outEntry = new ZipEntry(entry.Name) {HostSystem = (int) HostSystemID.Unix};
                var entryAttributes =  
                    ZipEntryAttributes.ReadOwner | 
                    ZipEntryAttributes.ReadOther | 
                    ZipEntryAttributes.ReadGroup |
                    ZipEntryAttributes.ExecuteOwner | 
                    ZipEntryAttributes.ExecuteOther | 
                    ZipEntryAttributes.ExecuteGroup;
                entryAttributes = entryAttributes | (entry.IsDirectory ? ZipEntryAttributes.Directory : ZipEntryAttributes.Regular);
                outEntry.ExternalFileAttributes = (int) (entryAttributes) << 16; // https://unix.stackexchange.com/questions/14705/the-zip-formats-external-file-attribute
                output.PutNextEntry(outEntry);
                input.CopyTo(output);
            }
            output.Finish();
            output.Flush();
        }

        DeleteFile(zipFile);
        RenameFile(tmpFileName,zipFile, FileExistsPolicy.Overwrite);
    }
    
    [Flags]
    enum ZipEntryAttributes
    {
        ExecuteOther = 1,
        WriteOther = 2,
        ReadOther = 4,
	
        ExecuteGroup = 8,
        WriteGroup = 16,
        ReadGroup = 32,

        ExecuteOwner = 64,
        WriteOwner = 128,
        ReadOwner = 256,

        Sticky = 512, // S_ISVTX
        SetGroupIdOnExecution = 1024,
        SetUserIdOnExecution = 2048,

        //This is the file type constant of a block-oriented device file.
        NamedPipe = 4096,
        CharacterSpecial = 8192,
        Directory = 16384,
        Block = 24576,
        Regular = 32768,
        SymbolicLink = 40960,
        Socket = 49152
	
    }
    class PublishTarget
    {
        public string Framework { get; set; }
        public string Runtime { get; set; }
    }
}
