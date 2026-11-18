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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PauseAndFocusHooksReportOneBackgroundAndPersistTheEnd(bool focus)
        {
            var clock = new FakeClock(); var storage = new MemoryStorage(); var transport = new FakeTransport();
            var host = new PulseHost(clock, storage, transport);
            host.Initialize("synthetic", new PulseOptions { FlushIntervalSeconds = 3600, Control = ControlMode.Off },
                new PulsePlatformOptions { EnvironmentInfo = new FakeEnvironment() });
            try
            {
                host.Runtime.Track("start"); await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                clock.Advance(17);
                await (focus ? host.FocusChangedAsync(false) : host.PauseAsync()).WaitAsync(TimeSpan.FromSeconds(10));
                await host.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
                host.Runtime.ReportBackground();
                var background = Assert.Single(Harness.Events(transport.Requests), e => e.GetProperty("type").GetString() == "pulse.app_backgrounded");
                Assert.Equal("17", background.GetProperty("system").GetProperty("sessionSeconds").GetString());
                Assert.Equal(17, background.GetProperty("props").GetProperty("foregroundSeconds").GetInt32());
                await host.Usage.Idle;
            }
            finally { await host.ShutdownAsync(); }
            var nextTransport = new FakeTransport(); var next = new PulseHost(clock, storage, nextTransport);
            next.Initialize("synthetic", new PulseOptions { Control = ControlMode.Off }, new PulsePlatformOptions { EnvironmentInfo = new FakeEnvironment() });
            try
            {
                next.Runtime.Track("next_launch"); await next.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                var signal = Harness.Events(nextTransport.Requests).Last();
                Assert.Equal("17", signal.GetProperty("system").GetProperty("previousSessionSeconds").GetString());
                clock.Advance(1); await next.FocusChangedAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
                await next.FocusChangedAsync(true); clock.Advance(1);
                await next.FocusChangedAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(2, Harness.Events(nextTransport.Requests).Count(e => e.GetProperty("type").GetString() == "pulse.app_backgrounded"));
            }
            finally { await next.ShutdownAsync(); }
        }

        [Fact]
        public async Task HostSendsLaunchMsOnTheColdSessionAndTheReportedEvents()
        {
            var storage = new MemoryStorage();
            storage.Files["config"] = Encoding.UTF8.GetBytes("{\"enabledSignals\":[\"pulse.app_opened\"]}");
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
