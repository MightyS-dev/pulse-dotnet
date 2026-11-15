using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ExtraFieldsTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        private static DotNetEnvironmentInfo Info(string os = "Windows", IDictionary<string, string> host = null,
            Func<IDictionary<string, string>> dynamicHost = null, string locale = "sr-RS") =>
            new DotNetEnvironmentInfo(os, "11", "1.2.3", "", locale, null, host, dynamicHost);

        [Theory]
        [InlineData("Windows")] [InlineData("macOS")] [InlineData("Linux")]
        public void DesktopSystemsReportDeviceTypeDesktop(string os) =>
            Assert.Equal("desktop", Info(os).StaticFields["deviceType"]);

        [Theory]
        [InlineData("Android")] [InlineData("iOS")]
        public void MobileSystemsOmitDeviceTypeUnlessTheHostSuppliesIt(string os)
        {
            Assert.False(Info(os).StaticFields.ContainsKey("deviceType"));
            Assert.Equal("tablet", Info(os, new Dictionary<string, string> { ["deviceType"] = "tablet" }).StaticFields["deviceType"]);
        }

        [Fact]
        public void StaticFieldsHaveTheDocumentedFormats()
        {
            var fields = Info().StaticFields;
            Assert.Contains(fields["architecture"], new[] { "x86_64", "arm64", "armv7", "x86" });
            Assert.Matches(@"^\.NET \d+\.\d+\.\d+$", fields["runtimeVersion"]);
            Assert.Contains(fields["isDebugBuild"], new[] { "true", "false" });
            Assert.Equal("ltr", fields["layoutDirection"]);
            Assert.Equal("sr", fields["language"]);
            Assert.False(fields.ContainsKey("installSource")); Assert.False(fields.ContainsKey("isSimulator"));
            Assert.False(fields.ContainsKey("deviceBrand")); Assert.False(fields.ContainsKey("osApiLevel"));
            Assert.False(fields.ContainsKey("orientation")); Assert.False(fields.ContainsKey("screenWidth"));
            Assert.All(fields, p => Assert.True(SystemFields.IsKnown(p.Key), p.Key));
        }

        [Fact]
        public void ArchitectureAndRuntimeVersionMappings()
        {
            Assert.Equal("x86_64", DetectFields.ArchitectureName(Architecture.X64));
            Assert.Equal("arm64", DetectFields.ArchitectureName(Architecture.Arm64));
            Assert.Equal("armv7", DetectFields.ArchitectureName(Architecture.Arm));
            Assert.Equal("x86", DetectFields.ArchitectureName(Architecture.X86));
            Assert.Null(DetectFields.ArchitectureName(Architecture.Wasm)); Assert.Null(DetectFields.ArchitectureName(null));
            Assert.Equal(".NET 9.0.7", DetectFields.RuntimeVersion(".NET 9.0.7", new Version(9, 0, 7)));
            Assert.Null(DetectFields.RuntimeVersion(".NET Framework 4.8.9300.0", new Version(4, 0, 30319, 42000)));
            Assert.Null(DetectFields.RuntimeVersion("Mono 6.12", new Version(6, 12))); Assert.Null(DetectFields.RuntimeVersion(null, null));
        }

        [Fact]
        public void LanguageRegionAndPreferredLanguageComeFromCultures()
        {
            Assert.Equal("de", DetectFields.Language(CultureInfo.GetCultureInfo("de-DE")));
            Assert.Null(DetectFields.Language(CultureInfo.InvariantCulture)); Assert.Null(DetectFields.Language(null));
            Assert.Equal("DE", DetectFields.Region(CultureInfo.GetCultureInfo("de-DE")));
            Assert.Null(DetectFields.Region(CultureInfo.InvariantCulture)); Assert.Null(DetectFields.Region(CultureInfo.GetCultureInfo("de")));
            Assert.Equal("rtl", Info(locale: "ar-SA").StaticFields["layoutDirection"]);
            var fields = Info(locale: "").StaticFields;
            Assert.All(new[] { "language", "preferredLanguage", "region" }, k =>
                Assert.True(!fields.TryGetValue(k, out var v) || v.Length >= 2, k));
        }

        [Fact]
        public void HostFieldsWinOverDetectedOnesAndTheCoreDropsUnknownKeys()
        {
            var info = Info(host: new Dictionary<string, string>
            { ["architecture"] = "arm64", ["screenWidth"] = "1920", ["colorScheme"] = "dark", ["bogus"] = "x" });
            Assert.Equal("arm64", info.StaticFields["architecture"]);
            var json = JsonDocument.Parse(SignalJson.SystemJson(info, new PulseOptions())).RootElement;
            Assert.Equal("1920", json.GetProperty("screenWidth").GetString()); Assert.Equal("dark", json.GetProperty("colorScheme").GetString());
            Assert.False(json.TryGetProperty("bogus", out _));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(SignalJson.SystemJson(info, new PulseOptions()), "\"architecture\""));
        }

        [Fact]
        public void DynamicFieldsGiveZoneOffsetAndHostValuesAndChangeWithTheirSource()
        {
            var scheme = "light";
            var info = Info(dynamicHost: () => new Dictionary<string, string> { ["colorScheme"] = scheme });
            var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.FromMinutes(-210), "t", "t");
            info.ZoneSource = () => zone;
            var first = info.DynamicFields();
            Assert.Equal("-210", first["utcOffsetMinutes"]); Assert.Equal("light", first["colorScheme"]);
            scheme = "dark"; info.ZoneSource = () => TimeZoneInfo.Utc;
            var second = info.DynamicFields();
            Assert.Equal("0", second["utcOffsetMinutes"]); Assert.Equal("dark", second["colorScheme"]);
            Assert.Contains(second["timeZone"], new[] { "UTC", "Etc/UTC" });
        }

        [Fact]
        public void FailingHostCallbackStillGivesZoneFields()
        {
            var info = Info(dynamicHost: () => throw new InvalidOperationException("synthetic"));
            Assert.True(info.DynamicFields().ContainsKey("utcOffsetMinutes"));
        }

        [Fact]
        public void ZoneIdsAreIanaAndWindowsIdsAreConvertedOrOmitted()
        {
            Assert.Equal("Europe/Belgrade", DetectFields.IanaZone("Europe/Belgrade", false, _ => null));
            Assert.Equal("Europe/Belgrade", DetectFields.IanaZone("Central Europe Standard Time", true, id => id == "Central Europe Standard Time" ? "Europe/Belgrade" : null));
            Assert.Null(DetectFields.IanaZone("Odd Standard Time", true, _ => null)); Assert.Null(DetectFields.IanaZone("", false, _ => null));
            Assert.False(string.IsNullOrEmpty(DetectFields.IanaZone(TimeZoneInfo.Utc.Id)));
        }

        [Fact]
        public void AppNameComesFromTheEntryAssemblyProductWhenPresent()
        {
            var name = Info().StaticFields.TryGetValue("appName", out var value) ? value : null;
            Assert.True(name == null || name.Length is > 0 and <= 64);
        }

        [Fact]
        public async Task EveryFieldGroupReachesTheWireOnTheFirstSignal()
        {
            var transport = new FakeTransport();
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, debugDefault: () => true);
            host.Initialize("synthetic", new PulseOptions(), new PulsePlatformOptions
            {
                EnvironmentInfo = null,
                HostFields = new Dictionary<string, string> { ["screenWidth"] = "1440", ["screenHeight"] = "900", ["screenScale"] = "2" },
                DynamicHostFields = () => new Dictionary<string, string> { ["colorScheme"] = "dark" }
            });
            try
            {
                host.Runtime.Track("first"); await host.Runtime.Flush().WaitAsync(Limit);
                var events = Harness.Events(transport.Requests);
                Assert.Equal(new[] { "pulse.new_install", "pulse.session_started", "first" }, events.Select(e => e.GetProperty("type").GetString()));
                var system = events.Last().GetProperty("system");
                foreach (var key in new[] { "architecture", "runtimeVersion", "isDebugBuild", "timeZone", "utcOffsetMinutes", "layoutDirection",
                    "screenWidth", "screenHeight", "screenScale", "colorScheme", "firstSessionDate", "totalSessionsCount", "distinctDaysUsed",
                    "distinctDaysUsedLastMonth", "sessionSeconds", "isNewInstall" })
                    Assert.True(system.TryGetProperty(key, out var v) && v.GetString().Length > 0, key);
                Assert.Equal("true", system.GetProperty("isDebugBuild").GetString());
                Assert.All(system.EnumerateObject(), p => Assert.True(p.Value.ValueKind == JsonValueKind.String && p.Value.GetString().Length <= 1024));
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task UsageFileLivesNextToTheInstallIdAndSurvivesRestartWithoutNewInstall()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, ".tmp", Guid.NewGuid().ToString("N"));
            try
            {
                var platform = new PulsePlatformOptions { StorageDirectory = directory };
                var one = new PulseHost(transport: new FakeTransport());
                one.Initialize("synthetic", new PulseOptions(), platform);
                try { one.Runtime.Track("a"); await one.Usage.Idle.WaitAsync(Limit); await one.InstallId.Idle.WaitAsync(Limit); }
                finally { await one.ShutdownAsync(); }
                Assert.True(File.Exists(Path.Combine(directory, "install-id"))); Assert.True(File.Exists(Path.Combine(directory, "usage")));
                var transport = new FakeTransport();
                var two = new PulseHost(transport: transport);
                two.Initialize("synthetic", new PulseOptions(), platform);
                try { two.Runtime.Track("b"); await two.Runtime.Flush().WaitAsync(Limit); }
                finally { await two.ShutdownAsync(); }
                var types = Harness.Events(transport.Requests).Select(e => e.GetProperty("type").GetString()).ToArray();
                // The first run's own signals were still queued; the second run adds only a session start and its signal.
                Assert.Equal(new[] { "pulse.new_install", "pulse.session_started", "a", "pulse.session_started", "b" }, types);
                Assert.Equal("2", Harness.Events(transport.Requests).Last().GetProperty("system").GetProperty("totalSessionsCount").GetString());
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Fact]
        public async Task OptOutDeletesTheUsageFileTogetherWithTheInstallId()
        {
            var storage = new MemoryStorage();
            var host = new PulseHost(storage: storage, transport: new FakeTransport());
            host.Initialize("synthetic", new PulseOptions());
            try
            {
                host.Runtime.Track("a"); await host.Usage.Idle.WaitAsync(Limit); await host.InstallId.Idle.WaitAsync(Limit);
                Assert.True(storage.Files.ContainsKey("usage")); Assert.True(storage.Files.ContainsKey("install-id"));
                host.Runtime.OptOut = true; await host.Usage.Idle.WaitAsync(Limit); await host.InstallId.Idle.WaitAsync(Limit);
                Assert.False(storage.Files.ContainsKey("usage")); Assert.False(storage.Files.ContainsKey("install-id"));
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task ExistingInstallIdWithoutUsageFileIsNotANewInstallAndDebugFlagFollowsDetection()
        {
            var storage = new MemoryStorage(); storage.Files["install-id"] = System.Text.Encoding.ASCII.GetBytes(new string('a', 32));
            var transport = new FakeTransport();
            var host = new PulseHost(storage: storage, transport: transport, debugDefault: () => false);
            host.Initialize("synthetic", new PulseOptions());
            try
            {
                host.Runtime.Track("x"); await host.Runtime.Flush().WaitAsync(Limit);
                var events = Harness.Events(transport.Requests);
                Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "pulse.new_install");
                Assert.Equal("false", events.Last().GetProperty("system").GetProperty("isDebugBuild").GetString());
            }
            finally { await host.ShutdownAsync(); }
        }
    }
}
