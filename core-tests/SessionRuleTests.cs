using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    // A new session id starts when the next signal arrives 30 minutes or more after the previous signal.
    public sealed class SessionRuleTests
    {
        private static string Id(JsonElement e) => e.GetProperty("sessionId").GetString();
        private static string[] Ids(JsonElement[] events, string type) =>
            events.Where(e => e.GetProperty("type").GetString() == type).Select(Id).ToArray();
        private static int Started(JsonElement[] events) => events.Count(e => e.GetProperty("type").GetString() == "pulse.session_started");

        [Fact]
        public async Task SameIdWithin29MinutesAndNewIdAtThirty()
        {
            var rig = new UsageRig();
            var a = await rig.Send("a");
            rig.Clock.Advance(29 * 60 + 59); var b = await rig.Send("b");
            Assert.Equal(Ids(a, "a")[0], Ids(b, "b")[0]);
            Assert.Equal(0, Started(b));
            rig.Clock.Advance(30 * 60); var c = await rig.Send("c");
            Assert.NotEqual(Ids(b, "b")[0], Ids(c, "c")[0]);
            Assert.Equal(1, Started(c));
        }

        [Fact]
        public async Task ExactlyThirtyMinutesRotates()
        {
            var rig = new UsageRig();
            var a = await rig.Send("a"); rig.Clock.Advance(30 * 60); var b = await rig.Send("b");
            Assert.NotEqual(Ids(a, "a")[0], Ids(b, "b")[0]);
            Assert.Equal(1, Started(b));
        }

        [Fact]
        public async Task FourteenHourGapInsideOneRunningProcessRotatesOnce()
        {
            var rig = new UsageRig();
            var a = await rig.Send("a");
            rig.Clock.Advance(14 * 3600);
            var b = await rig.Send("b"); var c = await rig.Send("c");
            Assert.NotEqual(Ids(a, "a")[0], Ids(b, "b")[0]);
            Assert.Equal(Ids(b, "b")[0], Ids(c, "c")[0]);
            Assert.Equal(1, Started(b)); Assert.Equal(0, Started(c));
        }

        [Fact]
        public async Task RestartAfter31MinutesGivesNewIdAndOneSessionStarted()
        {
            var storage = new MemoryStorage();
            var first = new UsageRig(storage); var a = await first.Send("a"); await first.Usage.Idle;
            var second = new UsageRig(storage, installIsNew: () => false);
            second.Clock.Advance((first.Clock.UtcNow - second.Clock.UtcNow).TotalSeconds + 31 * 60);
            var b = await second.Send("b");
            Assert.NotEqual(Ids(a, "a")[0], Ids(b, "b")[0]);
            Assert.Equal(1, Started(b));
        }

        [Fact]
        public async Task FlushAndTimersAloneDoNotExtendASession()
        {
            var rig = new UsageRig();
            var a = await rig.Send("a");
            for (var i = 0; i < 40; i++) { rig.Clock.Advance(60); await rig.Client.ProcessAsync(true); }
            var b = await rig.Send("b");
            Assert.NotEqual(Ids(a, "a")[0], Ids(b, "b")[0]);
            Assert.Equal(1, Started(b));
        }

        [Fact]
        public async Task IdleIsMeasuredFromTheLastSignalNotTheSessionStart()
        {
            var rig = new UsageRig();
            var a = await rig.Send("a");
            rig.Clock.Advance(20 * 60); var b = await rig.Send("b");
            rig.Clock.Advance(20 * 60); var c = await rig.Send("c");
            Assert.Single(new[] { Ids(a, "a")[0], Ids(b, "b")[0], Ids(c, "c")[0] }.Distinct());
            rig.Clock.Advance(40 * 60); await rig.Client.ProcessAsync(true); rig.Clock.Advance(5 * 60);
            var d = await rig.Send("d");
            Assert.NotEqual(Ids(c, "c")[0], Ids(d, "d")[0]);
            Assert.Equal(1, Started(d));
        }

        [Fact]
        public async Task DroppedInvalidSignalsNeitherStartNorExtendASession()
        {
            var rig = new UsageRig();
            var a = await rig.Send("a");
            for (var i = 0; i < 5; i++)
            {
                rig.Clock.Advance(10 * 60);
                rig.Runtime.Track("   "); rig.Runtime.Track("bad", double.NaN);
            }
            var b = await rig.Send("b");
            Assert.NotEqual(Ids(a, "a")[0], Ids(b, "b")[0]);
            Assert.Equal(new[] { "pulse.session_started", "b" }, UsageRig.Types(b));
        }

        [Fact]
        public async Task SignalsTrackedBeforeInitializeUseTheirOwnCaptureTime()
        {
            var clock = new FakeClock(); var transport = new FakeTransport(); var storage = new MemoryStorage();
            var usage = new UsageTracker(() => storage, t => t.UtcDateTime.ToString("yyyy-MM-dd"), null);
            var runtime = new PulseRuntime(clock);
            runtime.Track("early1"); clock.Advance(14 * 3600); runtime.Track("early2");
            PulseClient client = null;
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, storage, new ExtrasEnvironment(), null, false,
                () => 0.5, optOut, usage), null, () => PulseClient.ReadStoredOptOut(storage));
            runtime.Initialize("synthetic-key");
            await usage.Idle; await client.ProcessAsync(true);
            var events = Harness.Events(transport.Requests);
            Assert.NotEqual(Ids(events, "early1")[0], Ids(events, "early2")[0]);
            Assert.Equal(2, Started(events));
        }
    }
}
