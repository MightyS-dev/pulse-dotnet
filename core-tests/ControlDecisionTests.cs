using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ControlDecisionTests
    {
        private static ControlInput InputOf(JsonElement i)
        {
            var rules = i.GetProperty("rules");
            var dismissed = i.GetProperty("suggestDismissedHoursAgo");
            return new ControlInput
            {
                Rules = rules.ValueKind == JsonValueKind.Null ? null : ControlRules.Parse(rules.GetRawText()),
                Now = DateTimeOffset.Parse(i.GetProperty("now").GetString(), CultureInfo.InvariantCulture),
                Platform = i.GetProperty("platform").GetString(), AppVersion = i.GetProperty("appVersion").GetString(),
                Debug = i.GetProperty("debug").GetBoolean(), Sdk = i.GetProperty("sdk").GetString(),
                FromCache = i.GetProperty("fromCache").GetBoolean(), CacheAgeHours = i.GetProperty("cacheAgeHours").GetDouble(),
                UpdateReturns = i.GetProperty("updateReturns").GetInt32(),
                PlayUpdateNotAvailable = i.GetProperty("playUpdateNotAvailable").GetBoolean(),
                CanBuildStoreLink = i.GetProperty("canBuildStoreLink").GetBoolean(),
                SuggestDismissedHoursAgo = dismissed.ValueKind == JsonValueKind.Null ? (double?)null : dismissed.GetDouble(),
                ShownAnnouncements = i.GetProperty("shownAnnouncements").EnumerateArray().Select(x => x.GetString()).ToArray(),
                JustUpdated = i.GetProperty("justUpdated").GetBoolean(), WhatsNewShown = i.GetProperty("whatsNewShown").GetBoolean()
            };
        }

        [TablesFact]
        public void Every_decision_case_gives_the_expected_state_banners_and_card()
        {
            using var doc = ControlTables.Load("decision-cases.json");
            var cases = doc.RootElement.EnumerateArray().ToArray();
            Assert.True(cases.Length >= 60, "cases " + cases.Length);
            var states = new HashSet<string>();
            foreach (var c in cases)
            {
                var name = c.GetProperty("name").GetString();
                var input = InputOf(c.GetProperty("input"));
                var raw = c.GetProperty("input").GetProperty("rules");
                if (raw.ValueKind == JsonValueKind.Object && raw.GetProperty("format").GetInt32() == 1)
                    Assert.True(input.Rules != null, name + ": the rules must be readable");
                var result = ControlDecision.Decide(input);
                var e = c.GetProperty("expected");
                Assert.True(e.GetProperty("state").GetString() == ControlKinds.Wire(result.State), name + ": state " + result.State);
                Assert.True(e.GetProperty("allowContinue").GetBoolean() == result.AllowContinue, name + ": allowContinue " + result.AllowContinue);
                Assert.True(e.GetProperty("banners").EnumerateArray().Select(x => x.GetString()).SequenceEqual(result.Banners), name + ": banners " + string.Join(",", result.Banners));
                var card = e.GetProperty("card").ValueKind == JsonValueKind.Null ? null : e.GetProperty("card").GetString();
                Assert.True(card == (result.Card.HasValue ? ControlKinds.Wire(result.Card.Value) : null), name + ": card " + result.Card);
                states.Add(ControlKinds.Wire(result.State));
            }
            Assert.Superset(new HashSet<string> { "normal", "maintenance", "versionBlocked", "updateRequired", "updateSuggested" }, states);
        }

        [TablesFact]
        public void The_decision_table_covers_every_banner_and_card()
        {
            using var doc = ControlTables.Load("decision-cases.json");
            var all = doc.RootElement.EnumerateArray().Select(c => c.GetProperty("expected")).ToArray();
            Assert.Contains(all, e => e.GetProperty("banners").EnumerateArray().Any(b => b.GetString() == "testBuild"));
            Assert.Contains(all, e => e.GetProperty("banners").EnumerateArray().Any(b => b.GetString() == "maintenanceUpcoming"));
            Assert.Contains(all, e => e.GetProperty("card").ValueKind == JsonValueKind.String && e.GetProperty("card").GetString() == "whatsNew");
            Assert.Contains(all, e => e.GetProperty("card").ValueKind == JsonValueKind.String && e.GetProperty("card").GetString() == "announcement");
        }

        // ---- reading the rules ----

        private const string Empty = "{\"format\":1,\"version\":7,\"off\":false,\"update\":{},\"blocked\":[],\"maintenance\":null,\"announcement\":null," +
            "\"whatsNew\":null,\"texts\":{\"custom\":{}},\"disabledSdkVersions\":[]}";

        [Fact]
        public void Empty_rules_are_readable_and_decide_normal()
        {
            var rules = ControlRules.Parse(Empty);
            Assert.NotNull(rules);
            Assert.Equal(7, rules.Version);
            Assert.Equal("\"7\"", rules.ETag);
            Assert.Equal(ControlKind.Normal, ControlDecision.Decide(new ControlInput { Rules = rules }).State);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("[]")]
        [InlineData("\"text\"")]
        [InlineData("not json")]
        [InlineData("{\"format\":2}")]
        [InlineData("{\"version\":1}")]
        [InlineData("{\"format\":\"1\"}")]
        [InlineData("{\"format\":1.5}")]
        [InlineData("{\"format\":1,\"off\":\"no\"}")]
        [InlineData("{\"format\":1,\"update\":[]}")]
        [InlineData("{\"format\":1,\"update\":{\"ios\":{\"min\":3}}}")]
        [InlineData("{\"format\":1,\"blocked\":{}}")]
        [InlineData("{\"format\":1,\"blocked\":[{\"version\":\"1.0\",\"platforms\":\"ios\"}]}")]
        [InlineData("{\"format\":1,\"maintenance\":{\"startsAt\":\"yesterday\"}}")]
        [InlineData("{\"format\":1,\"maintenance\":{\"headsUpMinutes\":\"60\"}}")]
        [InlineData("{\"format\":1,\"announcement\":{\"id\":5}}")]
        [InlineData("{\"format\":1,\"disabledSdkVersions\":[1]}")]
        [InlineData("{\"format\":1,\"format\":1}")]
        public void Bodies_that_cannot_be_read_are_unusable(string body) => Assert.Null(ControlRules.Parse(body));

        [Fact]
        public void A_body_over_65536_bytes_is_unusable_even_when_it_is_valid_json()
        {
            Assert.Null(ControlRules.Parse("{\"format\":1,\"extra\":\"" + new string('a', 65536) + "\"}"));
            Assert.NotNull(ControlRules.Parse("{\"format\":1,\"extra\":\"" + new string('a', 65000) + "\"}"));
            // Fewer than 65536 characters, but more than 65536 bytes in UTF-8.
            Assert.Null(ControlRules.Parse("{\"format\":1,\"extra\":\"" + new string('é', 33000) + "\"}"));
        }

        [Fact]
        public void Unknown_fields_are_ignored_and_null_is_accepted_for_any_field()
        {
            var rules = ControlRules.Parse("{\"format\":1,\"future\":{\"a\":[1,2]},\"version\":null,\"off\":null,\"update\":{\"ios\":null,\"fax\":{\"min\":1}}," +
                "\"maintenance\":{\"startsAt\":null,\"endsAt\":null,\"unknown\":true},\"texts\":null,\"disabledSdkVersions\":null}");
            Assert.NotNull(rules);
            Assert.Equal(0, rules.Version);
            Assert.Null(rules.ETag);
            Assert.False(rules.Off);
            Assert.Empty(rules.Update);
            Assert.Equal(ControlKind.Normal, ControlDecision.Decide(new ControlInput { Rules = rules, Platform = "ios" }).State);
        }

        [Fact]
        public void A_store_link_that_is_not_a_web_address_counts_as_no_link()
        {
            var rules = ControlRules.Parse("{\"format\":1,\"update\":{\"ios\":{\"min\":\"2.0\",\"mode\":\"required\",\"storeUrl\":\"itms-apps://x\"}," +
                "\"android\":{\"min\":\"2.0\",\"mode\":\"required\",\"storeUrl\":\"javascript:alert(1)\"}}}");
            Assert.Null(rules.Update["ios"].StoreUrl);
            Assert.Null(rules.Update["android"].StoreUrl);
            // With no way to build a link the lock turns into a suggestion.
            var input = new ControlInput { Rules = rules, Platform = "ios", AppVersion = "1.0", CanBuildStoreLink = false };
            Assert.Equal(ControlKind.UpdateSuggested, ControlDecision.Decide(input).State);
        }

        [Fact]
        public void Every_way_out_of_a_required_update_turns_it_into_a_suggestion()
        {
            ControlInput Required(Action<ControlInput> change = null, string store = null)
            {
                var rules = ControlRules.Parse("{\"format\":1,\"update\":{\"android\":{\"min\":\"2.0.0\",\"latest\":\"2.0.0\",\"mode\":\"required\"," +
                    "\"storeVersion\":" + (store == null ? "null" : "\"" + store + "\"") + ",\"storeUrl\":\"https://play.google.com/store/apps/details?id=x\"}}}");
                var input = new ControlInput { Rules = rules, Platform = "android", AppVersion = "1.0.0", Now = DateTimeOffset.UtcNow };
                change?.Invoke(input);
                return input;
            }
            Assert.Equal(ControlKind.UpdateRequired, ControlDecision.Decide(Required()).State);
            Assert.False(ControlDecision.Decide(Required()).AllowContinue);
            Assert.Equal(ControlKind.UpdateSuggested, ControlDecision.Decide(Required(store: "1.0.0")).State);
            Assert.Equal(ControlKind.UpdateSuggested, ControlDecision.Decide(Required(i => i.UpdateReturns = 3)).State);
            Assert.Equal(ControlKind.UpdateRequired, ControlDecision.Decide(Required(i => i.UpdateReturns = 2)).State);
            Assert.Equal(ControlKind.UpdateSuggested, ControlDecision.Decide(Required(i => i.PlayUpdateNotAvailable = true)).State);
            Assert.True(ControlDecision.Decide(Required(i => i.PlayUpdateNotAvailable = true)).AllowContinue);
        }

        // ---- texts ----

        [TablesFact]
        public void The_built_in_texts_equal_the_shared_table_in_all_12_languages()
        {
            using var doc = ControlTables.Load("texts.json");
            var languages = doc.RootElement.GetProperty("languages").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Equal(languages, ControlTexts.Languages);
            var texts = doc.RootElement.GetProperty("texts");
            var count = 0;
            foreach (var language in languages)
            {
                var map = texts.GetProperty(language);
                Assert.Equal(ControlTexts.Keys.OrderBy(x => x, StringComparer.Ordinal), map.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
                foreach (var p in map.EnumerateObject())
                {
                    Assert.Equal(p.Value.GetString(), ControlTexts.Get(language, p.Name));
                    count++;
                }
            }
            Assert.Equal(12 * 20, count);
            Assert.Equal("", ControlTexts.Get("en", "no.such.key"));
            Assert.Equal(ControlTexts.Get("en", "continue"), ControlTexts.Get("xx", "continue"));
        }

        [Theory]
        [InlineData("en-US", "en")]
        [InlineData("de-AT", "de")]
        [InlineData("pt_PT", "pt-BR")]
        [InlineData("sr-RS", "sr-Latn")]
        [InlineData("zh-CN", "zh-Hans")]
        [InlineData("zh-Hans-SG", "zh-Hans")]
        [InlineData("zh-TW", "en")]
        [InlineData("nl-NL", "en")]
        [InlineData("", "en")]
        [InlineData(null, "en")]
        public void The_language_follows_the_locale_and_falls_back_to_english(string locale, string expected) =>
            Assert.Equal(expected, ControlStateBuilder.LanguageFor(new[] { locale }));

        [Fact]
        public void The_first_locale_with_a_built_in_language_wins()
        {
            Assert.Equal("de", ControlStateBuilder.LanguageFor(new[] { "nl-NL", "de-DE", "fr-FR" }));
            Assert.Equal("en", ControlStateBuilder.LanguageFor(new string[0]));
        }

        private sealed class ProviderEnvironment : IEnvironmentInfo, IScreenLanguage
        {
            private readonly Func<string> language;
            internal ProviderEnvironment(string locale, Func<string> language) { Locale = locale; this.language = language; }
            public string Platform => "mobile"; public string OsName => "Android"; public string OsVersion => "14";
            public string AppVersion => "1.0.0"; public string AppBuildNumber => "1"; public string DeviceModel => "";
            public string Locale { get; } public string BundleId => ""; public string SigningCertSha256 => "";
            public string ScreenLanguage => language();
        }

        private sealed class PlainEnvironment : IEnvironmentInfo
        {
            public string Platform => "desktop"; public string OsName => "Windows"; public string OsVersion => "11";
            public string AppVersion => "1.0.0"; public string AppBuildNumber => "1"; public string DeviceModel => "";
            public string Locale => "sr-RS"; public string BundleId => ""; public string SigningCertSha256 => "";
        }

        [Theory]
        [InlineData("de", "en-US", "de")]
        [InlineData("ja", "", "ja")]
        [InlineData("pt-BR", "en-US", "pt-BR")]
        [InlineData("", "fr-FR", "fr")]
        [InlineData(null, "fr-FR", "fr")]
        [InlineData("  ", "es-ES", "es")]
        [InlineData("xx", "it-IT", "it")]
        public void A_platform_language_wins_over_the_culture_when_it_is_not_empty(string provided, string culture, string expected) =>
            Assert.Equal(expected, ControlStateBuilder.LanguageFor(PulseClient.LocalePreference(
                new ProviderEnvironment(culture, () => provided), culture)));

        [Fact]
        public void An_environment_without_a_provider_uses_the_culture_and_a_failing_provider_is_ignored()
        {
            Assert.Equal("sr-Latn", ControlStateBuilder.LanguageFor(PulseClient.LocalePreference(new PlainEnvironment(), "")));
            Assert.Equal("fr", ControlStateBuilder.LanguageFor(PulseClient.LocalePreference(
                new ProviderEnvironment("", () => throw new InvalidOperationException()), "fr-CA")));
        }

        // ---- the public state ----

        private static ControlContext Context(string platform = "ios", string locale = "en-US") => new ControlContext
        {
            Platform = platform, AppName = "Demo", AppVersion = "1.0.0", BundleId = "dev.example.demo", Locales = new[] { locale },
            Now = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero), ToLocal = t => t.ToOffset(TimeSpan.FromHours(1))
        };

        private static ControlState Build(string body, ControlContext context, Action<ControlInput> change = null)
        {
            var rules = ControlRules.Parse(body);
            var input = new ControlInput { Rules = rules, Platform = context.Platform, AppVersion = context.AppVersion, Now = context.Now, Sdk = "pulse-dotnet@0.9.7" };
            change?.Invoke(input);
            return ControlStateBuilder.Build(rules, ControlDecision.Decide(input), context);
        }

        private const string RequiredIos = "{\"format\":1,\"update\":{\"ios\":{\"min\":\"2.0.0\",\"latest\":\"2.1.0\",\"mode\":\"required\"," +
            "\"storeVersion\":\"2.1.0\",\"storeUrl\":\"https://apps.apple.com/app/id1\"}}}";

        [Fact]
        public void A_required_update_shows_the_texts_and_the_store_link_and_cannot_be_dismissed()
        {
            var state = Build(RequiredIos, Context());
            Assert.Equal(ControlKind.UpdateRequired, state.Kind);
            Assert.Equal("Update required", state.Title);
            Assert.Equal("This version of Demo is no longer supported. Please update to keep using the app.", state.Body);
            Assert.Equal("https://apps.apple.com/app/id1", state.Url);
            Assert.Equal("2.1.0", state.Version);
            Assert.False(state.AllowContinue);
            Assert.True(state.IsLocking);
            Assert.False(state.CanDismiss);
            Assert.False(state.IsTestBuild);
        }

        [Fact]
        public void A_debug_build_shows_the_preview_banner_and_a_continue_button()
        {
            var state = Build(RequiredIos, Context(), i => i.Debug = true);
            Assert.Equal(ControlKind.UpdateRequired, state.Kind);
            Assert.True(state.AllowContinue);
            Assert.False(state.IsLocking);
            Assert.True(state.IsTestBuild);
            Assert.Equal("Test build: App Control preview", state.TestBuildText);
            Assert.Equal("Continue", state.DismissText);
        }

        [Fact]
        public void Maintenance_fills_the_end_time_in_the_device_time_zone_and_adds_the_date_when_it_is_not_today()
        {
            var sameDay = Build("{\"format\":1,\"maintenance\":{\"startsAt\":\"2026-12-01T11:00:00Z\",\"endsAt\":\"2026-12-01T14:00:00Z\",\"platforms\":null}}", Context());
            Assert.Equal(ControlKind.Maintenance, sameDay.Kind);
            Assert.Contains("3:00", sameDay.Body);
            Assert.DoesNotContain("2026", sameDay.Body);
            var nextDay = Build("{\"format\":1,\"maintenance\":{\"startsAt\":\"2026-12-01T11:00:00Z\",\"endsAt\":\"2026-12-02T14:00:00Z\",\"platforms\":null}}", Context());
            Assert.Contains("2026", nextDay.Body);
            Assert.DoesNotContain("{", nextDay.Body);
            Assert.Equal(new DateTimeOffset(2026, 12, 2, 14, 0, 0, TimeSpan.Zero), nextDay.EndsAt);
        }

        [Fact]
        public void Custom_texts_replace_only_the_fields_that_are_not_null_and_the_language_follows_the_locale()
        {
            var body = "{\"format\":1,\"update\":{\"ios\":{\"min\":\"2.0.0\",\"mode\":\"required\",\"storeUrl\":\"https://apps.apple.com/app/id1\"}}," +
                "\"texts\":{\"custom\":{\"updateRequired\":{\"title\":null,\"body\":\"Please update {appName} now\",\"buttonText\":null}}}}";
            var german = Build(body, Context(locale: "de-DE"));
            Assert.Equal("Update erforderlich", german.Title);
            Assert.Equal("Please update Demo now", german.Body);
            Assert.Equal("Updaten", german.ButtonText);
        }

        [Fact]
        public void A_card_and_a_banner_ride_along_with_a_lock_as_extras()
        {
            var body = "{\"format\":1,\"update\":{\"ios\":{\"min\":\"2.0.0\",\"mode\":\"required\",\"storeUrl\":\"https://apps.apple.com/app/id1\"}}," +
                "\"announcement\":{\"id\":\"a1\",\"title\":\"Winter\",\"body\":\"Snow\",\"url\":null,\"buttonText\":null,\"until\":\"2026-12-30T00:00:00Z\",\"platforms\":null}," +
                "\"maintenance\":{\"startsAt\":\"2026-12-01T12:30:00Z\",\"endsAt\":\"2026-12-01T13:30:00Z\",\"headsUpMinutes\":60,\"exemptAboveStore\":false,\"platforms\":null}}";
            var state = Build(body, Context());
            Assert.Equal(ControlKind.UpdateRequired, state.Kind);
            Assert.Equal(new[] { ControlKind.Announcement, ControlKind.MaintenanceUpcoming }, state.Extras.Select(x => x.Kind).ToArray());
            Assert.Equal("Winter", state.Extras[0].Title);
            Assert.Equal("OK", state.Extras[0].ButtonText);
            Assert.True(state.Extras[0].CanDismiss);
        }

        [Fact]
        public void With_nothing_to_lock_a_card_is_the_state_and_a_banner_is_next()
        {
            var body = "{\"format\":1,\"whatsNew\":{\"version\":\"1.0.0\",\"body\":\"Dark mode\"},\"maintenance\":{\"startsAt\":\"2026-12-01T12:30:00Z\"," +
                "\"endsAt\":\"2026-12-01T13:30:00Z\",\"headsUpMinutes\":60,\"platforms\":null}}";
            var state = Build(body, Context(), i => i.JustUpdated = true);
            Assert.Equal(ControlKind.WhatsNew, state.Kind);
            Assert.Contains("1.0.0", state.Title);
            Assert.Equal("Dark mode", state.Body);
            Assert.Equal(ControlKind.MaintenanceUpcoming, Assert.Single(state.Extras).Kind);
            var banner = Build(body, Context());
            Assert.Equal(ControlKind.MaintenanceUpcoming, banner.Kind);
            Assert.DoesNotContain("{", banner.Body);
        }

        [Fact]
        public void A_blocked_version_without_any_store_link_says_a_fix_is_on_the_way()
        {
            var state = Build("{\"format\":1,\"blocked\":[{\"version\":\"1.0.0\",\"platforms\":[\"windows\"],\"allowContinue\":false}]}", Context("windows"));
            Assert.Equal(ControlKind.VersionBlocked, state.Kind);
            Assert.Null(state.Url);
            Assert.Contains("A fix is on the way", state.Body);
        }

        [Fact]
        public void On_android_the_store_link_is_built_from_the_package_name_when_the_rules_have_none()
        {
            var state = Build("{\"format\":1,\"update\":{\"android\":{\"min\":\"2.0.0\",\"mode\":\"required\"}}}", Context("android"), i => i.CanBuildStoreLink = true);
            Assert.Equal(ControlKind.UpdateRequired, state.Kind);
            Assert.Equal("https://play.google.com/store/apps/details?id=dev.example.demo", state.Url);
        }

        [Fact]
        public void Platform_keys_come_from_the_operating_system_name()
        {
            Assert.Equal("ios", ControlPlatform.FromOsName("iOS"));
            Assert.Equal("ios", ControlPlatform.FromOsName("iPadOS"));
            Assert.Equal("macos", ControlPlatform.FromOsName("MacCatalyst"));
            Assert.Equal("windows", ControlPlatform.FromOsName("WinUI"));
            Assert.Equal("android", ControlPlatform.FromOsName(" Android "));
            Assert.Equal("linux", ControlPlatform.FromOsName("Linux"));
            Assert.Null(ControlPlatform.FromOsName("FreeBSD"));
            Assert.Null(ControlPlatform.FromOsName(null));
        }
    }
}
