using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    // AE1: automatic events, quota guards and the new fields.
    public sealed class AutoEventsTests
    {
        private const string OptIn = "{\"version\":1,\"enabledSignals\":[\"pulse.app_opened\",\"pulse.app_backgrounded\"]}";

        private sealed class Rig
        {
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly FakeTransport Transport = new FakeTransport();
            internal readonly MemoryStorage Storage;
            internal readonly PulseRuntime Runtime;
            internal PulseClient Client;
            private int seen;
            internal Rig(string config = null, PulseOptions options = null, MemoryStorage storage = null, bool usage = true, Func<DateTimeOffset, long?> launch = null)
            {
                Storage = storage ?? new MemoryStorage();
                if (config != null) Storage.Files["config"] = Encoding.UTF8.GetBytes(config);
                var tracker = usage ? new UsageTracker(() => Storage, t => t.UtcDateTime.ToString("yyyy-MM-dd")) : null;
                Runtime = new PulseRuntime(Clock);
                Runtime.InstallPlatform((k, o, optOut) =>
                {
                    Client = new PulseClient(k, o, Clock, Transport, Storage, new ExtrasEnvironment(), null, false, () => 0.5, optOut, tracker,
                        t => t.UtcDateTime.ToString("yyyy-MM-dd"));
                    Client.LaunchProvider = launch; return Client;
                }, null, () => PulseClient.ReadStoredOptOut(Storage));
                Runtime.Initialize("synthetic-key", options);
            }
            internal async Task<JsonElement[]> Sent()
            {
                await Client.Auto.Idle;
                await Client.ProcessAsync(true);
                var all = Harness.Events(Transport.Requests); var fresh = all.Skip(seen).ToArray(); seen = all.Length; return fresh;
            }
            internal async Task<JsonElement[]> Send(string type)
            { Runtime.Track(type); if (Client.Auto != null) await Client.Auto.Idle; return await Sent(); }
            internal void Foreground(bool cold = false) => Runtime.ReportForeground(cold);
            internal void Background() => Runtime.ReportBackground();
        }

        private static string[] Types(JsonElement[] e) => e.Select(x => x.GetProperty("type").GetString()).ToArray();
        private static string Prop(JsonElement e, string k) =>
            e.GetProperty("props").TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()) : null;
        private static string Sys(JsonElement e, string k) => e.GetProperty("system").TryGetProperty(k, out var v) ? v.GetString() : null;
        private static JsonElement Last(JsonElement[] e, string type) => e.Last(x => x.GetProperty("type").GetString() == type);

        // ---- config client ----

        [Fact]
        public void ConfigReadsEnabledAndPausedSignalsAndIgnoresTheRest()
        {
            var c = RemoteConfig.Parse("{\"enabledSignals\":[\"pulse.app_opened\",\"pulse.screen_view\",5,null],\"pausedSignals\":[\"pulse.screen_view\",7]," +
                "\"pauseExpensiveAt90\":true}");
            Assert.Equal(new[] { "pulse.app_opened" }, c.EnabledSignals.ToArray());
            Assert.Equal(new[] { "pulse.screen_view" }, c.PausedSignals.ToArray());
            Assert.True(c.IsEnabled("pulse.app_opened")); Assert.False(c.IsEnabled("pulse.app_backgrounded"));
            Assert.True(c.IsPaused("pulse.screen_view"));
        }

        [Theory]
        [InlineData("{\"enabledSignals\":\"pulse.app_opened\"}")] [InlineData("{\"enabledSignals\":{\"a\":1}}")] [InlineData("{\"pausedSignals\":3}")]
        public void WrongTypesMeanNothingEnabledOrPaused(string body)
        {
            var c = RemoteConfig.Parse(body);
            Assert.Empty(c.EnabledSignals); Assert.Empty(c.PausedSignals);
        }

        // ---- opt-in rule and portal switches ----

        [Fact]
        public async Task OptInEventsAreNotSentWithoutACachedListOrWithAFailedRead()
        {
            foreach (var rig in new[] { new Rig(), new Rig("{\"version\":1}"), new Rig("{broken") })
            {
                rig.Foreground(true); rig.Background();
                var types = Types(await rig.Sent());
                Assert.DoesNotContain("pulse.app_opened", types); Assert.DoesNotContain("pulse.app_backgrounded", types);
            }
            var unreadable = new Rig(storage: new MemoryStorage { FailOnly = "config" });
            unreadable.Foreground(true);
            Assert.DoesNotContain("pulse.app_opened", Types(await unreadable.Sent()));
        }

        [Fact]
        public async Task OptInEventsAreSentWhenListedWithTheirProps()
        {
            var rig = new Rig(OptIn);
            rig.Foreground(true); rig.Clock.Advance(42); rig.Background();
            var events = await rig.Sent();
            Assert.Equal("true", Prop(Last(events, "pulse.app_opened"), "coldStart"));
            Assert.Equal("42", Prop(Last(events, "pulse.app_backgrounded"), "foregroundSeconds"));
            rig.Foreground(false);
            Assert.Equal("false", Prop(Last(await rig.Sent(), "pulse.app_opened"), "coldStart"));
        }

        [Theory]
        [InlineData("{\"enabledSignals\":[\"pulse.app_opened\"],\"pausedSignals\":[\"pulse.app_opened\"]}")]
        [InlineData("{\"enabledSignals\":[\"pulse.app_opened\"],\"disabledSignals\":[\"pulse.app_opened\"]}")]
        [InlineData("{\"enabledSignals\":[\"pulse.app_opened\"],\"automaticSignals\":false}")]
        public async Task PausedDisabledAndAutomaticOffWinOverEnabled(string config)
        {
            var rig = new Rig(config); rig.Foreground(true);
            Assert.DoesNotContain("pulse.app_opened", Types(await rig.Sent()));
        }

        [Fact]
        public async Task CodeOptionAutomaticSignalsFalseSwitchesTheNewTypesOffToo()
        {
            var rig = new Rig(OptIn, new PulseOptions { AutomaticSignals = false });
            rig.Foreground(true); rig.Runtime.DeepLink("myapp://x/y"); rig.Runtime.ReportPermission("notification", "granted");
            rig.Runtime.ReportPermission("notification", "denied");
            Assert.All(Types(await rig.Sent()), t => Assert.False(t.StartsWith("pulse.app_o") || t.StartsWith("pulse.app_b") || t == "pulse.deep_link" || t == "pulse.permission_changed"));
        }

        [Fact]
        public async Task PausedScreenViewsAreNotSentButOtherSignalsAre()
        {
            var rig = new Rig("{\"pausedSignals\":[\"pulse.screen_view\"]}");
            rig.Runtime.Screen("A", false);
            var types = Types(await rig.Send("level"));
            Assert.DoesNotContain("pulse.screen_view", types); Assert.Contains("level", types);
            Assert.False(rig.Runtime.AutomaticScreenViewsEnabled);
        }

        // ---- transitions ----

        [Fact]
        public async Task EveryEventFiresOncePerRealTransition()
        {
            var rig = new Rig(OptIn);
            rig.Foreground(true); rig.Foreground(false); rig.Foreground(false);
            rig.Background(); rig.Background();
            rig.Foreground(false);
            var types = Types(await rig.Sent()).Where(t => t.StartsWith("pulse.app_")).ToArray();
            Assert.Equal(new[] { "pulse.app_opened", "pulse.app_backgrounded", "pulse.app_opened" }, types);
        }

        [Fact]
        public async Task BackgroundBeforeAnyForegroundReportCountsFromStart()
        {
            var rig = new Rig(OptIn); rig.Clock.Advance(9); rig.Background();
            Assert.Equal("9", Prop(Last(await rig.Sent(), "pulse.app_backgrounded"), "foregroundSeconds"));
        }

        [Fact]
        public async Task ForegroundCountersAreSystemFieldsOnEverySignal()
        {
            var rig = new Rig();
            rig.Foreground(true);
            var first = await rig.Send("x");
            Assert.Equal("1", Sys(Last(first, "pulse.session_started"), "foregroundsToday"));
            Assert.Null(Sys(Last(first, "pulse.session_started"), "previousForegroundSeconds"));
            rig.Clock.Advance(30); rig.Background(); rig.Clock.Advance(5); rig.Foreground();
            var e = (await rig.Send("later")).Last();
            Assert.Equal("2", Sys(e, "foregroundsToday")); Assert.Equal("30", Sys(e, "previousForegroundSeconds"));
        }

        [Fact]
        public async Task ForegroundCountResetsOnANewLocalDayAndSurvivesARestart()
        {
            var storage = new MemoryStorage(); var rig = new Rig(storage: storage);
            rig.Foreground(true); rig.Background(); rig.Foreground();
            Assert.Equal("2", Sys((await rig.Send("a")).Last(), "foregroundsToday"));
            await rig.Client.Auto.Idle;
            var second = new Rig(storage: storage); second.Clock.Advance(60);
            var e = (await second.Send("b")).Last();
            Assert.Equal("2", Sys(e, "foregroundsToday"));   // same day: the count is kept
            second.Clock.Advance(24 * 3600);
            Assert.Null(Sys((await second.Send("c")).Last(), "foregroundsToday"));  // new day, no foreground yet
            second.Foreground(true);
            Assert.Equal("1", Sys((await second.Send("d")).Last(), "foregroundsToday"));
        }

        [Fact]
        public async Task NoForegroundHooksMeansNoForegroundFields()
        {
            var e = (await new Rig().Send("x")).Last();
            Assert.Null(Sys(e, "foregroundsToday")); Assert.Null(Sys(e, "previousForegroundSeconds"));
            Assert.Null(Sys(e, "notificationPermission")); Assert.Null(Sys(e, "trackingPermission"));
        }

        [Fact]
        public async Task UsageGroupOffHidesTheForegroundFields()
        {
            var rig = new Rig("{\"groups\":{\"usage\":false}}");
            rig.Foreground(true); rig.Background();
            var e = (await rig.Send("x")).Last();
            Assert.Null(Sys(e, "foregroundsToday")); Assert.Null(Sys(e, "previousForegroundSeconds"));
        }

        // ---- time on screen ----

        [Fact]
        public async Task PreviousScreenSecondsExcludesBackgroundTime()
        {
            var rig = new Rig();
            rig.Runtime.Screen("A", false);
            rig.Clock.Advance(10); rig.Background(); rig.Clock.Advance(600); rig.Foreground(); rig.Clock.Advance(5);
            rig.Runtime.Screen("B", false);
            rig.Clock.Advance(7); rig.Runtime.Screen("C", false);
            var views = (await rig.Sent()).Where(e => e.GetProperty("type").GetString() == "pulse.screen_view").ToArray();
            Assert.Null(Prop(views[0], "previousScreenSeconds"));
            Assert.Equal("15", Prop(views[1], "previousScreenSeconds"));
            Assert.Equal("7", Prop(views[2], "previousScreenSeconds"));
        }

        [Fact]
        public async Task ScreenChangedInTheBackgroundCountsOnlyItsForegroundTime()
        {
            var rig = new Rig();
            rig.Foreground(true); rig.Runtime.Screen("A", false); rig.Clock.Advance(4); rig.Background();
            rig.Clock.Advance(100); rig.Runtime.Screen("B", false); rig.Clock.Advance(100); rig.Foreground(); rig.Clock.Advance(3);
            rig.Runtime.Screen("C", false);
            var views = (await rig.Sent()).Where(e => e.GetProperty("type").GetString() == "pulse.screen_view").ToArray();
            Assert.Equal("4", Prop(views[1], "previousScreenSeconds")); Assert.Equal("3", Prop(views[2], "previousScreenSeconds"));
        }

        // ---- permissions ----

        [Fact]
        public async Task PermissionChangeIsSentOnceAndTheStatusIsAFieldOnEverySignal()
        {
            var rig = new Rig();
            rig.Runtime.ReportPermission("notification", "notDetermined");
            rig.Runtime.ReportPermission("notification", "notDetermined");
            Assert.DoesNotContain("pulse.permission_changed", Types(await rig.Sent()));
            rig.Runtime.ReportPermission("notification", "granted");
            rig.Runtime.ReportPermission("notification", "granted");
            rig.Runtime.ReportPermission("tracking", "denied");
            var events = await rig.Sent();
            var change = Assert.Single(events, e => e.GetProperty("type").GetString() == "pulse.permission_changed");
            Assert.Equal("notification", Prop(change, "permission")); Assert.Equal("notDetermined", Prop(change, "from")); Assert.Equal("granted", Prop(change, "to"));
            var later = (await rig.Send("x")).Last();
            Assert.Equal("granted", Sys(later, "notificationPermission")); Assert.Equal("denied", Sys(later, "trackingPermission"));
        }

        [Fact]
        public async Task PermissionStatusSurvivesARestartSoTheNextChangeIsSeen()
        {
            var storage = new MemoryStorage(); var rig = new Rig(storage: storage);
            rig.Runtime.ReportPermission("notification", "denied"); await rig.Sent(); await rig.Client.Auto.Idle;
            var next = new Rig(storage: storage);
            next.Runtime.ReportPermission("notification", "denied");
            Assert.DoesNotContain("pulse.permission_changed", Types(await next.Sent()));
            next.Runtime.ReportPermission("notification", "granted");
            Assert.Contains("pulse.permission_changed", Types(await next.Sent()));
        }

        [Theory]
        [InlineData("notification", "maybe")] [InlineData("camera", "granted")] [InlineData(null, "granted")] [InlineData("notification", null)]
        [InlineData("tracking", "granted")]
        public async Task InvalidPermissionReportsAreIgnored(string name, string status)
        {
            var rig = new Rig();
            rig.Runtime.ReportPermission("notification", "denied"); await rig.Sent();
            rig.Runtime.ReportPermission(name, status);
            Assert.DoesNotContain("pulse.permission_changed", Types(await rig.Sent()));
            var e = (await rig.Send("x")).Last();
            Assert.Equal("denied", Sys(e, "notificationPermission")); Assert.Null(Sys(e, "trackingPermission"));
        }

        [Fact]
        public async Task PermissionsGroupOffHidesBothFieldsButTheChangeStillGoesOut()
        {
            var rig = new Rig("{\"groups\":{\"permissions\":false}}");
            rig.Runtime.ReportPermission("notification", "denied"); rig.Runtime.ReportPermission("notification", "granted");
            var events = await rig.Sent();
            Assert.Contains("pulse.permission_changed", Types(events));
            Assert.All(events, e => { Assert.Null(Sys(e, "notificationPermission")); Assert.Null(Sys(e, "trackingPermission")); });
        }

        [Fact]
        public async Task PermissionChangesAreCappedAtFivePerSession()
        {
            var rig = new Rig(); var states = new[] { "granted", "denied" };
            for (var i = 0; i < 12; i++) rig.Runtime.ReportPermission("notification", states[i % 2]);
            Assert.Equal(5, Types(await rig.Sent()).Count(t => t == "pulse.permission_changed"));
        }

        // ---- deep links ----

        [Fact]
        public async Task DeepLinkSendsSchemeHostPathAndCampaignOnly()
        {
            var rig = new Rig();
            rig.Runtime.DeepLink("HTTPS://Example.invalid:8443/item/12345/ab/0b8a2f3e-1c4d-4e5f-8a9b-0c1d2e3f4a5b?utm_source=news&utm_medium=mail&utm_campaign=fall&token=SECRET&email=a@b.co#frag");
            var e = Last(await rig.Sent(), "pulse.deep_link");
            Assert.Equal("https", Prop(e, "scheme")); Assert.Equal("example.invalid", Prop(e, "host")); Assert.Equal("/item/:id/ab/:id", Prop(e, "path"));
            Assert.Equal("news", Prop(e, "utmSource")); Assert.Equal("mail", Prop(e, "utmMedium")); Assert.Equal("fall", Prop(e, "utmCampaign"));
            var raw = e.GetProperty("props").GetRawText();
            Assert.DoesNotContain("SECRET", raw); Assert.DoesNotContain("token", raw); Assert.DoesNotContain("frag", raw); Assert.DoesNotContain("a@b", raw);
            Assert.Equal(6, e.GetProperty("props").EnumerateObject().Count());
        }

        [Fact]
        public void DeepLinkPropsHandleOddInput()
        {
            Assert.Null(DeepLinkProps.From(null)); Assert.Null(DeepLinkProps.From("")); Assert.Null(DeepLinkProps.From("no scheme")); Assert.Null(DeepLinkProps.From("1x://a"));
            var custom = DeepLinkProps.From("myapp:///open/7");
            Assert.Equal("myapp", custom["scheme"]); Assert.Equal("", custom["host"]); Assert.Equal("/open/:id", custom["path"]);
            var plain = DeepLinkProps.From("myapp://home");
            Assert.Equal("home", plain["host"]); Assert.Equal("", plain["path"]); Assert.False(plain.ContainsKey("utmSource"));
            var user = DeepLinkProps.From("myapp://user:pw@example.invalid/a");
            Assert.Equal("example.invalid", user["host"]);
            var long1 = DeepLinkProps.From("https://example.invalid/" + new string('a', 300) + "?utm_source=" + new string('b', 300));
            Assert.Equal(128, ((string)long1["path"]).Length); Assert.Equal(64, ((string)long1["utmSource"]).Length);
            var mail = DeepLinkProps.From("https://example.invalid/user/a@b.co");
            Assert.DoesNotContain("a@b", (string)mail["path"]);
        }

        [Fact]
        public async Task DeepLinkRespectsPortalSwitchesAndInvalidInput()
        {
            var off = new Rig("{\"disabledSignals\":[\"pulse.deep_link\"]}"); off.Runtime.DeepLink("myapp://x");
            Assert.DoesNotContain("pulse.deep_link", Types(await off.Sent()));
            var paused = new Rig("{\"pausedSignals\":[\"pulse.deep_link\"]}"); paused.Runtime.DeepLink("myapp://x");
            Assert.DoesNotContain("pulse.deep_link", Types(await paused.Sent()));
            var rig = new Rig(); rig.Runtime.DeepLink("garbage"); rig.Runtime.DeepLink(null);
            Assert.DoesNotContain("pulse.deep_link", Types(await rig.Sent()));
        }

        [Fact]
        public async Task DeepLinksAreCappedAtTenPerSession()
        {
            var rig = new Rig();
            for (var i = 0; i < 14; i++) rig.Runtime.DeepLink("myapp://x/" + i);
            Assert.Equal(10, Types(await rig.Sent()).Count(t => t == "pulse.deep_link"));
        }

        // ---- opt-out and debug ----

        [Fact]
        public async Task OptOutSendsNothingAndDeletesTheStateFile()
        {
            var rig = new Rig(OptIn);
            rig.Foreground(true); rig.Runtime.ReportPermission("notification", "granted"); await rig.Sent(); await rig.Client.Auto.Idle;
            Assert.True(rig.Storage.Files.ContainsKey("auto"));
            rig.Runtime.OptOut = true; await rig.Client.Auto.Idle;
            rig.Foreground(false); rig.Background(); rig.Runtime.DeepLink("myapp://x"); rig.Runtime.ReportPermission("notification", "denied");
            Assert.Empty(await rig.Sent());
            Assert.False(rig.Storage.Files.ContainsKey("auto"));
        }

        [Fact]
        public async Task DebugFlagMarksTheNewSignalsAsTestData()
        {
            var rig = new Rig(OptIn, new PulseOptions { Debug = true });
            rig.Foreground(true); rig.Runtime.DeepLink("myapp://x");
            var events = await rig.Sent();
            Assert.All(events.Where(e => e.GetProperty("type").GetString() != "x"), e => Assert.True(e.GetProperty("debug").GetBoolean()));
        }

        // ---- launch time ----

        [Fact]
        public async Task LaunchMsIsOnlyOnTheColdStartSessionStarted()
        {
            var rig = new Rig(launch: t => 1234);
            var first = await rig.Send("a");
            Assert.Equal("1234", Sys(Last(first, "pulse.session_started"), "launchMs"));
            Assert.Null(Sys(Last(first, "a"), "launchMs"));
            rig.Clock.Advance(3600);
            var second = await rig.Send("b");
            Assert.Null(Sys(Last(second, "pulse.session_started"), "launchMs"));
        }

        [Fact]
        public async Task PlatformSuppliedLaunchTimeWinsAndNothingIsSentWithoutOne()
        {
            var rig = new Rig(launch: t => 5000); rig.Runtime.SetLaunchMs(800);
            Assert.Equal("800", Sys(Last(await rig.Send("a"), "pulse.session_started"), "launchMs"));
            var none = await new Rig().Send("a");
            Assert.Null(Sys(Last(none, "pulse.session_started"), "launchMs"));
        }

        [Fact]
        public async Task ADisabledColdStartSessionStartedDoesNotMakeALaterOneCold()
        {
            var rig = new Rig("{\"disabledSignals\":[\"pulse.session_started\"]}", launch: t => 10);
            await rig.Send("a"); rig.Clock.Advance(3600);
            Assert.DoesNotContain("pulse.session_started", Types(await rig.Send("b")));
        }

        // ---- quota guards ----

        [Fact]
        public async Task AppOpenedAndBackgroundedAreCappedAtTwentyPerSessionAndTheDropCountIsSentOnce()
        {
            var rig = new Rig(OptIn);
            for (var i = 0; i < 25; i++) { rig.Foreground(i == 0); rig.Background(); }
            var events = await rig.Sent();
            Assert.Equal(20, Types(events).Count(t => t == "pulse.app_opened"));
            Assert.Equal(20, Types(events).Count(t => t == "pulse.app_backgrounded"));
            var next = (await rig.Send("manual")).Single();
            Assert.Equal("10", Sys(next, "droppedAutomatic"));
            Assert.Null(Sys((await rig.Send("again")).Single(), "droppedAutomatic"));
        }

        [Fact]
        public async Task DroppedCountRidesTheNextSignalOfAnyKindAndOnlyOnce()
        {
            var rig = new Rig();
            for (var i = 0; i < 12; i++) rig.Runtime.DeepLink("myapp://x/" + i);
            var events = await rig.Sent();
            var withDrop = events.Where(e => Sys(e, "droppedAutomatic") != null).ToArray();
            Assert.Empty(withDrop);                           // the drops came after the last admitted signal
            var e2 = (await rig.Send("m")).Single();
            Assert.Equal("2", Sys(e2, "droppedAutomatic"));
            Assert.Null(Sys((await rig.Send("n")).Single(), "droppedAutomatic"));
        }

        [Fact]
        public async Task SessionCountsStartOverInANewSession()
        {
            var rig = new Rig();
            for (var i = 0; i < 12; i++) rig.Runtime.DeepLink("myapp://x/" + i);
            await rig.Sent(); rig.Clock.Advance(31 * 60);
            for (var i = 0; i < 4; i++) rig.Runtime.DeepLink("myapp://y/" + i);
            Assert.Equal(4, Types(await rig.Sent()).Count(t => t == "pulse.deep_link"));
        }

        // ---- a stale background never opens a session ----

        private static int AutoDayCount(MemoryStorage storage) =>
            JsonDocument.Parse(Encoding.UTF8.GetString(storage.Files["auto"])).RootElement.GetProperty("count").GetInt32();

        [Fact]
        public async Task BackgroundAfterThirtyMinutesOfSilenceIsDroppedAndOpensNoSession()
        {
            var rig = new Rig(OptIn);
            rig.Foreground(true);
            Assert.Contains("pulse.session_started", Types(await rig.Sent()));
            rig.Clock.Advance(30 * 60); rig.Background();
            var stale = await rig.Sent();
            Assert.DoesNotContain("pulse.app_backgrounded", Types(stale));
            Assert.DoesNotContain("pulse.session_started", Types(stale));
            Assert.Empty(stale);
            // The foreground counters still moved: the finished period is "previous" and the next foreground counts.
            rig.Clock.Advance(5); rig.Foreground();
            var after = await rig.Sent();
            Assert.Equal(new[] { "pulse.session_started", "pulse.app_opened" }, Types(after));
            Assert.Equal("2", Sys(Last(after, "pulse.app_opened"), "foregroundsToday"));
            Assert.Equal("1800", Sys(Last(after, "pulse.app_opened"), "previousForegroundSeconds"));
        }

        [Fact]
        public async Task BackgroundJustUnderThirtyMinutesIsStillSent()
        {
            var rig = new Rig(OptIn);
            rig.Foreground(true); await rig.Sent();
            rig.Clock.Advance(30 * 60 - 1); rig.Background();
            Assert.Contains("pulse.app_backgrounded", Types(await rig.Sent()));
        }

        [Fact]
        public async Task ADroppedBackgroundIsNeitherCountedTowardTheCapsNorAsDroppedAutomatic()
        {
            var rig = new Rig(OptIn);
            rig.Foreground(true); await rig.Sent(); await rig.Client.Auto.Idle;
            var before = AutoDayCount(rig.Storage);
            rig.Clock.Advance(31 * 60); rig.Background();
            Assert.Empty(await rig.Sent()); await rig.Client.Auto.Idle;
            Assert.Equal(before, AutoDayCount(rig.Storage));
            Assert.Equal(0, rig.Client.Auto.TakeDropped());
            Assert.Null(Sys((await rig.Send("m")).Last(), "droppedAutomatic"));
        }

        [Fact]
        public async Task ScreenViewsAreCappedAtOneHundredPerSession()
        {
            var rig = new Rig();
            for (var i = 0; i < 110; i++) rig.Runtime.Screen("S" + i, true);
            Assert.Equal(100, Types(await rig.Sent()).Count(t => t == "pulse.screen_view"));
        }

        [Fact]
        public async Task DailyTotalIsCappedAtThreeHundredAutomaticSignalsAndRollsOverWithTheDay()
        {
            var rig = new Rig(usage: false);
            // Four sessions of 100 screen views would be 400; the day allows 300.
            for (var s = 0; s < 4; s++)
            {
                for (var i = 0; i < 100; i++) rig.Runtime.Screen("S" + s + "_" + i, true);
                rig.Clock.Advance(31 * 60);
            }
            var events = await rig.Sent();
            Assert.Equal(300, Types(events).Count(t => t == "pulse.screen_view"));
            Assert.Equal("100", Sys((await rig.Send("m")).Single(), "droppedAutomatic"));
            rig.Clock.Advance(24 * 3600);
            rig.Runtime.Screen("Next", true);
            Assert.Contains("pulse.screen_view", Types(await rig.Sent()));
        }

        [Fact]
        public async Task DailyCountSurvivesARestart()
        {
            var storage = new MemoryStorage(); var rig = new Rig(storage: storage, usage: false);
            for (var s = 0; s < 3; s++)
            {
                for (var i = 0; i < 100; i++) rig.Runtime.Screen("S" + s + "_" + i, true);
                rig.Clock.Advance(31 * 60);
            }
            await rig.Sent(); await rig.Client.Auto.Idle;
            var next = new Rig(storage: storage, usage: false); next.Clock.Advance(3600 * 2);
            next.Runtime.Screen("X", true);
            Assert.DoesNotContain("pulse.screen_view", Types(await next.Sent()));
        }

        [Fact]
        public async Task ManualSignalsAndErrorsAreNeverCapped()
        {
            var rig = new Rig(usage: false);
            for (var i = 0; i < 350; i++) rig.Runtime.Track("level_done");
            for (var i = 0; i < 5; i++) rig.Runtime.TrackError(new InvalidOperationException("x"), null);
            var types = Types(await rig.Sent());
            Assert.Equal(350, types.Count(t => t == "level_done")); Assert.Equal(5, types.Count(t => t == "pulse.error"));
        }

        [Fact]
        public async Task DroppedByPortalIsNotCountedAsDroppedByTheGuard()
        {
            var rig = new Rig("{\"disabledSignals\":[\"pulse.deep_link\"]}");
            for (var i = 0; i < 30; i++) rig.Runtime.DeepLink("myapp://x/" + i);
            await rig.Sent();
            Assert.Null(Sys((await rig.Send("m")).Single(), "droppedAutomatic"));
        }

        [Fact]
        public async Task BrokenStorageKeepsTheGuardsWorkingInMemory()
        {
            var rig = new Rig(storage: new MemoryStorage { FailOnly = "auto" }, usage: false);
            for (var i = 0; i < 12; i++) rig.Runtime.DeepLink("myapp://x/" + i);
            Assert.Equal(10, Types(await rig.Sent()).Count(t => t == "pulse.deep_link"));
        }
    }
}
