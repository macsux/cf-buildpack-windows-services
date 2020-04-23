using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WindowsServicesBuildpack;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace WindowsServices.Tests
{
    public class WindowsServicesTests
    {
        private readonly ITestOutputHelper _output;
        private readonly ConsoleOutputCollector _consoleOutputCollector;

        public WindowsServicesTests(ITestOutputHelper output)
        {
            _output = output;
            _consoleOutputCollector = new ConsoleOutputCollector(output);
            Environment.SetEnvironmentVariable("WINDOWS_SERVICE_EXE",null);
        }

        [Fact]
        public void Launch_WithSampleAppPresent_CorrectOutput()
        {
            Task.Run(async () =>
            {
                await Task.Delay(3000);
                ApplicationLifecycle.Shutdown();
            });
            Lifecycle.Program.Main(new[] {"."});
            _consoleOutputCollector.Output.Should().Contain("OnStart called");
            _consoleOutputCollector.Output.Should().Contain("OnStop called");
        }
        [Fact]
        public void Launch_WithMultipleAppsPresent_SelectsCorrectEntrypoint()
        {
            var otherExeName = "ZampleServices.exe";
            File.Copy("SampleServices.exe",otherExeName, true);
            Environment.SetEnvironmentVariable("WINDOWS_SERVICE_EXE",otherExeName);

            Task.Run(async () =>
            {
                await Task.Delay(3000);
                ApplicationLifecycle.Shutdown();
            });
            Lifecycle.Program.Main(new[] {"."});
            _consoleOutputCollector.Output.Should().Contain($"{otherExeName} as the service entry point executable");
        }
    }
}