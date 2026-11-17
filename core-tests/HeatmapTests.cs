using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    // Heatmaps: heatmap capture, buffering, privacy rules and the debug screenshot upload.
    public sealed class HeatmapTests
    {
        private const string On = "{\"version\":1,\"heatmaps\":true}";

        private sealed class Rig
        {
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly FakeTransport Transport = new FakeTransport();
            internal readonly MemoryStorage Storage;
            internal readonly PulseRuntime Runtime;
            internal PulseClient Client;
            internal bool Debug;
            private int seen;
            internal Rig(string config = On, PulseOptions options = null, bool debug = false, MemoryStorage storage = null)
            {
                Storage = storage ?? new MemoryStorage(); Debug = debug;
                if (config != null) Storage.Files["config"] = Encoding.UTF8.GetBytes(config);
                Runtime = new PulseRuntime(Clock);
                Runtime.InstallPlatform((k, o, optOut) => Client = new PulseClient(k, o, Clock, Transport, Storage, new FakeEnvironment(),
                    null, false, () => 0.5, optOut, null, null, () => Debug), null, () => PulseClient.ReadStoredOptOut(Storage));
                Runtime.Initialize("synthetic-key", options);
            }
            internal void Tap(double x, double y, double width = 400, double height = 800, bool keyboard = false, bool input = false, bool ignored = false) =>
                Runtime.HeatmapTap(x, y, width, height, keyboard, input, ignored);
            internal async Task<JsonElement[]> Sent()
            {
                await Client.ProcessAsync(true);
                var all = Harness.Events(Transport.Requests); var fresh = all.Skip(seen).ToArray(); seen = all.Length;
                return fresh;
            }
            internal async Task<JsonElement[]> Maps()
            {
                var all = await Sent();
                return all.Where(e => e.GetProperty("type").GetString() == "pulse.heatmap").ToArray();
            }
            internal async Task<JsonElement[]> FlushMaps() { Runtime.HeatmapFlush(); return await Maps(); }
        }

        private static JsonElement Map(JsonElement e) => e.GetProperty("heatmap");
        private static JsonElement[] Points(JsonElement e) => Map(e).GetProperty("points").EnumerateArray().ToArray();
        private static string Kind(JsonElement p) => p.GetProperty("kind").GetString();

        // ---- config and options ----

        [Fact]
        public void OptionDefaultsToFollowThePortal() => Assert.True(new PulseOptions().CaptureHeatmaps);

        [Fact]
        public void ConfigKeyIsReadAndDefaultsToOff()
        {
            Assert.False(RemoteConfig.Parse("{\"version\":1}").Heatmaps);
            Assert.True(RemoteConfig.Parse("{\"heatmaps\":true}").Heatmaps);
            Assert.False(RemoteConfig.Parse("{\"heatmaps\":\"true\"}").Heatmaps);
            Assert.False(RemoteConfig.AllOn.Heatmaps);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("{\"version\":1,\"heatmaps\":false}")]
        [InlineData("not json")]
        public async Task OffWithoutCacheOrWhenSwitchedOff(string config)
        {
            var rig = new Rig(config);
            rig.Runtime.Screen("Home", false);
            rig.Tap(0.5 * 400, 100);
            Assert.Empty(await rig.FlushMaps());
        }

        [Fact]
        public async Task OnWhenTheCacheSaysSo()
        {
            var rig = new Rig();
            rig.Runtime.Screen("Home", false);
            rig.Tap(200, 340);
            var maps = await rig.FlushMaps();
            var one = Assert.Single(maps);
            Assert.Equal("Home", Map(one).GetProperty("screen").GetString());
            Assert.Equal("phone", Map(one).GetProperty("bucket").GetString());
            Assert.Equal("portrait", Map(one).GetProperty("orientation").GetString());
            Assert.Equal("{\"x\":0.5,\"y\":340,\"kind\":\"tap\"}", Points(one).Single().GetRawText().Replace(" ", ""));
            Assert.Equal("{}", one.GetProperty("props").GetRawText());
        }

        [Fact]
        public async Task CodeOptionOnlySwitchesOff()
        {
            var off = new Rig(On, new PulseOptions { CaptureHeatmaps = false });
            off.Runtime.Screen("Home", false); off.Tap(10, 10);
            Assert.Empty(await off.FlushMaps());
            var noPortal = new Rig("{\"version\":1,\"heatmaps\":false}", new PulseOptions { CaptureHeatmaps = true });
            noPortal.Runtime.Screen("Home", false); noPortal.Tap(10, 10);
            Assert.Empty(await noPortal.FlushMaps());
        }

        [Theory]
        [InlineData("{\"version\":1,\"heatmaps\":true,\"disabledSignals\":[\"pulse.heatmap\"]}")]
        [InlineData("{\"version\":1,\"heatmaps\":true,\"pausedSignals\":[\"pulse.heatmap\"]}")]
        [InlineData("{\"version\":1,\"heatmaps\":true,\"automaticSignals\":false}")]
        [InlineData("{\"version\":1,\"heatmaps\":true,\"screenViews\":false}")]
        public async Task PortalSwitchesStopCapture(string config)
        {
            var rig = new Rig(config);
            rig.Runtime.Screen("Home", false); rig.Tap(10, 10);
            Assert.Empty(await rig.FlushMaps());
        }

        [Fact]
        public async Task AutomaticSignalsOptionOffStopsCapture()
        {
            var rig = new Rig(On, new PulseOptions { AutomaticSignals = false });
            rig.Runtime.Screen("Home", false); rig.Tap(10, 10);
            Assert.Empty(await rig.FlushMaps());
        }

        [Fact]
        public async Task PausedScreenViewsMeansNoScreenAndNoHeatmap()
        {
            var rig = new Rig("{\"version\":1,\"heatmaps\":true,\"pausedSignals\":[\"pulse.screen_view\"]}");
            rig.Runtime.Screen("Home", false); rig.Tap(10, 10);
            Assert.Empty(await rig.FlushMaps());
        }

        [Fact]
        public async Task OptOutStopsCaptureAndDropsTheBuffer()
        {
            var rig = new Rig();
            rig.Runtime.Screen("Home", false); rig.Tap(10, 10);
            rig.Runtime.OptOut = true;
            rig.Tap(20, 20);
            Assert.Empty(await rig.FlushMaps());
            rig.Runtime.OptOut = false;
            Assert.Empty(await rig.FlushMaps());
        }

        [Fact]
        public async Task NoKnownScreenMeansNoCapture()
        {
            var rig = new Rig();
            rig.Tap(10, 10);
            Assert.Empty(await rig.FlushMaps());
            rig.Runtime.Screen("Home", false); rig.Tap(10, 10);
            Assert.Single(await rig.FlushMaps());
        }

        [Fact]
        public async Task ScreenNameIsExactlyTheLastScreenView()
        {
            var rig = new Rig();
            rig.Runtime.Screen("  Shop  ", false); rig.Tap(10, 10);
            var sent = await rig.FlushMaps();
            Assert.Equal("Shop", Map(sent.Single()).GetProperty("screen").GetString());
        }

        [Fact]
        public async Task ConfigRefreshSwitchingOffStopsCaptureAndDropsTheBuffer()
        {
            var release = new TaskCompletionSource<bool>();
            var storage = new MemoryStorage(); storage.Files["config"] = Encoding.UTF8.GetBytes(On);
            var transport = new FakeTransport { ConfigHandler = async (r, c) => { await release.Task; return new TransportResponse(200, "{\"version\":2,\"heatmaps\":false}"); } };
            var clock = new FakeClock(); PulseClient client = null;
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, storage, new FakeEnvironment(), null, false, () => 0.5, optOut),
                null, () => PulseClient.ReadStoredOptOut(storage));
            runtime.Initialize("synthetic-key");
            runtime.Screen("Home", false); runtime.HeatmapTap(10, 10, 400, 800, false, false, false);
            release.SetResult(true); await client.ConfigFetch;
            runtime.HeatmapFlush();
            await client.ProcessAsync(true);
            Assert.DoesNotContain(Harness.Events(transport.Requests), e => e.GetProperty("type").GetString() == "pulse.heatmap");
            runtime.HeatmapTap(10, 10, 400, 800, false, false, false); runtime.HeatmapFlush();
            await client.ProcessAsync(true);
            Assert.DoesNotContain(Harness.Events(transport.Requests), e => e.GetProperty("type").GetString() == "pulse.heatmap");
        }

        [Fact]
        public async Task ConfigRefreshSwitchingOnStartsCapture()
        {
            var storage = new MemoryStorage();
            var transport = new FakeTransport { ConfigHandler = (r, c) => Task.FromResult(new TransportResponse(200, On)) };
            var clock = new FakeClock(); PulseClient client = null;
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, storage, new FakeEnvironment(), null, false, () => 0.5, optOut),
                null, () => PulseClient.ReadStoredOptOut(storage));
            runtime.Initialize("synthetic-key");
            await client.ConfigFetch;
            runtime.Screen("Home", false); runtime.HeatmapTap(10, 10, 400, 800, false, false, false); runtime.HeatmapFlush();
            await client.ProcessAsync(true);
            Assert.Contains(Harness.Events(transport.Requests), e => e.GetProperty("type").GetString() == "pulse.heatmap");
        }

        // ---- point math ----

        [Theory]
        [InlineData(100, 340.04, 400, 0.25, 340)]
        [InlineData(100, 340.05, 400, 0.25, 340.1)]
        [InlineData(133.3333, 10, 400, 0.333, 10)]
        [InlineData(0.2, 10, 400, 0.001, 10)]
        [InlineData(-50, -20, 400, 0, 0)]
        [InlineData(900, 1e9, 400, 1, 100000)]
        public async Task PointMathRoundsAndClamps(double sx, double sy, double width, double x, double y)
        {
            var rig = new Rig();
            rig.Runtime.Screen("Home", false); rig.Tap(sx, sy, width);
            var p = Points((await rig.FlushMaps()).Single()).Single();
            Assert.Equal(x, p.GetProperty("x").GetDouble(), 6);
            Assert.Equal(y, p.GetProperty("y").GetDouble(), 6);
        }

        [Theory]
        [InlineData(10, 599.9)]
        [InlineData(1, 1)]
        public void BucketPhone(double width, double w2) { Assert.Equal("phone", HeatmapCapture.BucketOf(width)); Assert.Equal("phone", HeatmapCapture.BucketOf(w2)); }

        [Theory]
        [InlineData(600, "tablet")] [InlineData(1023.9, "tablet")] [InlineData(1024, "desktop")] [InlineData(3000, "desktop")]
        public void BucketTabletAndDesktop(double width, string bucket) => Assert.Equal(bucket, HeatmapCapture.BucketOf(width));

        [Theory]
        [InlineData(400, 800, "portrait")] [InlineData(800, 800, "portrait")] [InlineData(800, 400, "landscape")]
        public void Orientation(double w, double h, string o) => Assert.Equal(o, HeatmapCapture.OrientationOf(w, h));

        [Theory]
        [InlineData(double.NaN, 1, 400, 800)] [InlineData(1, double.NaN, 400, 800)] [InlineData(1, 1, 0, 800)]
        [InlineData(1, 1, -5, 800)] [InlineData(1, 1, 400, double.NaN)] [InlineData(double.PositiveInfinity, 1, 400, 800)]
        public async Task InvalidNumbersAreNotCaptured(double x, double y, double w, double h)
        {
            var rig = new Rig();
            rig.Runtime.Screen("Home", false); rig.Runtime.HeatmapTap(x, y, w, h, false, false, false);
            Assert.Empty(await rig.FlushMaps());
        }

        // ---- buffering ----

        [Fact]
        public async Task EmptyBuffersAreNeverSent()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false); rig.Runtime.Screen("B", false);
            rig.Runtime.HeatmapFlush(); rig.Runtime.ReportBackground();
            Assert.Empty(await rig.FlushMaps());
        }

        [Fact]
        public async Task ScreenChangeSendsTheOldBufferWithTheOldScreen()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false); rig.Tap(10, 10); rig.Tap(20, 20);
            Assert.Empty(await rig.Maps());
            rig.Runtime.Screen("B", false); rig.Tap(30, 30);
            var sent = await rig.Maps();
            Assert.Equal("A", Map(sent.Single()).GetProperty("screen").GetString());
            Assert.Equal(2, Points(sent.Single()).Length);
            var rest = await rig.FlushMaps();
            Assert.Equal("B", Map(rest.Single()).GetProperty("screen").GetString());
        }

        [Fact]
        public async Task HeatmapComesBeforeTheNextScreenViewInTime()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false); rig.Tap(10, 10);
            rig.Clock.Advance(5);
            rig.Runtime.Screen("B", false);
            var all = await rig.Sent();
            var order = all.Select(e => e.GetProperty("type").GetString()).ToArray();
            Assert.True(Array.IndexOf(order, "pulse.heatmap") < Array.LastIndexOf(order, "pulse.screen_view"));
        }

        [Fact]
        public async Task BucketChangeStartsANewBuffer()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Tap(10, 10, 400, 800); rig.Tap(10, 10, 700, 800);
            var sent = await rig.FlushMaps();
            Assert.Equal(new[] { "phone", "tablet" }, sent.Select(e => Map(e).GetProperty("bucket").GetString()).ToArray());
            Assert.All(sent, e => Assert.Single(Points(e)));
        }

        [Fact]
        public async Task RotationStartsANewBuffer()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Tap(10, 10, 400, 800); rig.Tap(10, 10, 800, 400);
            var sent = await rig.FlushMaps();
            Assert.Equal(new[] { "portrait", "landscape" }, sent.Select(e => Map(e).GetProperty("orientation").GetString()).ToArray());
        }

        [Fact]
        public async Task BackgroundFlushesTheBuffer()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false); rig.Tap(10, 10);
            rig.Runtime.ReportBackground();
            Assert.Single(await rig.Maps());
        }

        [Fact]
        public async Task PublicFlushAndShutdownSendTheBuffer()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false); rig.Tap(10, 10);
            var flush = rig.Runtime.Flush();
            await rig.Client.ProcessAsync(true); await flush;
            Assert.Single(await rig.Maps());
            rig.Tap(10, 10);
            await rig.Runtime.StopAsync();
            Assert.True(rig.Client.Count > 0 || rig.Storage.Files.Count > 0);
        }

        [Fact]
        public async Task TwoHundredPointsSplitIntoANewBuffer()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            for (var i = 0; i < 450; i++) { rig.Tap(i % 400, 10 + i); rig.Clock.Advance(2); }
            var sent = await rig.FlushMaps();
            Assert.Equal(new[] { 200, 200, 50 }, sent.Select(e => Points(e).Length).ToArray());
        }

        [Fact]
        public async Task ThousandPointsPerSessionThenSilentDrop()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            for (var i = 0; i < 1300; i++) { rig.Tap(i % 400, 10 + i); rig.Clock.Advance(0.5); }
            var sent = await rig.FlushMaps();
            Assert.Equal(1000, sent.Sum(e => Points(e).Length));
            Assert.All(sent, e => Assert.True(Points(e).Length <= 200));
        }

        [Fact]
        public async Task NewSessionResetsThePointCap()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            for (var i = 0; i < 1005; i++) { rig.Tap(i % 400, 10 + i); rig.Clock.Advance(0.1); }
            await rig.FlushMaps();
            rig.Clock.Advance(31 * 60);
            rig.Tap(10, 10);
            var sent = await rig.FlushMaps();
            Assert.Equal(1, sent.Sum(e => Points(e).Length));
        }

        [Fact]
        public async Task SessionRuleAppliesToTheSignalTimestamp()
        {
            var rig = new Rig();
            rig.Runtime.Track("first"); var first = (await rig.Sent()).Single();
            rig.Runtime.Screen("A", false); rig.Tap(10, 10);
            rig.Clock.Advance(31 * 60);
            var map = (await rig.FlushMaps()).Single();
            Assert.NotEqual(first.GetProperty("sessionId").GetString(), map.GetProperty("sessionId").GetString());
        }

        // ---- kinds ----

        [Fact]
        public async Task ThirdTapWithinASecondAndThirtyPxIsRage()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Tap(100, 100); rig.Clock.Advance(0.3); rig.Tap(110, 105); rig.Clock.Advance(0.3); rig.Tap(120, 110);
            rig.Clock.Advance(0.3); rig.Tap(100, 100);
            var kinds = Points((await rig.FlushMaps()).Single()).Select(Kind).ToArray();
            Assert.Equal(new[] { "tap", "tap", "rage", "rage" }, kinds);
        }

        [Fact]
        public async Task SlowOrFarTapsAreNotRage()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Tap(100, 100); rig.Clock.Advance(0.6); rig.Tap(100, 100); rig.Clock.Advance(0.6); rig.Tap(100, 100);
            rig.Clock.Advance(5);
            rig.Tap(100, 100); rig.Clock.Advance(0.1); rig.Tap(100, 100); rig.Clock.Advance(0.1); rig.Tap(100, 131);
            var kinds = Points((await rig.FlushMaps()).Single()).Select(Kind).ToArray();
            Assert.All(kinds, k => Assert.Equal("tap", k));
        }

        [Fact]
        public async Task RageIsMeasuredFromTheFirstTapOfTheCluster()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Tap(100, 100); rig.Clock.Advance(0.5); rig.Tap(120, 100); rig.Clock.Advance(0.5); rig.Tap(129, 100);
            Assert.Equal("rage", Kind(Points((await rig.FlushMaps()).Single()).Last()));
            var second = new Rig();
            second.Runtime.Screen("A", false);
            second.Tap(100, 100); second.Clock.Advance(0.4); second.Tap(120, 100); second.Clock.Advance(0.4); second.Tap(131, 100);
            Assert.Equal("tap", Kind(Points((await second.FlushMaps()).Single()).Last()));
        }

        // ---- never capture ----

        [Fact]
        public async Task KeyboardInputsAndIgnoredAreNeverCaptured()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Tap(10, 10, keyboard: true); rig.Tap(10, 10, input: true); rig.Tap(10, 10, ignored: true);
            Assert.Empty(await rig.FlushMaps());
            rig.Tap(10, 10);
            Assert.Single(Points((await rig.FlushMaps()).Single()));
        }

        [Fact]
        public async Task SkippedTapsDoNotCountTowardsRage()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Tap(10, 10); rig.Tap(10, 10, keyboard: true); rig.Tap(10, 10, input: true); rig.Tap(10, 10);
            Assert.All(Points((await rig.FlushMaps()).Single()), p => Assert.Equal("tap", Kind(p)));
        }

        // ---- AE1 counters ----

        [Fact]
        public void HeatmapIsExemptFromTheDailyCapAndNeverDrops()
        {
            var storage = new MemoryStorage(); var state = new AutoState(() => storage);
            var now = new FakeClock().UtcNow;
            for (var i = 0; i < AutoState.MaxPerDay; i++) Assert.True(state.Admit("pulse.deep_link", "s" + (i / 5), now));
            Assert.False(state.Admit("pulse.deep_link", "s", now));
            Assert.Equal(1, state.TakeDropped());
            Assert.True(state.Admit("pulse.heatmap", "s", now));
            Assert.Equal(0, state.TakeDropped());
        }

        [Fact]
        public async Task HeatmapSignalNeverCarriesDroppedAutomatic()
        {
            var rig = new Rig("{\"version\":1,\"heatmaps\":true,\"enabledSignals\":[\"pulse.app_opened\"]}");
            for (var i = 0; i < 25; i++) { rig.Runtime.ReportBackground(); rig.Runtime.ReportForeground(false); }
            rig.Runtime.Screen("A", false); rig.Tap(10, 10);
            var all = await rig.Sent();
            Assert.True(rig.Client.Auto.TakeDropped() >= 0);
            rig.Runtime.HeatmapFlush();
            var map = (await rig.Sent()).Single(e => e.GetProperty("type").GetString() == "pulse.heatmap");
            Assert.False(map.GetProperty("system").TryGetProperty("droppedAutomatic", out _));
            Assert.NotEmpty(all);
        }

        [Fact]
        public void ActiveAndScreenFollowTheRules()
        {
            var rig = new Rig();
            Assert.False(rig.Runtime.HeatmapActive); Assert.Null(rig.Runtime.HeatmapScreen);
            rig.Runtime.Screen("Home", false);
            Assert.True(rig.Runtime.HeatmapActive); Assert.Equal("Home", rig.Runtime.HeatmapScreen);
            Assert.False(new Rig("{\"version\":1}").Runtime.HeatmapActive);
        }

        // ---- screenshot ----

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

        [Fact]
        public void ReleaseBuildsNeverGetATicket()
        {
            var rig = new Rig(debug: false);
            rig.Runtime.Screen("Home", false);
            Assert.Null(rig.Runtime.HeatmapShotTicket(400, 800));
        }

        [Fact]
        public void DebugBuildNeedsHeatmapsOnAndAScreen()
        {
            var rig = new Rig(debug: true);
            Assert.Null(rig.Runtime.HeatmapShotTicket(400, 800));
            rig.Runtime.Screen("Home", false);
            var t = rig.Runtime.HeatmapShotTicket(400, 800);
            Assert.Equal(("Home", "phone", "portrait"), (t.Screen, t.Bucket, t.Orientation));
            Assert.Null(new Rig("{\"version\":1,\"heatmaps\":false}", debug: true).Runtime.HeatmapShotTicket(400, 800));
            var other = new Rig(On, new PulseOptions { CaptureHeatmaps = false }, true);
            other.Runtime.Screen("Home", false);
            Assert.Null(other.Runtime.HeatmapShotTicket(400, 800));
        }

        [Fact]
        public async Task UploadSendsTheRawImageWithTheRightRequest()
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Shop & Co", false);
            var ticket = rig.Runtime.HeatmapShotTicket(800, 400);
            rig.Transport.Reply(201);
            Assert.True(await rig.Runtime.HeatmapScreenshot(ticket, Png, "image/png"));
            var request = rig.Transport.Requests.Single();
            Assert.Equal("synthetic-key", request.AppKey);
            Assert.Equal(Png, request.Body); Assert.False(request.IsGzip); Assert.Equal("image/png", request.ContentType);
            Assert.Equal("/api/v1/heatmap/screenshot", request.Url.AbsolutePath);
            var query = request.Url.Query;
            Assert.Contains("screen=Shop%20%26%20Co", query); Assert.Contains("bucket=tablet", query);
            Assert.Contains("orientation=landscape", query); Assert.Contains("appVersion=1.2.3", query); Assert.Contains("debug=true", query);
        }

        [Fact]
        public async Task SuccessWritesAMarkerThatSurvivesARestart()
        {
            var storage = new MemoryStorage();
            var rig = new Rig(debug: true, storage: storage);
            rig.Runtime.Screen("Home", false);
            rig.Transport.Reply(201);
            await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png");
            Assert.Null(rig.Runtime.HeatmapShotTicket(400, 800));
            Assert.NotNull(rig.Runtime.HeatmapShotTicket(800, 400));
            var again = new Rig(debug: true, storage: storage);
            again.Runtime.Screen("Home", false);
            Assert.Null(again.Runtime.HeatmapShotTicket(400, 800));
        }

        [Fact]
        public async Task AnotherAppVersionNeedsANewPicture()
        {
            var storage = new MemoryStorage();
            var rig = new Rig(debug: true, storage: storage, options: null);
            rig.Runtime.Screen("Home", false);
            rig.Transport.Reply(201);
            await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png");
            var next = new Rig(On, new PulseOptions { AppVersion = "9.9.9" }, true, storage);
            next.Runtime.Screen("Home", false);
            Assert.NotNull(next.Runtime.HeatmapShotTicket(400, 800));
        }

        [Theory]
        [InlineData(403)] [InlineData(415)] [InlineData(413)] [InlineData(422)]
        public async Task PermanentRefusalsAreMarkedDoneAndNeverRetried(int status)
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            rig.Transport.Reply(status);
            Assert.False(await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png"));
            Assert.Null(rig.Runtime.HeatmapShotTicket(400, 800));
        }

        [Theory]
        [InlineData(429)] [InlineData(503)] [InlineData(507)] [InlineData(0)] [InlineData(500)]
        public async Task TransientFailuresAreRetriedOnALaterVisit(int status)
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            rig.Transport.Reply(status);
            Assert.False(await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png"));
            rig.Clock.Advance(3600);
            Assert.NotNull(rig.Runtime.HeatmapShotTicket(400, 800));
        }

        [Fact]
        public async Task RetryAfterIsHonoured()
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            rig.Transport.Responses.Enqueue(new TransportResponse(429, "", "120"));
            await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png");
            rig.Clock.Advance(119);
            Assert.Null(rig.Runtime.HeatmapShotTicket(400, 800));
            rig.Clock.Advance(2);
            Assert.NotNull(rig.Runtime.HeatmapShotTicket(400, 800));
        }

        [Fact]
        public async Task NeverMoreThanTwentyPerHour()
        {
            var rig = new Rig(debug: true);
            for (var i = 0; i < 20; i++)
            {
                rig.Runtime.Screen("S" + i, false);
                rig.Transport.Reply(201);
                Assert.True(await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png"));
                rig.Clock.Advance(60);
            }
            rig.Runtime.Screen("S20", false);
            Assert.Null(rig.Runtime.HeatmapShotTicket(400, 800));
            rig.Clock.Advance(3600);
            Assert.NotNull(rig.Runtime.HeatmapShotTicket(400, 800));
        }

        [Theory]
        [InlineData("image/svg+xml")] [InlineData("text/html")] [InlineData(null)] [InlineData("")]
        public async Task OnlyRasterTypesAreSent(string type)
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            Assert.False(await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, type));
            Assert.Empty(rig.Transport.Requests);
        }

        [Fact]
        public async Task EmptyAndOversizedImagesAreNotSentAndNotMarked()
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            var ticket = rig.Runtime.HeatmapShotTicket(400, 800);
            Assert.False(await rig.Runtime.HeatmapScreenshot(ticket, new byte[0], "image/png"));
            Assert.False(await rig.Runtime.HeatmapScreenshot(ticket, null, "image/png"));
            Assert.False(await rig.Runtime.HeatmapScreenshot(ticket, new byte[HeatmapShots.MaxBytes + 1], "image/png"));
            Assert.False(await rig.Runtime.HeatmapScreenshot(null, Png, "image/png"));
            Assert.Empty(rig.Transport.Requests);
            Assert.NotNull(rig.Runtime.HeatmapShotTicket(400, 800));
        }

        [Fact]
        public async Task UploadIsRefusedWhenHeatmapsAreOffOrOptedOut()
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            var ticket = rig.Runtime.HeatmapShotTicket(400, 800);
            rig.Runtime.OptOut = true;
            Assert.False(await rig.Runtime.HeatmapScreenshot(ticket, Png, "image/png"));
            Assert.Empty(rig.Transport.Requests);
            Assert.False(rig.Storage.Files.ContainsKey(HeatmapShots.FileName));
        }

        [Fact]
        public async Task OptOutForgetsTheMarkers()
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            rig.Transport.Reply(201);
            await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png");
            Assert.True(rig.Storage.Files.ContainsKey(HeatmapShots.FileName));
            rig.Runtime.OptOut = true;
            await Harness.Until(() => !rig.Storage.Files.ContainsKey(HeatmapShots.FileName));
        }

        [Fact]
        public async Task StorageTroubleNeverThrows()
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            rig.Storage.FailOnly = HeatmapShots.FileName;
            rig.Transport.Reply(201);
            var ticket = rig.Runtime.HeatmapShotTicket(400, 800);
            await rig.Runtime.HeatmapScreenshot(ticket, Png, "image/png");
        }

        [Fact]
        public async Task TransportExceptionsAreSilent()
        {
            var rig = new Rig(debug: true);
            rig.Runtime.Screen("Home", false);
            rig.Transport.Handler = (r, c) => throw new InvalidOperationException("boom");
            Assert.False(await rig.Runtime.HeatmapScreenshot(rig.Runtime.HeatmapShotTicket(400, 800), Png, "image/png"));
        }
    }
}
