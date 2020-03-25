using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsServicesBuildpack
{
    public static class ApplicationLifecycle
    {
        private static readonly CancellationTokenSource _appShutdownCts = new CancellationTokenSource();
        private static readonly ManualResetEvent _shutdownCompleteHandle = new ManualResetEvent(false);

        private static readonly List<Func<Task>> _shutdownDelegates = new List<Func<Task>>();

        static ApplicationLifecycle()
        {
            SystemEvents.SetConsoleEventHandler(OnTerminateCommand);
        }

        public static ManualResetEvent ShutdownCompleteHandle => _shutdownCompleteHandle;

        private static bool OnTerminateCommand(CtrlEvent eventtype)
        {
            Shutdown();
            return true;
        }

        public static void Shutdown()
        {
            _appShutdownCts.Cancel();
            Task.WhenAll(_shutdownDelegates.Select(x => x())).Wait(TimeSpan.FromSeconds(10));
            _shutdownCompleteHandle.Set();
        }

        public static CancellationToken RegisterForGracefulShutdown(Func<Task> serviceCleanup)
        {
            _shutdownDelegates.Add(serviceCleanup);
            return _appShutdownCts.Token;
        }
    }
}