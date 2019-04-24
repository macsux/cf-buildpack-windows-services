using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceProcess;
using System.Threading;
using Harmony;

namespace Bootstrap
{
    [Harmony]
    internal class ServiceBase_RunMultiple
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(ServiceBase), nameof(ServiceBase.Run), new[] {typeof(ServiceBase[])});

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr) =>
            new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ServiceBase_RunMultiple), nameof(Run))),
                new CodeInstruction(OpCodes.Ret)
            };

        private static readonly ManualResetEvent _exitWaitHandle = new ManualResetEvent(false);

        private static readonly Dictionary<ServiceBase, ServiceControllerStatus> _services =
            new Dictionary<ServiceBase, ServiceControllerStatus>();

        internal static void Run(ServiceBase[] services)
        {
            SystemEvents.SetConsoleEventHandler(ConsoleEventCallback);
            var args = Environment.GetEnvironmentVariable("args")?.Split(' ') ?? new string[0];

            foreach (var service in services)
            {
                if (!_services.TryGetValue(service, out var status))
                {
                    _services.Add(service, ServiceControllerStatus.StartPending);
                }


                if (status != ServiceControllerStatus.Running)
                {
                    var onStart = AccessTools.Method(service.GetType(), "OnStart", new[] {typeof(string[])});
                    onStart.Invoke(service, new object[] {args});
                }
            }

            new Thread(() =>
            {
                Console.ReadLine();
                _exitWaitHandle.Set();
            }).Start();
            _exitWaitHandle.WaitOne();
            foreach (var service in services)
            {
                var onStop = AccessTools.Method(service.GetType(), "OnStop");
                onStop.Invoke(service, null);
                _services.Remove(service);
            }

        }

        static bool ConsoleEventCallback(CtrlEvent eventType)
        {

            _exitWaitHandle.Set();
            return true;
        }
    }
}