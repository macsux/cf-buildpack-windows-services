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
using Nuke.Common.Tools.CloudFoundry;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using Octokit;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.CloudFoundry.CloudFoundryTasks;
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
    
    [Parameter("Cloud Foundry API endpoint")]
    string CfApiUrl;
    [Parameter("If SSL should be skipped when talking to Cloud Foundry")]
    bool CfSkipSsl = true;
    [Parameter("Cloud Foundry Username")]
    string CfUsername;
    [Parameter("Cloud Foundry Password")]
    string CfPassword;
    [Parameter("Cloud Foundry org")]
    string CfOrg;
    [Parameter("Cloud Foundry space")]
    string CfSpace;
    [Parameter("Skip loggin and target and use whatever the current CF cli is pointing at")]
    bool CfSkipLogin;
    
    

    IEnumerable<PublishTarget> PublishCombinations
    {
        get
        {
            if (Stack.HasFlag(StackType.Windows))
                yield return new PublishTarget {Framework = "net472", Runtime = "win-x64"};
            // if (Stack.HasFlag(StackType.Windows))
            //     yield return new PublishTarget {Framework = "net472", Runtime = "win-x86"};
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
    AbsolutePath GetPublishSampleDir(PublishTarget publishCombination) => ArtifactsDirectory / $"sampleapp-{publishCombination.Runtime}" ;
    string[] LifecycleHooks = {"detect", "supply", "release", "finalize", "launch"};

    Target Clean => _ => _
        .Description("Cleans up **/bin and **/obj folders")
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
        });

    [Parameter("The URL of test buildpack. If not specified, will be set to where Release publishes to")]
    string BuildpackUrl;

    Target PublishSample => _ => _
        .DependsOn(Clean)
        .DependsOn(PublishSolution)
        .Executes(async () =>
        {
            foreach (var publishCombination in PublishCombinations)
            {
                var samplesFolder = RootDirectory / "test" / "SampleService";

                var publishSampleDir = GetPublishSampleDir(publishCombination);
                DeleteDirectory(publishSampleDir);
                CopyDirectoryRecursively(samplesFolder / "bin" / Configuration / publishCombination.Framework / publishCombination.Runtime / "publish", publishSampleDir);

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
            }
        });

    Target PublishSolution => _ => _
        .Unlisted()
        .DependsOn(Clean)
        .Description("Executes DotNet publish on the solution")
        .Executes(() =>
        {
            DotNetPublish(s => s
                .SetProject(Solution)
                .SetConfiguration(Configuration)

                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .CombineWith(PublishCombinations, (ss,v) => ss
                    .SetFramework(v.Framework)
                    .SetRuntime(v.Runtime))
            );
        });

    Target Publish => _ => _
        .Description("Packages buildpack in Cloud Foundry expected format into /artifacts directory")
        .DependsOn(Clean)
        .DependsOn(PublishSolution)
        .Triggers(PublishSample)
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
                if(BuildpackUrl == null)
                    BuildpackUrl = releaseAsset.BrowserDownloadUrl;
                
                Logger.Block(releaseAsset.BrowserDownloadUrl);
            }
        });

    Target AcceptanceTest => _ => _
        .DependsOn(Release)
        .Requires(() => CfApiUrl)
        .Requires(() => CfUsername)
        .Requires(() => CfPassword)
        .Requires(() => CfOrg)
        .Requires(() => CfSpace)
        .Executes(async () =>
        {
            if (!CfSkipLogin)
            {
                CloudFoundryApi(o => o
                    .SetUrl(CfApiUrl)
                    .SetSkipSSLValidation(CfSkipSsl));
                CloudFoundryAuth(o => o
                    .SetUsername(CfUsername)
                    .SetPassword(CfPassword));
                CloudFoundryCreateSpace(o => o
                    .SetOrg(CfOrg)
                    .SetSpace(CfSpace));
                CloudFoundryTarget(o => o
                    .SetOrg(CfOrg)
                    .SetSpace(CfSpace));
            }

            foreach (var publishCombination in PublishCombinations)
            {
                var appName = "test-windows-service";
                CloudFoundryDeleteApplication(o => o.SetAppName(appName));
                var publishSampleDir = GetPublishSampleDir(publishCombination);
                CloudFoundryPush(o => o
                    .SetWorkingDirectory(publishSampleDir)
                    .SetBuildpack(BuildpackUrl)
                );
                CloudFoundryStop(o => o.SetAppName(appName));
                // await Task.Delay(10000);
                var result = CloudFoundry($"logs {appName} --recent");
                ControlFlow.Assert(result.Any(x => x.Text.Contains("OnStart called")), "OnStart was not called");
                // ControlFlow.Assert(result.Any(x => x.Text.Contains("OnStop called")), "OnStop was not called");
                
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
