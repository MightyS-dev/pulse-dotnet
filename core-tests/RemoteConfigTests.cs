using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class RemoteConfigTests
    {
        private const string AllOnBody = "{\"version\":3,\"screenViews\":true,\"automaticSignals\":true," +
            "\"groups\":{\"device\":true,\"screen\":true,\"settings\":true,\"network\":true,\"app\":true,\"usage\":true},\"disabledSignals\":[]}";

        private static Func<TransportRequest, CancellationToken, Task<TransportResponse>> Reply(int status, string body = "") =>
            (r, c) => Task.FromResult(new TransportResponse(status, body));

        private static MemoryStorage Cached(string body)
        { var s = new MemoryStorage(); s.Files["config"] = Encoding.UTF8.GetBytes(body); return s; }

        // An environment that gives every platform key in the contract, static and dynamic, with valid values.
        private static ExtrasEnvironment FullEnvironment()
        {
            var env = new ExtrasEnvironment();
            env.Static = new Dictionary<string, string>
            {
                ["deviceType"] = "phone", ["deviceBrand"] = "Acme", ["architecture"] = "arm64", ["osApiLevel"] = "34",
                ["runtimeVersion"] = "9.0", ["scriptingBackend"] = "il2cpp", ["isSimulator"] = "false", ["targetEnvironment"] = "native",
                ["extensionId"] = "ext", ["screenWidth"] = "393", ["screenHeight"] = "852", ["screenScale"] = "3", ["screenWidthPx"] = "1179",
                ["screenHeightPx"] = "2556", ["screenDpi"] = "460", ["screenCount"] = "1", ["appName"] = "Demo", ["installSource"] = "appstore",
                ["isDebugBuild"] = "false", ["language"] = "en", ["preferredLanguage"] = "en-US", ["region"] = "US", ["layoutDirection"] = "ltr",
                ["timeZone"] = "Europe/Belgrade", ["utcOffsetMinutes"] = "60"
            };
            env.Dynamic = new Dictionary<string, string>
            {
                ["orientation"] = "portrait", ["colorScheme"] = "dark", ["fontScale"] = "1", ["contentSizeCategory"] = "large",
                ["reduceMotion"] = "false", ["boldText"] = "false", ["invertColors"] = "false", ["increaseContrast"] = "false",
                ["reduceTransparency"] = "false", ["differentiateWithoutColor"] = "false", ["fontWeightAdjustment"] = "0",
                ["connectionType"] = "wifi", ["lowPowerMode"] = "false"
            };
            return env;
        }

        private static string[] SystemKeys(JsonElement e) => e.GetProperty("system").EnumerateObject().Select(p => p.Name).ToArray();

        // ---- parsing ----

        [Fact]
        public void EmptyObjectMeansEverythingOn()
        {
            var c = RemoteConfig.Parse("{}");
            Assert.NotNull(c); Assert.True(c.ScreenViews); Assert.True(c.AutomaticSignals); Assert.Null(c.Version);
            Assert.Empty(c.HiddenKeys()); Assert.Empty(c.DisabledSignals); Assert.Null(c.ETag);
        }

        [Fact]
        public void FullBodyIsReadAndUnknownKeysAreIgnored()
        {
            var c = RemoteConfig.Parse("{\"version\":7,\"screenViews\":false,\"automaticSignals\":false,\"future\":{\"x\":1}," +
                "\"groups\":{\"network\":false,\"mystery\":false},\"disabledSignals\":[\"pulse.app_updated\",\"pulse.unknown\",5,null]}");
            Assert.Equal(7, c.Version); Assert.Equal("\"7\"", c.ETag);
            Assert.False(c.ScreenViews); Assert.False(c.AutomaticSignals);
            Assert.False(c.GroupOn("network")); Assert.True(c.GroupOn("device")); Assert.True(c.GroupOn("mystery"));
            Assert.Equal(new[] { "pulse.app_updated" }, c.DisabledSignals.ToArray());
        }

        [Theory]
        [InlineData("{\"screenViews\":\"no\"}")] [InlineData("{\"screenViews\":0}")] [InlineData("{\"screenViews\":null}")]
        [InlineData("{\"screenViews\":[false]}")] [InlineData("{\"groups\":false}")] [InlineData("{\"groups\":{\"device\":\"off\"}}")]
        [InlineData("{\"groups\":{\"device\":0,\"usage\":null}}")] [InlineData("{\"disabledSignals\":\"pulse.screen_view\"}")]
        [InlineData("{\"disabledSignals\":{\"a\":1}}")] [InlineData("{\"automaticSignals\":{}}")]
        public void WrongTypesMeanOn(string body)
        {
            var c = RemoteConfig.Parse(body);
            Assert.NotNull(c); Assert.True(c.ScreenViews); Assert.True(c.AutomaticSignals);
            Assert.Empty(c.HiddenKeys()); Assert.Empty(c.DisabledSignals);
        }

        [Theory]
        [InlineData("{\"version\":-1}")] [InlineData("{\"version\":1.5}")] [InlineData("{\"version\":\"3\"}")]
        [InlineData("{\"version\":99999999999}")] [InlineData("{\"version\":true}")]
        public void BadVersionIsNoVersion(string body) => Assert.Null(RemoteConfig.Parse(body).Version);

        [Theory]
        [InlineData("")] [InlineData("   ")] [InlineData("not json")] [InlineData("{")] [InlineData("[]")] [InlineData("\"x\"")]
        [InlineData("null")] [InlineData("42")] [InlineData("{\"a\":1,\"a\":2}")] [InlineData("{} trailing")]
        public void UnusableBodyIsNullAndNeverThrows(string body) => Assert.Null(RemoteConfig.Parse(body));

        [Fact]
        public void OversizedBodyIsRejected() =>
            Assert.Null(RemoteConfig.Parse("{\"pad\":\"" + new string('x', 70000) + "\"}"));

        [Fact]
        public void GroupsCoverTheAllowlistExactlyOnce()
        {
            var all = RemoteConfig.GroupNames.SelectMany(RemoteConfig.KeysOf).ToList();
            Assert.Equal(46, all.Count);
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Equal(SystemFields.Keys.OrderBy(k => k, StringComparer.Ordinal), all.OrderBy(k => k, StringComparer.Ordinal));
            Assert.DoesNotContain("bundleId", all);
        }

        [Fact]
        public void LoadMissingDamagedAndUnreadableFilesAreAllOn()
        {
            Assert.Same(RemoteConfig.AllOn, RemoteConfig.Load(new MemoryStorage()));
            Assert.Same(RemoteConfig.AllOn, RemoteConfig.Load(Cached("{broken")));
            Assert.Same(RemoteConfig.AllOn, RemoteConfig.Load(new MemoryStorage { Fail = true }));
            Assert.Equal(3, RemoteConfig.Load(Cached(AllOnBody)).Version);
        }

        [Fact]
        public void OptionsKeepTheirPublicDefaultsAndSnapshotCopiesThem()
        {
            var plain = new PulseOptions();
            Assert.True(plain.AutomaticScreenViews); Assert.True(plain.AutomaticSignals);
            var set = new PulseOptions { AutomaticScreenViews = false, AutomaticSignals = false }.Snapshot();
            Assert.False(set.AutomaticScreenViews); Assert.False(set.AutomaticSignals);
        }

        // ---- fetch ----

        [Fact]
        public async Task FetchesOncePerLaunchWithKeyAndNoValidatorWhenNothingIsCached()
        {
            var rig = new UsageRig(withUsage: false, configHandler: Reply(200, AllOnBody));
            await rig.Client.ConfigFetch;
            await rig.Send(); await rig.Send();
            var request = Assert.Single(rig.Transport.ConfigRequests);
            Assert.True(request.IsGet); Assert.Null(request.Body); Assert.Null(request.IfNoneMatch);
            Assert.Equal("https://in.mightys.dev/api/v1/config", request.Url.AbsoluteUri);
            Assert.Equal("synthetic-key", request.AppKey); Assert.Equal(10, request.TimeoutSeconds);
            Assert.Equal(AllOnBody, Encoding.UTF8.GetString(rig.Storage.Files["config"]));
        }

        [Fact]
        public async Task SendsTheCachedVersionAsIfNoneMatchAndKeepsTheCacheOn304()
        {
            var storage = Cached(AllOnBody);
            var rig = new UsageRig(storage, withUsage: false, configHandler: Reply(304));
            await rig.Client.ConfigFetch;
            Assert.Equal("\"3\"", Assert.Single(rig.Transport.ConfigRequests).IfNoneMatch);
            Assert.Equal(AllOnBody, Encoding.UTF8.GetString(storage.Files["config"]));
        }

        [Theory]
        [InlineData(401)] [InlineData(404)] [InlineData(429)] [InlineData(500)] [InlineData(503)] [InlineData(0)] [InlineData(204)]
        public async Task AnyOtherStatusKeepsTheCache(int status)
        {
            const string off = "{\"version\":3,\"screenViews\":false}";
            var storage = Cached(off);
            var rig = new UsageRig(storage, withUsage: false, configHandler: Reply(status, AllOnBody));
            await rig.Client.ConfigFetch;
            Assert.Equal(off, Encoding.UTF8.GetString(storage.Files["config"]));
            Assert.False(rig.Client.Policy.ScreenViewsAllowed);
        }

        [Fact]
        public async Task ThrowingTransportKeepsTheCacheAndNeverRetriesInThisLaunch()
        {
            const string off = "{\"version\":3,\"screenViews\":false}";
            var storage = Cached(off);
            var rig = new UsageRig(storage, withUsage: false, configHandler: (r, c) => throw new InvalidOperationException("synthetic"));
            await rig.Client.ConfigFetch; await rig.Send(); await rig.Send();
            Assert.Single(rig.Transport.ConfigRequests);
            Assert.Equal(off, Encoding.UTF8.GetString(storage.Files["config"]));
        }

        [Theory]
        [InlineData("not json")] [InlineData("[1,2]")] [InlineData("")]
        public async Task BadBodyOn200KeepsTheCacheAndStoresNothingNew(string body)
        {
            var empty = new UsageRig(withUsage: false, configHandler: Reply(200, body));
            await empty.Client.ConfigFetch;
            Assert.False(empty.Storage.Files.ContainsKey("config"));
            const string off = "{\"version\":3,\"screenViews\":false}";
            var storage = Cached(off);
            var rig = new UsageRig(storage, withUsage: false, configHandler: Reply(200, body));
            await rig.Client.ConfigFetch;
            Assert.Equal(off, Encoding.UTF8.GetString(storage.Files["config"]));
        }

        [Fact]
        public async Task NoCacheAndFailedFetchMeansEverythingOn()
        {
            var rig = new UsageRig(withUsage: false, configHandler: Reply(500));
            await rig.Client.ConfigFetch;
            var events = await rig.Send();
            Assert.True(rig.Client.Policy.ScreenViewsAllowed); Assert.True(rig.Client.Policy.Allows("pulse.session_started"));
            Assert.Single(events);
        }

        [Fact]
        public async Task FetchedSettingsApplyFromTheNextLaunchOnly()
        {
            var off = "{\"version\":4,\"screenViews\":false,\"automaticSignals\":false,\"groups\":{\"device\":false}}";
            var first = new UsageRig(environment: FullEnvironment(), configHandler: Reply(200, off));
            await first.Client.ConfigFetch;
            var events = await first.Send();
            Assert.Contains("pulse.session_started", UsageRig.Types(events));     // this run is still all on
            Assert.Contains("deviceType", SystemKeys(events[0]));
            first.Runtime.Screen("Home", false); await first.Client.ProcessAsync(true);
            Assert.Contains(Harness.Events(first.Transport.Requests), e => e.GetProperty("type").GetString() == "pulse.screen_view");

            var second = new UsageRig(first.Storage, FullEnvironment(), configHandler: Reply(304));
            await second.Client.ConfigFetch;
            Assert.Equal("\"4\"", Assert.Single(second.Transport.ConfigRequests).IfNoneMatch);
            var again = await second.Send();
            Assert.Equal(new[] { "test" }, UsageRig.Types(again));
            Assert.DoesNotContain("deviceType", SystemKeys(again[0]));
        }

        // ---- precedence (code can only switch off) ----

        private static SignalPolicy Policy(bool codeScreens, bool codeSignals, string portal) =>
            new SignalPolicy(new PulseOptions { AutomaticScreenViews = codeScreens, AutomaticSignals = codeSignals }.Snapshot(),
                portal == null ? RemoteConfig.AllOn : RemoteConfig.Parse(portal));

        [Fact]
        public void ScreenViewNeedsPortalScreenViewsAutomaticSignalsAndNotDisabled()
        {
            Assert.True(Policy(true, true, null).ScreenViewsAllowed);
            Assert.False(Policy(true, true, "{\"screenViews\":false}").ScreenViewsAllowed);
            Assert.False(Policy(true, true, "{\"automaticSignals\":false}").ScreenViewsAllowed);
            Assert.False(Policy(true, true, "{\"disabledSignals\":[\"pulse.screen_view\"]}").ScreenViewsAllowed);
            Assert.False(Policy(true, true, "{\"screenViews\":false}").Allows("pulse.screen_view"));
        }

        [Fact]
        public void ManualScreenCallIgnoresCodeOptionButAutomaticHookObeysIt()
        {
            var codeOff = Policy(false, true, null);
            Assert.True(codeOff.Allows("pulse.screen_view"));          // manual path: portal only
            Assert.False(codeOff.AutomaticScreenHook);                 // hook: code option off wins
            Assert.True(Policy(true, true, null).AutomaticScreenHook);
            Assert.False(Policy(true, true, "{\"screenViews\":false}").AutomaticScreenHook);
            Assert.False(Policy(false, true, "{\"screenViews\":false}").Allows("pulse.screen_view"));
        }

        [Fact]
        public void CodeAutomaticSignalsDoesNotAffectScreenViews()
        {
            var p = Policy(true, false, null);
            Assert.True(p.ScreenViewsAllowed); Assert.True(p.AutomaticScreenHook); Assert.True(p.Allows("pulse.screen_view"));
        }

        [Fact]
        public void OtherThreeAutomaticSignalsNeedPortalAutomaticNotDisabledAndCodeNotOff()
        {
            foreach (var type in new[] { "pulse.session_started", "pulse.new_install", "pulse.app_updated" })
            {
                Assert.True(Policy(true, true, null).Allows(type));
                Assert.False(Policy(true, true, "{\"automaticSignals\":false}").Allows(type));
                Assert.False(Policy(true, true, "{\"disabledSignals\":[\"" + type + "\"]}").Allows(type));
                Assert.False(Policy(true, false, null).Allows(type));
                Assert.True(Policy(true, true, "{\"screenViews\":false}").Allows(type));
            }
        }

        [Fact]
        public void CodeTrueNeverForcesPortalOffSignalsOn()
        {
            var p = Policy(true, true, "{\"screenViews\":false,\"automaticSignals\":false}");
            Assert.False(p.AutomaticScreenHook); Assert.False(p.Allows("pulse.session_started")); Assert.False(p.Allows("pulse.screen_view"));
            Assert.True(p.Allows("level_done"));
        }

        [Fact]
        public async Task CodeAutomaticSignalsOffDropsThreeTypesButKeepsScreenViewsAndOtherSignals()
        {
            var rig = new UsageRig(options: new PulseOptions { AutomaticSignals = false });
            rig.Runtime.Screen("Home", false);
            var types = UsageRig.Types(await rig.Send("test"));
            Assert.DoesNotContain("pulse.session_started", types); Assert.DoesNotContain("pulse.new_install", types);
            Assert.Contains("pulse.screen_view", types); Assert.Contains("test", types);
        }

        [Fact]
        public async Task CodeScreenOptionOffStopsHookButManualStillSent()
        {
            var rig = new UsageRig(options: new PulseOptions { AutomaticScreenViews = false }, withUsage: false);
            rig.Runtime.Screen("Auto", true); rig.Runtime.Screen("Manual", false);
            await rig.Client.ProcessAsync(true);
            var screens = Harness.Events(rig.Transport.Requests).Where(e => e.GetProperty("type").GetString() == "pulse.screen_view").ToArray();
            Assert.Equal("Manual", Assert.Single(screens).GetProperty("props").GetProperty("screen").GetString());
            Assert.False(rig.Runtime.AutomaticScreenViewsEnabled);
        }

        // ---- effects ----

        [Fact]
        public async Task ScreenViewsOffStopsHookAndManualCalls()
        {
            var rig = new UsageRig(Cached("{\"version\":1,\"screenViews\":false}"), withUsage: false);
            Assert.False(rig.Runtime.AutomaticScreenViewsEnabled);
            rig.Runtime.Screen("Auto", true); rig.Runtime.Screen("Manual", false);
            await rig.Client.ProcessAsync(true);
            Assert.Empty(Harness.Events(rig.Transport.Requests));
            Assert.Equal(new[] { "test" }, UsageRig.Types(await rig.Send()));
        }

        [Fact]
        public async Task AutomaticSignalsOffDropsTheFourTypesButKeepsEverythingElse()
        {
            var rig = new UsageRig(Cached("{\"version\":1,\"automaticSignals\":false}"));
            rig.Runtime.Screen("Home", false);
            var events = await rig.Send("level_done");
            Assert.Equal(new[] { "level_done" }, UsageRig.Types(events));
            Assert.Contains("totalSessionsCount", SystemKeys(events[0]));
            Assert.False(rig.Runtime.AutomaticScreenViewsEnabled);
        }

        [Theory]
        [InlineData("pulse.session_started")] [InlineData("pulse.new_install")] [InlineData("pulse.screen_view")]
        public async Task OneDisabledTypeIsNotSentAndTheOthersAre(string type)
        {
            var rig = new UsageRig(Cached("{\"version\":2,\"disabledSignals\":[\"" + type + "\"]}"));
            rig.Runtime.Screen("Home", false);
            var sent = UsageRig.Types(await rig.Send("test"));
            Assert.DoesNotContain(type, sent); Assert.Contains("test", sent);
            foreach (var other in RemoteConfig.AutomaticTypes.Where(t => t != type && t != "pulse.app_updated"))
                Assert.Contains(other, sent);
        }

        [Fact]
        public async Task DisabledAppUpdatedIsNotSent()
        {
            var storage = new MemoryStorage();
            // First launch writes the usage file with the current version; the second launch sees a newer build.
            var first = new UsageRig(storage, new VersionedEnvironment("1.0", "1"));
            await first.Send(); await first.Usage.Idle;
            storage.Files["config"] = Encoding.UTF8.GetBytes("{\"version\":1,\"disabledSignals\":[\"pulse.app_updated\"]}");
            var clock = first.Clock; clock.Advance(3600 * 24);
            var second = new UsageRig(storage, new VersionedEnvironment("1.1", "2"));
            var types = UsageRig.Types(await second.Send());
            Assert.DoesNotContain("pulse.app_updated", types); Assert.Contains("pulse.session_started", types);
        }

        [Theory]
        [InlineData("device")] [InlineData("screen")] [InlineData("settings")] [InlineData("network")] [InlineData("app")] [InlineData("usage")]
        public async Task EachGroupOffRemovesExactlyItsKeys(string group)
        {
            var baseline = await Both(FullEnvironment(), null);
            var after = await Both(FullEnvironment(), "{\"version\":1,\"groups\":{\"" + group + "\":false}}");
            var mine = new HashSet<string>(RemoteConfig.KeysOf(group));
            var removed = baseline.Except(after).ToArray();
            Assert.NotEmpty(removed);
            Assert.All(removed, k => Assert.Contains(k, mine));
            Assert.Equal(baseline.Where(k => !mine.Contains(k)).OrderBy(k => k), after.OrderBy(k => k));
            foreach (var kept in new[] { "platform", "osName", "osVersion", "appVersion", "appBuildNumber", "deviceModel", "locale", "sdkName", "sdkVersion", "bundleId" })
                Assert.Contains(kept, after);
        }

        // Keys on the first signal and on a signal of a later session (so the statistics are all present).
        private static async Task<string[]> Both(ExtrasEnvironment env, string config)
        {
            var storage = config == null ? new MemoryStorage() : Cached(config);
            var rig = new UsageRig(storage, env);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in await rig.Send()) foreach (var k in SystemKeys(e)) keys.Add(k);
            rig.Clock.Advance(3 * 3600);
            foreach (var e in await rig.Send()) foreach (var k in SystemKeys(e)) keys.Add(k);
            return keys.ToArray();
        }

        [Fact]
        public async Task AllGroupsOffStillSendsTheBaseSystemFields()
        {
            var all = "{\"groups\":{\"device\":false,\"screen\":false,\"settings\":false,\"network\":false,\"app\":false,\"usage\":false}}";
            var rig = new UsageRig(Cached(all), FullEnvironment());
            var keys = SystemKeys((await rig.Send("only")).Last());
            Assert.Equal(new[] { "appBuildNumber", "appVersion", "bundleId", "deviceModel", "locale", "osName", "osVersion", "platform", "sdkName", "sdkVersion" },
                keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        // ---- opt-out and blocking ----

        [Fact]
        public async Task OptOutDeletesTheConfigFile()
        {
            var rig = new UsageRig(Cached(AllOnBody), withUsage: false, configHandler: Reply(304));
            await rig.Client.ConfigFetch;
            rig.Runtime.OptOut = true;
            await Harness.Until(() => !rig.Storage.Files.ContainsKey("config"));
        }

        [Fact]
        public async Task ResponseArrivingAfterOptOutIsNotSaved()
        {
            var gate = new TaskCompletionSource<TransportResponse>();
            var rig = new UsageRig(withUsage: false, configHandler: (r, c) => gate.Task);
            await Harness.Until(() => !rig.Transport.ConfigRequests.IsEmpty);
            rig.Runtime.OptOut = true;
            gate.SetResult(new TransportResponse(200, AllOnBody));
            await rig.Client.ConfigFetch;
            Assert.False(rig.Storage.Files.ContainsKey("config"));
        }

        [Fact]
        public async Task NoFetchWhileOptedOutAndOneAfterOptOutIsTurnedOff()
        {
            var storage = new MemoryStorage(); storage.Files["opt-out"] = new byte[] { 1 };
            var rig = new UsageRig(storage, withUsage: false, configHandler: Reply(200, AllOnBody));
            await rig.Client.ConfigFetch;
            Assert.Empty(rig.Transport.ConfigRequests);
            rig.Runtime.OptOut = false;
            await rig.Client.ConfigFetch;
            Assert.Single(rig.Transport.ConfigRequests);
        }

        [Fact]
        public async Task HungSettingsRequestDoesNotDelayTrackOrFlush()
        {
            var rig = new UsageRig(withUsage: false, configHandler: (r, c) => new TaskCompletionSource<TransportResponse>().Task);
            await Harness.Until(() => !rig.Transport.ConfigRequests.IsEmpty);
            rig.Runtime.Track("fast");
            var flush = rig.Runtime.Flush();
            await rig.Client.ProcessAsync(true);
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("fast", Harness.Event(Assert.Single(rig.Transport.Requests)).GetProperty("type").GetString());
            Assert.False(rig.Client.ConfigFetch.IsCompleted);
            await rig.Client.StopAsync();
            await rig.Client.ConfigFetch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task SettingsRequestHonoursCancellationOfARespectfulTransport()
        {
            var seen = new TaskCompletionSource<CancellationToken>();
            var rig = new UsageRig(withUsage: false, configHandler: async (r, c) => { seen.TrySetResult(c); await Task.Delay(Timeout.Infinite, c); return null; });
            var token = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(token.CanBeCanceled);
            await rig.Client.StopAsync();
            await rig.Client.ConfigFetch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task UnreadableStorageMeansEverythingOnAndNeverThrows()
        {
            var storage = new MemoryStorage(); storage.Files["config"] = Encoding.UTF8.GetBytes("{\"screenViews\":false}"); storage.FailOnly = "config";
            var rig = new UsageRig(storage, withUsage: false, configHandler: Reply(200, AllOnBody));
            await rig.Client.ConfigFetch;
            Assert.True(rig.Client.Policy.ScreenViewsAllowed);
        }

        private sealed class VersionedEnvironment : IEnvironmentInfo
        {
            private readonly string version, build;
            internal VersionedEnvironment(string version, string build) { this.version = version; this.build = build; }
            public string Platform => "desktop"; public string OsName => "Windows"; public string OsVersion => "11";
            public string AppVersion => version; public string AppBuildNumber => build; public string DeviceModel => "Desktop";
            public string Locale => "en-US"; public string BundleId => "dev.example.synthetic"; public string SigningCertSha256 => "";
        }
    }
}
