using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Harmony;

namespace WindowsServicesBuildpack
{
    public class WindowsServicesBuildpack : FinalBuildpack 
    {
        
        protected override bool Detect(string buildPath)
        {
            return false;
        }

        protected override void Apply(string buildPath, string cachePath, string depsPath, int index)
        {
            foreach (var file in Directory.EnumerateFiles(Assembly.GetExecutingAssembly().Location).Where(x => x.EndsWith(".dll") || x.EndsWith(".exe")))
            {
                File.Copy(file, Path.Combine(buildPath, Path.GetFileName(file)));
            }
        }

        public override string GetStartupCommand(string buildPath)
        {
            return $"{Assembly.GetExecutingAssembly().Location} run";
        }

        protected override int DoRun(string[] args)
        {
            if (args[0] == "run")
            {
                Main(args.Skip(0).ToArray());
                return 0;
            }
            return base.DoRun(args);
        }

        private void Main(string[] args)
        {
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var filename = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
            var targetExe = Directory.EnumerateFiles(folder)
                .FirstOrDefault(x => x.EndsWith(".exe") && Path.GetFileName(x) != filename && !Path.GetFileName(x).Contains("EasyHook"));

            if (targetExe == null)
            {
                Console.Error.Write("Target executable not found");
                return;
            }

            var context = new InjectorContext(args, HarmonyInstance.Create("bootstrapper"));
            var injectors = new Injector[]
            {
                new ScimControllerInjector(context),
                new EventLogInjector(context)
            };
            foreach (var injector in injectors) injector.Install();


            var serviceAsm = Assembly.LoadFile(targetExe);
            var entryPoint = serviceAsm.EntryPoint;

            if (entryPoint.GetParameters().Any())
                Task.Run(() => entryPoint.Invoke(null, new[] {new string[0]}));
            else
                Task.Run(() => entryPoint.Invoke(null, null));
            Console.WriteLine("Press CTRL+C to Shutdown...");
            ApplicationLifecycle.ShutdownCompleteHandle.WaitOne();
        }
    }
}
