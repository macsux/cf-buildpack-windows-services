using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace WindowsServicesBuildpack
{
    public class WindowsServicesBuildpack : FinalBuildpack
    {
        public override bool Detect(string buildPath)
        {
            return false;
        }

        protected override void Apply(string buildPath, string cachePath, string depsPath, int index)
        {
            Console.WriteLine("===== Windows Service Buildpack ====");
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)))
            {
                File.Copy(file, Path.Combine(buildPath, Path.GetFileName(file)));
            }
        }

        public void Run(string[] args)
        {
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string targetExe = Environment.GetEnvironmentVariable("WINDOWS_SERVICE_EXE");
            if (targetExe == null)
            {
                targetExe = Directory.EnumerateFiles(folder)
                    .FirstOrDefault(x =>
                        x.EndsWith(".exe") &&
                        x.ToLower() != Assembly.GetEntryAssembly().Location.ToLower() &&
                        !Path.GetFileName(x).Contains("EasyHook") && 
                        !Path.GetFileName(x).ToLower().Equals("buildpack.exe") && 
                        !Path.GetFileName(x).ToLower().Equals("detect.exe") && 
                        !Path.GetFileName(x).ToLower().Equals("supply.exe") && 
                        !Path.GetFileName(x).ToLower().Equals("finalize.exe") && 
                        !Path.GetFileName(x).ToLower().Equals("release.exe")  &&
                        !Path.GetFileName(x).ToLower().Equals("launch.exe") 
                        );
            }
            Console.WriteLine($"Identified {targetExe} as the service entry point executable");
            if (targetExe == null || !File.Exists(targetExe))
            {
                Console.Error.Write("Target executable not found");
                return;
            }
            var context = new InjectorContext(args, new Harmony("bootstrapper"));
            var injectors = new Injector[]
            {
                new ScimControllerInjector(context),
                new EventLogInjector(context)
            };
            foreach (var injector in injectors) injector.Install();

            Console.WriteLine("Injectors applied");

            var serviceAsm = Assembly.LoadFile(targetExe);
            var entryPoint = serviceAsm.EntryPoint;
            Console.WriteLine("Starting service Main method...");
            if (entryPoint.GetParameters().Any())
                Task.Run(() => entryPoint.Invoke(null, new[] {new string[0]}));
            else
                Task.Run(() => entryPoint.Invoke(null, null));
            Console.WriteLine("Press CTRL+C to Shutdown...");
            ApplicationLifecycle.ShutdownCompleteHandle.WaitOne();
        }
        public override string GetStartupCommand(string buildPath)
        {
            return $"launch.exe";
        }
    }
}
