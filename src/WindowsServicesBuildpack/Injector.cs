using System;
using EasyHook;

namespace WindowsServicesBuildpack
{
    public abstract class Injector
    {
        protected Injector(InjectorContext context)
        {
            Context = context;
        }

        protected InjectorContext Context { get; }

        protected static LocalHook CreateHook(string dll, string method, Delegate hookImpl)
        {
            var procAddress = LocalHook.GetProcAddress(dll, method);
            var hook = LocalHook.Create(procAddress, hookImpl, null);
            hook.ThreadACL.SetExclusiveACL(new[] {0});
            return hook;
        }

        public void Install()
        {
            OnInstall();
            Context.HarmonyInstance.PatchAll(GetType().Assembly);
        }

        protected virtual void OnInstall()
        {
        }

    }
}