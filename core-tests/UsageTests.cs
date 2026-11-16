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
    internal sealed class ExtrasEnvironment : IEnvironmentInfo, IExtraSystemFields
    {
        public string Platform => "mobile";
        public string OsName => "Android";
        public string OsVersion => "14";
        public string AppVersion => "1.2.3";
        public string AppBuildNumber => "42";
        public string DeviceModel => "Pixel 8";
        public string Locale => "en-US";
        public string BundleId => "dev.example.synthetic";
        public string SigningCertSha256 => "";
        internal Dictionary<string, string> Static = new Dictionary<string, string> { ["deviceType"] = "phone", ["architecture"] = "arm64" };
        internal Dictionary<string, string> Dynamic = new Dictionary<string, string>();
        internal bool Throw;
        internal string Version = "1.2.3";
        public IReadOnlyDictionary<string, string> StaticFields => Static;
        public IReadOnlyDictionary<string, string> DynamicFields()
        { if (Throw) throw new InvalidOperationException("synthetic"); return new Dictionary<string, string>(Dynamic); }
    }

    internal sealed class UsageRig
    {
        internal readonly FakeClock Clock = new FakeClock();
        internal readonly FakeTransport Transport = new FakeTransport();
        internal readonly MemoryStorage Storage;
        internal readonly IEnvironmentInfo Environment;
        internal readonly PulseRuntime Runtime;
        internal readonly UsageTracker Usage;
        internal PulseClient Client;
        private int seen;
        internal UsageRig(MemoryStorage storage = null, IEnvironmentInfo environment = null, PulseOptions options = null,
            Func<bool> installIsNew = null, bool withUsage = true,
            Func<TransportRequest, CancellationToken, Task<TransportResponse>> configHandler = null)
        {
            Transport.ConfigHandler = configHandler;
            Storage = storage ?? new MemoryStorage(); Environment = environment ?? new ExtrasEnvironment();
            Usage = withUsage ? new UsageTracker(() => Storage, t => t.UtcDateTime.ToString("yyyy-MM-dd"), installIsNew) : null;
            Runtime = new PulseRuntime(Clock);
            Runtime.InstallPlatform((k, o, optOut) => Client = new PulseClient(k, o, Clock, Transport, Storage, Environment, null, false,
                () => 0.5, optOut, Usage), null, () => PulseClient.ReadStoredOptOut(Storage));
            Runtime.Initialize("synthetic-key", options);
        }

        // Tracks one signal and returns the signals that this call put on the wire, in order.
        internal async Task<JsonElement[]> Send(string type = "test")
        {
            Runtime.Track(type);
            if (Usage != null) await Usage.Idle;
            await Client.ProcessAsync(true);
            var all = Harness.Events(Transport.Requests);
            var fresh = all.Skip(seen).ToArray(); seen = all.Length; return fresh;
        }

        internal static string[] Types(JsonElement[] events) => events.Select(e => e.GetProperty("type").GetString()).ToArray();
        internal static string Field(JsonElement e, string key) =>
            e.GetProperty("system").TryGetProperty(key, out var v) ? v.GetString() : null;
    }

    public sealed class UsageTests
    {
        [Fact]
        public async Task ExtraFieldsAreSentOnEverySignalAndOnlyKnownValidOnesSurvive()
        {
            var env = new ExtrasEnvironment();
            env.Static["colorScheme"] = "purple";            // not an allowed value
            env.Static["madeUpKey"] = "x";                    // not a system field
            env.Static["appName"] = new string('a', 100);     // cut to 64 characters
            env.Static["screenWidth"] = "393.0";              // written without trailing zeros
            env.Static["screenHeight"] = "not a number";
            env.Static["region"] = "";                        // empty: omitted
            var rig = new UsageRig(environment: env, withUsage: false);
            var events = await rig.Send(); var e = Assert.Single(events);
            Assert.Equal("phone", UsageRig.Field(e, "deviceType")); Assert.Equal("arm64", UsageRig.Field(e, "architecture"));
            Assert.Null(UsageRig.Field(e, "colorScheme")); Assert.Null(UsageRig.Field(e, "madeUpKey"));
            Assert.Equal(64, UsageRig.Field(e, "appName").Length); Assert.Equal("393", UsageRig.Field(e, "screenWidth"));
            Assert.Null(UsageRig.Field(e, "screenHeight")); Assert.Null(UsageRig.Field(e, "region"));
            Assert.Equal("dev.example.synthetic", UsageRig.Field(e, "bundleId"));
        }

        [Theory]
        [InlineData("deviceType", "tablet", true)] [InlineData("deviceType", "toaster", false)]
        [InlineData("orientation", "square", true)] [InlineData("orientation", "diagonal", false)]
        [InlineData("connectionType", "ethernet", true)] [InlineData("connectionType", "5g", false)]
        [InlineData("installSource", "galaxystore", true)] [InlineData("targetEnvironment", "ios-on-mac", true)]
        [InlineData("reduceMotion", "true", true)] [InlineData("reduceMotion", "yes", false)]
        [InlineData("utcOffsetMinutes", "-330", true)] [InlineData("utcOffsetMinutes", "1.5", false)]
        [InlineData("fontScale", "1.15", true)] [InlineData("fontScale", "1.23456", true)]
        public async Task EnumBooleanAndNumberFormatsAreEnforced(string key, string value, bool accepted)
        {
            var env = new ExtrasEnvironment(); env.Static[key] = value;
            var e = Assert.Single(await new UsageRig(environment: env, withUsage: false).Send());
            Assert.Equal(accepted, UsageRig.Field(e, key) != null);
        }

        [Fact]
        public async Task FontScaleKeepsAtMostThreeDecimals()
        {
            var env = new ExtrasEnvironment(); env.Static["fontScale"] = "1.23456";
            Assert.Equal("1.235", UsageRig.Field(Assert.Single(await new UsageRig(environment: env, withUsage: false).Send()), "fontScale"));
        }

        [Fact]
        public async Task DynamicFieldsAreReadAtCaptureAndChangeBetweenSignals()
        {
            var env = new ExtrasEnvironment(); env.Dynamic["orientation"] = "portrait"; env.Dynamic["colorScheme"] = "light";
            var rig = new UsageRig(environment: env, withUsage: false);
            var first = Assert.Single(await rig.Send());
            env.Dynamic["orientation"] = "landscape"; env.Dynamic["colorScheme"] = "dark";
            var second = Assert.Single(await rig.Send());
            Assert.Equal("portrait", UsageRig.Field(first, "orientation")); Assert.Equal("landscape", UsageRig.Field(second, "orientation"));
            Assert.Equal("light", UsageRig.Field(first, "colorScheme")); Assert.Equal("dark", UsageRig.Field(second, "colorScheme"));
        }

        [Fact]
        public async Task DynamicFieldNeverRepeatsAStaticKeyAndAFailingCallbackKeepsTheSignal()
        {
            var env = new ExtrasEnvironment(); env.Dynamic["deviceType"] = "tablet"; env.Dynamic["orientation"] = "portrait";
            var rig = new UsageRig(environment: env, withUsage: false);
            var e = Assert.Single(await rig.Send());
            Assert.Equal("phone", UsageRig.Field(e, "deviceType"));
            Assert.Equal(1, Regex(e.GetRawText(), "\"deviceType\""));
            env.Throw = true;
            var after = Assert.Single(await rig.Send());
            Assert.Equal("phone", UsageRig.Field(after, "deviceType")); Assert.Null(UsageRig.Field(after, "orientation"));
        }

        private static int Regex(string text, string pattern) => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;

        [Fact]
        public async Task PlatformCannotOverrideUsageFields()
        {
            var env = new ExtrasEnvironment(); env.Static["sessionSeconds"] = "999"; env.Static["totalSessionsCount"] = "999";
            var events = await new UsageRig(environment: env).Send();
            var user = events.Last();
            Assert.Equal("1", UsageRig.Field(user, "totalSessionsCount")); Assert.Equal("0", UsageRig.Field(user, "sessionSeconds"));
        }

        [Fact]
        public async Task EnvironmentWithoutExtrasStillWorksAndSendsNoUsageWithoutTracker()
        {
            var h = new Harness(); await h.Send();
            var system = Harness.Event(Assert.Single(h.Transport.Requests)).GetProperty("system");
            Assert.False(system.TryGetProperty("totalSessionsCount", out _)); Assert.False(system.TryGetProperty("deviceType", out _));
        }

        [Fact]
        public async Task FirstSessionOfANewInstallCarriesTheFullSetAndTheThreeSignalsInOrder()
        {
            var env = new ExtrasEnvironment();
            var rig = new UsageRig(environment: env);
            var events = await rig.Send("level_start");
            Assert.Equal(new[] { "pulse.new_install", "pulse.session_started", "level_start" }, UsageRig.Types(events));
            foreach (var e in events)
            {
                Assert.Equal("2026-11-13", UsageRig.Field(e, "firstSessionDate")); Assert.Equal("1", UsageRig.Field(e, "totalSessionsCount"));
                Assert.Equal("1", UsageRig.Field(e, "distinctDaysUsed")); Assert.Equal("1", UsageRig.Field(e, "distinctDaysUsedLastMonth"));
                Assert.Equal("true", UsageRig.Field(e, "isNewInstall")); Assert.Equal("phone", UsageRig.Field(e, "deviceType"));
                Assert.Null(UsageRig.Field(e, "previousSessionSeconds")); Assert.Null(UsageRig.Field(e, "averageSessionSeconds"));
                Assert.Equal(e.GetProperty("sessionId").GetString(), events[0].GetProperty("sessionId").GetString());
            }
        }

        [Fact]
        public async Task SecondSessionHasPreviousAndAverageAndNoNewInstallFlag()
        {
            var rig = new UsageRig();
            await rig.Send(); rig.Clock.Advance(100); await rig.Send();       // session 1 lasts 100 s
            rig.Clock.Advance(40 * 60);                                        // idle past 30 minutes
            var second = await rig.Send("again");
            Assert.Equal(new[] { "pulse.session_started", "again" }, UsageRig.Types(second));
            var e = second.Last();
            Assert.Equal("2", UsageRig.Field(e, "totalSessionsCount")); Assert.Equal("100", UsageRig.Field(e, "previousSessionSeconds"));
            Assert.Equal("100", UsageRig.Field(e, "averageSessionSeconds")); Assert.Null(UsageRig.Field(e, "isNewInstall"));
            Assert.Equal("0", UsageRig.Field(e, "sessionSeconds"));
            rig.Clock.Advance(25); var inside = Assert.Single(await rig.Send());
            Assert.Equal("25", UsageRig.Field(inside, "sessionSeconds"));
            rig.Clock.Advance(40 * 60); await rig.Send();
            var third = (await rig.Send()).Last();
            Assert.Equal("3", UsageRig.Field(third, "totalSessionsCount")); Assert.Equal("25", UsageRig.Field(third, "previousSessionSeconds"));
            Assert.Equal("63", UsageRig.Field(third, "averageSessionSeconds"));   // mean of 100 and 25, rounded
        }

        [Fact]
        public async Task SessionStartedIsSentOncePerSessionOnly()
        {
            var rig = new UsageRig();
            await rig.Send();
            for (var i = 0; i < 3; i++) { rig.Clock.Advance(60); Assert.Equal(new[] { "x" }, UsageRig.Types(await rig.Send("x"))); }
        }

        [Fact]
        public async Task DaysAreCountedAcrossDayChangesAndThirtyDayWindow()
        {
            var rig = new UsageRig();
            await rig.Send();                                                  // day 1
            rig.Clock.Advance(60); await rig.Send();                           // same day
            rig.Clock.Advance(24 * 3600); await rig.Send();                    // day 2
            var third = (await rig.Send()).Last();
            Assert.Equal("2", UsageRig.Field(third, "distinctDaysUsed")); Assert.Equal("2", UsageRig.Field(third, "distinctDaysUsedLastMonth"));
            rig.Clock.Advance(35 * 24 * 3600); var late = (await rig.Send()).Last();   // 35 days later
            Assert.Equal("3", UsageRig.Field(late, "distinctDaysUsed")); Assert.Equal("1", UsageRig.Field(late, "distinctDaysUsedLastMonth"));
            Assert.Equal("2026-11-13", UsageRig.Field(late, "firstSessionDate"));
        }

        [Fact]
        public async Task ThirtyDayWindowIncludesTheTwentyNinthDayBackAndNotTheThirtieth()
        {
            var rig = new UsageRig();
            await rig.Send();
            rig.Clock.Advance(29 * 24 * 3600); var edge = (await rig.Send()).Last();
            Assert.Equal("2", UsageRig.Field(edge, "distinctDaysUsedLastMonth"));
            rig.Clock.Advance(24 * 3600); var past = (await rig.Send()).Last();
            Assert.Equal("2", UsageRig.Field(past, "distinctDaysUsedLastMonth")); Assert.Equal("3", UsageRig.Field(past, "distinctDaysUsed"));
        }

        [Fact]
        public async Task StatisticsSurviveARestartAndARunningSessionCountsAsCompleted()
        {
            var storage = new MemoryStorage();
            var first = new UsageRig(storage); await first.Send(); first.Clock.Advance(90); await first.Send();
            await first.Usage.Idle;
            Assert.True(storage.Files.ContainsKey("usage"));
            var second = new UsageRig(storage, installIsNew: () => false);
            var events = await second.Send("back");
            Assert.Equal(new[] { "pulse.session_started", "back" }, UsageRig.Types(events));
            var e = events.Last();
            Assert.Equal("2", UsageRig.Field(e, "totalSessionsCount")); Assert.Equal("2026-11-13", UsageRig.Field(e, "firstSessionDate"));
            Assert.Equal("90", UsageRig.Field(e, "previousSessionSeconds")); Assert.Null(UsageRig.Field(e, "isNewInstall"));
        }

        [Fact]
        public async Task ExistingInstallWithoutAUsageFileIsNotANewInstall()
        {
            var events = await new UsageRig(installIsNew: () => false).Send();
            Assert.Equal(new[] { "pulse.session_started", "test" }, UsageRig.Types(events));
            Assert.Null(UsageRig.Field(events.Last(), "isNewInstall"));
        }

        [Fact]
        public async Task AppUpdateIsDetectedOnceWithPreviousValues()
        {
            var storage = new MemoryStorage();
            var old = new UsageRig(storage, new ExtrasEnvironment()); await old.Send(); await old.Usage.Idle;
            var env = new ExtrasEnvironment();
            var options = new PulseOptions { AppVersion = "1.3.0" };
            var updated = new UsageRig(storage, env, options, () => false);
            var events = await updated.Send();
            Assert.Equal(new[] { "pulse.app_updated", "pulse.session_started", "test" }, UsageRig.Types(events));
            var props = events[0].GetProperty("props");
            Assert.Equal("1.2.3", props.GetProperty("previousVersion").GetString()); Assert.Equal("42", props.GetProperty("previousBuild").GetString());
            await updated.Usage.Idle;
            var again = new UsageRig(storage, new ExtrasEnvironment(), new PulseOptions { AppVersion = "1.3.0" }, () => false);
            Assert.DoesNotContain("pulse.app_updated", UsageRig.Types(await again.Send()));
        }

        [Fact]
        public async Task RestartInsideIdleWindowWithNewVersionSendsExactlyOneAppUpdated()
        {
            var storage = new MemoryStorage();
            var old = new UsageRig(storage, new BuildEnvironment("1.2.3", "42"), installIsNew: () => false);
            await old.Send(); await old.Usage.Idle;
            old.Clock.Advance(60);
            var upgraded = new UsageRig(storage, new BuildEnvironment("1.3.0", "50"), installIsNew: () => false);
            upgraded.Clock.Advance((old.Clock.UtcNow - upgraded.Clock.UtcNow).TotalSeconds);
            var first = await upgraded.Send(); var second = await upgraded.Send("again"); await upgraded.Usage.Idle;
            Assert.Single(UsageRig.Types(first), "pulse.app_updated");
            Assert.DoesNotContain("pulse.app_updated", UsageRig.Types(second));
            var props = first[0].GetProperty("props");
            Assert.Equal("1.2.3", props.GetProperty("previousVersion").GetString()); Assert.Equal("42", props.GetProperty("previousBuild").GetString());
            var same = new UsageRig(storage, new BuildEnvironment("1.3.0", "50"), installIsNew: () => false);
            Assert.DoesNotContain("pulse.app_updated", UsageRig.Types(await same.Send()));
        }

        [Fact]
        public async Task StoredVersionIsKeptWhileAppUpdatedIsDisabledByThePortal()
        {
            var storage = new MemoryStorage();
            var old = new UsageRig(storage, new BuildEnvironment("1.2.3", "42"), installIsNew: () => false);
            await old.Send(); await old.Usage.Idle;
            storage.Files["config"] = System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"disabledSignals\":[\"pulse.app_updated\"]}");
            var off = new UsageRig(storage, new BuildEnvironment("1.3.0", "50"), installIsNew: () => false);
            Assert.DoesNotContain("pulse.app_updated", UsageRig.Types(await off.Send())); await off.Usage.Idle;
            storage.Files.Remove("config");
            var on = new UsageRig(storage, new BuildEnvironment("1.3.0", "50"), installIsNew: () => false);
            Assert.Contains("pulse.app_updated", UsageRig.Types(await on.Send()));
        }

        [Fact]
        public async Task StoredVersionIsKeptWhileCodeTurnsAutomaticSignalsOff()
        {
            var storage = new MemoryStorage();
            var old = new UsageRig(storage, new BuildEnvironment("1.2.3", "42"), installIsNew: () => false);
            await old.Send(); await old.Usage.Idle;
            var off = new UsageRig(storage, new BuildEnvironment("1.3.0", "50"), new PulseOptions { AutomaticSignals = false }, () => false);
            Assert.DoesNotContain("pulse.app_updated", UsageRig.Types(await off.Send())); await off.Usage.Idle;
            var on = new UsageRig(storage, new BuildEnvironment("1.3.0", "50"), installIsNew: () => false);
            Assert.Contains("pulse.app_updated", UsageRig.Types(await on.Send()));
        }

        [Fact]
        public async Task BuildNumberChangeAloneIsAnUpdate()
        {
            var storage = new MemoryStorage();
            var old = new UsageRig(storage); await old.Send(); await old.Usage.Idle;
            var types = UsageRig.Types(await new UsageRig(storage, new BuildEnvironment("1.2.3", "43"), installIsNew: () => false).Send());
            Assert.Contains("pulse.app_updated", types);
        }

        private sealed class BuildEnvironment : IEnvironmentInfo
        {
            public BuildEnvironment(string version, string build) { AppVersion = version; AppBuildNumber = build; }
            public string Platform => "mobile"; public string OsName => "Android"; public string OsVersion => "14";
            public string AppVersion { get; } public string AppBuildNumber { get; } public string DeviceModel => "x";
            public string Locale => "en"; public string BundleId => ""; public string SigningCertSha256 => "";
        }

        [Fact]
        public async Task AutomaticSignalsFollowTheDebugFlag()
        {
            var events = await new UsageRig(options: new PulseOptions { Debug = true }).Send();
            Assert.All(events, e => Assert.True(e.GetProperty("debug").GetBoolean()));
            events = await new UsageRig(options: new PulseOptions { Debug = false }).Send();
            Assert.All(events, e => Assert.False(e.GetProperty("debug").GetBoolean()));
        }

        [Fact]
        public async Task OptOutSuppressesAutomaticSignalsAndDeletesTheFile()
        {
            var rig = new UsageRig(); await rig.Send(); await rig.Usage.Idle;
            Assert.True(rig.Storage.Files.ContainsKey("usage"));
            rig.Runtime.OptOut = true; await rig.Usage.Idle;
            Assert.False(rig.Storage.Files.ContainsKey("usage"));
            var before = Harness.Events(rig.Transport.Requests).Length;
            rig.Runtime.Track("silent"); await rig.Usage.Idle; await rig.Client.ProcessAsync(true);
            Assert.Equal(before, Harness.Events(rig.Transport.Requests).Length);
            Assert.False(rig.Storage.Files.ContainsKey("usage"));
        }

        [Fact]
        public async Task OptOutThenOptInStartsAFreshHistory()
        {
            var rig = new UsageRig(); await rig.Send(); rig.Clock.Advance(50); await rig.Send();
            rig.Runtime.OptOut = true; rig.Runtime.OptOut = false; await rig.Usage.Idle;
            var events = await rig.Send("after");
            Assert.Equal(new[] { "pulse.new_install", "pulse.session_started", "after" }, UsageRig.Types(events));
            var e = events.Last();
            Assert.Equal("1", UsageRig.Field(e, "totalSessionsCount")); Assert.Null(UsageRig.Field(e, "previousSessionSeconds"));
        }

        [Fact]
        public async Task UnreadableOrWriteFailingStorageKeepsCountingInMemoryAndNeverThrows()
        {
            var rig = new UsageRig(new MemoryStorage { FailOnly = "usage" });
            var first = await rig.Send();
            Assert.Contains("pulse.session_started", UsageRig.Types(first)); Assert.DoesNotContain("pulse.new_install", UsageRig.Types(first));
            rig.Clock.Advance(60); await rig.Send(); rig.Clock.Advance(40 * 60);
            var later = (await rig.Send()).Last();
            Assert.Equal("2", UsageRig.Field(later, "totalSessionsCount")); Assert.Equal("60", UsageRig.Field(later, "previousSessionSeconds"));
        }

        [Fact]
        public async Task CorruptUsageFileIsIgnoredWithoutClaimingANewInstall()
        {
            var storage = new MemoryStorage(); storage.Files["usage"] = Encoding.UTF8.GetBytes("{not json");
            var events = await new UsageRig(storage).Send();
            Assert.Equal(new[] { "pulse.session_started", "test" }, UsageRig.Types(events));
            Assert.Equal("1", UsageRig.Field(events.Last(), "totalSessionsCount"));
        }

        [Fact]
        public async Task CompletedSessionsOlderThanNinetyDaysLeaveTheAverage()
        {
            var rig = new UsageRig();
            await rig.Send(); rig.Clock.Advance(1000); await rig.Send();              // 1000 s session
            rig.Clock.Advance(100 * 24 * 3600); await rig.Send(); rig.Clock.Advance(10); await rig.Send();   // 10 s session, 100 days later
            rig.Clock.Advance(40 * 60); var e = (await rig.Send()).Last();
            Assert.Equal("10", UsageRig.Field(e, "previousSessionSeconds")); Assert.Equal("10", UsageRig.Field(e, "averageSessionSeconds"));
        }

        [Fact]
        public async Task UsageFileIsOneSmallJsonObject()
        {
            var storage = new MemoryStorage();
            var tracker = new UsageTracker(() => storage, t => "2026-11-13");
            tracker.Observe("s1", DateTimeOffset.UtcNow, "1.0", "1");
            await tracker.Idle;
            using var json = JsonDocument.Parse(storage.Files["usage"]);
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
            Assert.Equal("2026-11-13", json.RootElement.GetProperty("first").GetString());
            Assert.True(storage.Files["usage"].Length < 2000);
        }
    }
}
