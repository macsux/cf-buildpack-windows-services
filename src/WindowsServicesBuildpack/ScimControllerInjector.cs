﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WindowsServicesBuildpack
{
    public class ScimControllerInjector : Injector
    {
        private static readonly Dictionary<string, ServiceInfo> _serviceCommands = new Dictionary<string, ServiceInfo>();

        private static string[] Args;

        public ScimControllerInjector(InjectorContext context) :
            base(context)
        {
            Args = context.Args;
        }

        protected override void OnInstall()
        {
            CreateHook("advapi32.dll", "StartServiceCtrlDispatcherW", new StartServiceCtrlDispatcherDelegate(StartServiceCtrlDispatcherHook));
            CreateHook("advapi32.dll", "RegisterServiceCtrlHandlerExW", new RegisterServiceCtrlHandlerExDelegate(RegisterServiceCtrlHandlerExHook));
            CreateHook("advapi32.dll", "SetServiceStatus", new SetServiceStatusDelegate(SetServiceStatusHook));
            ApplicationLifecycle.RegisterForGracefulShutdown(async () =>
            {
                Console.WriteLine("Starting graceful shutdown of services");
                await Task.WhenAll(_serviceCommands.Select(x =>
                    Task.Run(() => x.Value.CommandControlDelegate(1, 0, IntPtr.Zero, IntPtr.Zero))));
            });
        }


        private class ServiceInfo
        {
            // not gonna bother cleanup - will get cleanup on it's own when container dies
            private readonly GCHandle _handle;
            public readonly ServiceControlCallbackEx CommandControlDelegate;
            public readonly SERVICE_STATUS Status;
            public readonly IntPtr StatusPtr;

            public ServiceInfo(ServiceControlCallbackEx commandControlDelegate)
            {
                CommandControlDelegate = commandControlDelegate;
                Status = new SERVICE_STATUS();
                _handle = GCHandle.Alloc(Status);
                StatusPtr = GCHandle.ToIntPtr(_handle);
            }
        }


        [StructLayout(LayoutKind.Sequential)]
        public class SERVICE_TABLE_ENTRY
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string name;
            public ServiceMainCallback callback;
        }

        public struct SERVICE_STATUS
        {
            public int serviceType;
            public int currentState;
            public int controlsAccepted;
            public int win32ExitCode;
            public int serviceSpecificExitCode;
            public int checkPoint;
            public int waitHint;
        }

        #region [Delegates]

        public delegate int ServiceControlCallbackEx(
            int control,
            int eventType,
            IntPtr eventData,
            IntPtr eventContext);

        public delegate IntPtr RegisterServiceCtrlHandlerExDelegate(string serviceName, ServiceControlCallbackEx callback, IntPtr userData);

        public delegate void ServiceMainCallback(int argCount, IntPtr argPointer);

        public delegate bool SetServiceStatusDelegate(IntPtr serviceStatusHandle, ref SERVICE_STATUS status);

        public delegate bool StartServiceCtrlDispatcherDelegate(IntPtr entry);

        #endregion


        #region Hooks

        public static bool StartServiceCtrlDispatcherHook(IntPtr entry)
        {
            var services = new List<SERVICE_TABLE_ENTRY>();
            var entrySize = Marshal.SizeOf<SERVICE_TABLE_ENTRY>();
            var pos = entry;
            while (true)
            {
                var entryObj = Marshal.PtrToStructure<SERVICE_TABLE_ENTRY>(pos);
                if(entryObj.name == null)
                    break;
                services.Add(entryObj);
                pos += entrySize;
            }


            foreach (var svc in services)
            {
                var arg = new[] {svc.name}.Union(Args).ToArray();
                var arr = arg.Select(x => Marshal.StringToCoTaskMemUni(x)).ToArray();
                var argPointer = Marshal.UnsafeAddrOfPinnedArrayElement(arr, 0);
                svc.callback(arg.Length, argPointer);
            }

            while (!ApplicationLifecycle.ShutdownCompleteHandle.WaitOne(100))
            {
            }

            return true;
        }


        public static IntPtr RegisterServiceCtrlHandlerExHook(string serviceName, ServiceControlCallbackEx callback, IntPtr userData)
        {
            var serviceInfo = new ServiceInfo(callback);
            _serviceCommands.Add(serviceName, serviceInfo);
            return serviceInfo.StatusPtr;
        }


        public static bool SetServiceStatusHook(IntPtr serviceStatusHandle, ref SERVICE_STATUS status)
        {
            return true;
        }

        #endregion
    }
}