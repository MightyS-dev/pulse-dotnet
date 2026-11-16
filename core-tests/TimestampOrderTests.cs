using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class TimestampOrderTests
    {
        private static long Millis(JsonElement e) =>
            DateTimeOffset.ParseExact(e.GetProperty("timestamp").GetString(), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();
        private static string[] Types(JsonElement[] events) => events.Select(e => e.GetProperty("type").GetString()).ToArray();

        [Fact]
        public async Task ThreeSignalsInTheSameMillisecondGetPlusZeroPlusOnePlusTwo()
        {
            var h = new Harness();
            h.Runtime.Track("a"); h.Runtime.Track("b"); h.Runtime.Track("c"); await h.Client.ProcessAsync(true);
            var events = Harness.Events(h.Transport.Requests); var stamps = events.Select(Millis).ToArray();
            Assert.Equal(new[] { "a", "b", "c" }, Types(events));
            Assert.Equal(new[] { stamps[0], stamps[0] + 1, stamps[0] + 2 }, stamps);
        }

        [Fact]
        public async Task AutomaticSignalsComeBeforeTheTriggerInOrderAndTime()
        {
            var rig = new UsageRig();
            rig.Runtime.Track("first"); rig.Runtime.Track("second"); await rig.Usage.Idle; await rig.Client.ProcessAsync(true);
            var events = Harness.Events(rig.Transport.Requests); var stamps = events.Select(Millis).ToArray();
            Assert.Equal(new[] { "pulse.new_install", "pulse.session_started", "first", "second" }, Types(events));
            for (var i = 1; i < stamps.Length; i++) Assert.Equal(stamps[i - 1] + 1, stamps[i]);
        }

        [Fact]
        public async Task SignalsTrackedBeforeInitializeKeepTheirCaptureOrder()
        {
            var h = new Harness(initialize: false);
            h.Runtime.Track("a"); h.Runtime.Track("b"); h.Runtime.Initialize("synthetic-key"); h.Runtime.Track("c");
            await h.Client.ProcessAsync(true);
            var events = Harness.Events(h.Transport.Requests); var stamps = events.Select(Millis).ToArray();
            Assert.Equal(new[] { "a", "b", "c" }, Types(events));
            Assert.Equal(new[] { stamps[0], stamps[0] + 1, stamps[0] + 2 }, stamps);
        }

        [Fact]
        public async Task AClockThatGoesBackwardsStillYieldsIncreasingTimestamps()
        {
            var h = new Harness();
            h.Runtime.Track("a"); h.Clock.Advance(-5); h.Runtime.Track("b"); h.Runtime.Track("c");
            await h.Client.ProcessAsync(true);
            var events = Harness.Events(h.Transport.Requests); var stamps = events.Select(Millis).ToArray();
            Assert.Equal(new[] { "a", "b", "c" }, Types(events));
            Assert.Equal(new[] { stamps[0], stamps[0] + 1, stamps[0] + 2 }, stamps);
        }

        [Fact]
        public async Task ALaterClockReadingIsKept()
        {
            var h = new Harness();
            h.Runtime.Track("a"); h.Clock.Advance(2); h.Runtime.Track("b"); await h.Client.ProcessAsync(true);
            var stamps = Harness.Events(h.Transport.Requests).Select(Millis).ToArray();
            Assert.Equal(2000, stamps[1] - stamps[0]);
        }
    }
}
