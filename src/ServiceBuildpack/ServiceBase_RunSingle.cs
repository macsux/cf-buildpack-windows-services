using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceProcess;
using Harmony;

namespace Bootstrap
{
    [Harmony]
    internal class ServiceBase_RunSingle
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(ServiceBase), nameof(ServiceBase.Run), new[] {typeof(ServiceBase)});

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr) =>
            new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ServiceBase_RunSingle), nameof(Run))),
                new CodeInstruction(OpCodes.Ret)
            };

        internal static void Run(ServiceBase[] services)
        {
            ServiceBase_RunMultiple.Run(services);
        }
    }
}