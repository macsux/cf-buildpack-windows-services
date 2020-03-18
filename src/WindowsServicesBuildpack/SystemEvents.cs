﻿using System.Runtime.InteropServices;

namespace WindowsServicesBuildpack
{
    public static class SystemEvents
    {
        // Pinvoke
        public delegate bool ConsoleEventDelegate(CtrlEvent eventType);

        private static ConsoleEventDelegate _handler; // Keeps it from getting garbage collected

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        public static void SetConsoleEventHandler(ConsoleEventDelegate handler)
        {
            _handler = handler;
            SetConsoleCtrlHandler(_handler, true);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(CtrlEvent sigevent, int dwProcessGroupId);
    }
}