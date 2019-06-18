using Harmony;

namespace WindowsServicesBuildpack
{
    public class InjectorContext
    {
        public InjectorContext(string[] args, HarmonyInstance harmonyInstance)
        {
            Args = args;
            HarmonyInstance = harmonyInstance;
        }

        public string[] Args { get; }
        public HarmonyInstance HarmonyInstance { get; }
    }
}