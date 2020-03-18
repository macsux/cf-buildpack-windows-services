using HarmonyLib;

namespace WindowsServicesBuildpack
{
    public class InjectorContext
    {
        public InjectorContext(string[] args, Harmony harmonyInstance)
        {
            Args = args;
            HarmonyInstance = harmonyInstance;
        }

        public string[] Args { get; }
        public Harmony HarmonyInstance { get; }
    }
}