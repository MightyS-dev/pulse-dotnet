using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ScreenTests
    {
        private static async Task<JsonElement[]> Screens(UsageRig rig, params string[] names)
        {
            foreach (var n in names) rig.Runtime.Screen(n, false);
            await rig.Client.ProcessAsync(true);
            return Harness.Events(rig.Transport.Requests).Where(e => e.GetProperty("type").GetString() == "pulse.screen_view").ToArray();
        }
        private static string Prop(JsonElement e, string key) =>
            e.GetProperty("props").TryGetProperty(key, out var v) ? v.GetString() : null;

        [Fact]
        public async Task ScreenSendsSignalWithPreviousScreenWhenKnown()
        {
            var events = await Screens(new UsageRig(withUsage: false), "Home", "Settings");
            Assert.Equal(2, events.Length);
            Assert.Equal("Home", Prop(events[0], "screen")); Assert.Null(Prop(events[0], "previousScreen"));
            Assert.Equal("Settings", Prop(events[1], "screen")); Assert.Equal("Home", Prop(events[1], "previousScreen"));
        }

        [Fact]
        public async Task SameScreenAgainIsIgnoredButReturningAfterAnotherIsSent()
        {
            var events = await Screens(new UsageRig(withUsage: false), "A", "A", " A ", "B", "A");
            Assert.Equal(new[] { "A", "B", "A" }, events.Select(e => Prop(e, "screen")).ToArray());
        }

        [Fact]
        public async Task NamesAreTrimmedCutAndEmptyIgnored()
        {
            var events = await Screens(new UsageRig(withUsage: false), null, "", "   ", "  Home  ", new string('x', 300));
            Assert.Equal(2, events.Length);
            Assert.Equal("Home", Prop(events[0], "screen")); Assert.Equal(128, Prop(events[1], "screen").Length);
        }

        [Fact]
        public async Task AutomaticHookIsIgnoredWhenOptionIsOffButManualStillWorks()
        {
            var rig = new UsageRig(withUsage: false, options: new PulseOptions { AutomaticScreenViews = false });
            rig.Runtime.Screen("Auto", true);
            Assert.Empty(await Screens(rig));
            var events = await Screens(rig, "Manual");
            Assert.Equal("Manual", Prop(Assert.Single(events), "screen"));
        }

        [Fact]
        public async Task AutomaticHookSendsByDefault()
        {
            var rig = new UsageRig(withUsage: false);
            rig.Runtime.Screen("Auto", true);
            await rig.Client.ProcessAsync(true);
            Assert.Single(Harness.Events(rig.Transport.Requests), e => e.GetProperty("type").GetString() == "pulse.screen_view");
        }

        [Fact]
        public async Task OptOutSuppressesScreensAndResetsDedupe()
        {
            var rig = new UsageRig(withUsage: false);
            rig.Runtime.Screen("A", false);
            rig.Runtime.OptOut = true;
            rig.Runtime.Screen("B", false);
            rig.Runtime.OptOut = false;
            var events = await Screens(rig, "A");
            Assert.DoesNotContain(events, e => Prop(e, "screen") == "B");
            Assert.Null(Prop(events.Last(), "previousScreen"));
        }

        [Fact]
        public void OptionIsCopiedInSnapshot()
        {
            Assert.True(new PulseOptions().Snapshot().AutomaticScreenViews);
            Assert.False(new PulseOptions { AutomaticScreenViews = false }.Snapshot().AutomaticScreenViews);
        }
    }
}
