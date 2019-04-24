using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Harmony;

namespace MyBuildpack
{
    public class MyBuildpack : FinalBuildpack
    {
        static int Main(string[] args)
        {
            return new MyBuildpack().Run(args);
        }
        protected override bool Detect(string buildPath)
        {
            return false;
        }

        protected override void Apply(string buildPath, string cachePath, string depsPath, int index)
        {
            var buildpackFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var file in Directory.EnumerateFiles(buildpackFolder))
            {
                File.Copy(file, Path.Combine(buildPath,Path.GetFileName(file)));
            }
        }

        protected override int DoRun(string[] args)
        {
            var command = args[0];
            switch (command)
            {
                case "launch":
                    StartProcess();
                    break;
                default:
                    return base.DoRun(args);
            }

            return 0;
        }

        private void StartProcess()
        {
            var harmony = HarmonyInstance.Create("servicepatch");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var filename = Path.GetFileName(Assembly.GetExecutingAssembly().Location);

            foreach (var file in Directory.EnumerateFiles(folder)
                .Where(x => (x.EndsWith(".dll") || x.EndsWith(".exe")) && Path.GetFileName(x) != filename))
            {
                try
                {
                    Assembly.LoadFrom(file);
                }
                // ReSharper disable once EmptyGeneralCatchClause
                catch
                {
                }
                
            }

            MethodInfo entryPoint;
            try
            {
                entryPoint = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(x => x != Assembly.GetExecutingAssembly() && x.EntryPoint != null)
                    .Select(x => x.EntryPoint)
                    .Single();
                
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Unable to find entry point for the application");
                Environment.Exit(1);
                return;
            }

            try
            {
                entryPoint.Invoke(null, null);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Failed to start the service");
                Console.Error.WriteLine(e);
                Environment.Exit(1);
            }
        }

        public override string GetStartupCommand(string buildPath)
        {
            var selfName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
            var isSelfDll = selfName.EndsWith("exe");
            if (isSelfDll)
            {
                return $"{Path.Combine(buildPath, selfName.Remove(selfName.Length - ".dll".Length))} launch";
            }
            else
            {
                return $"{Path.Combine(buildPath, selfName)} launch";
            }
        }
    }
}
