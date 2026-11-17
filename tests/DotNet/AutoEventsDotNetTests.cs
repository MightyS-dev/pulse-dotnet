using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class AutoEventsDotNetTests
    {
        [Fact]
        public void LaunchTimeIsProcessStartToFirstSignalAndNotALaunchWhenTooLong()
        {
            var start = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
            Assert.Equal(1500, LaunchTime.Between(start, start.AddMilliseconds(1500)));
            Assert.Null(LaunchTime.Between(start, start.AddMinutes(11)));
            Assert.Null(LaunchTime.Between(start, start.AddSeconds(-1)));
            var real = LaunchTime.SinceProcessStart(DateTimeOffset.UtcNow);
            Assert.True(real.HasValue && real.Value >= 0);
        }

        [Fact]
        public void PublicCallsBeforeInitializeAndWithBadInputNeverThrow()
        {
            Pulse.DeepLink(null); Pulse.DeepLink("nonsense"); Pulse.ReportForeground(true); Pulse.ReportBackground();
            Pulse.ReportPermission("notification", "granted"); Pulse.ReportPermission(null, null);
        }

        [Fact]
        public async Task HostSendsLaunchMsOnTheColdSessionAndTheReportedEvents()
        {
            var storage = new MemoryStorage();
            storage.Files["config"] = Encoding.UTF8.GetBytes("{\"enabledSignals\":[\"pulse.app_opened\",\"pulse.app_backgrounded\"]}");
            var transport = new FakeTransport();
            var host = new PulseHost(storage: storage, transport: transport);
            host.Initialize("synthetic", new PulseOptions(), new PulsePlatformOptions { EnvironmentInfo = new FakeEnvironment() });
            try
            {
                host.Runtime.ReportForeground(true); host.Runtime.DeepLink("myapp://open/42"); host.Runtime.ReportBackground();
                await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                var events = Harness.Events(transport.Requests);
                var types = events.Select(e => e.GetProperty("type").GetString()).ToArray();
                Assert.Contains("pulse.app_opened", types); Assert.Contains("pulse.deep_link", types); Assert.Contains("pulse.app_backgrounded", types);
                var started = events.First(e => e.GetProperty("type").GetString() == "pulse.session_started");
                Assert.True(started.GetProperty("system").TryGetProperty("launchMs", out _));
            }
            finally { await host.ShutdownAsync(); }
        }
    }
}
