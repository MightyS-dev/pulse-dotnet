using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class SerializationTests
    {
        [Fact]
        public async Task MatchesFirstPartyFixtureStructureAndV1Headers()
        {
            var h = new Harness(); h.Runtime.SetUser("synthetic-user");
            await h.Send("purchase", 4.5, new Dictionary<string, object> { ["item"] = "coins", ["amount"] = 2 });
            var request = Assert.Single(h.Transport.Requests);
            Assert.Equal("synthetic-key", request.AppKey);
            Assert.Equal("/api/v1/signals", request.Url.AbsolutePath);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/v1.jsonc")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var actual = Harness.Event(request); var expected = fixture.RootElement[0];
            Assert.Equal(Names(expected), Names(actual));
            Assert.Equal(Names(expected.GetProperty("system")), Names(actual.GetProperty("system")));
            foreach (var key in Names(expected.GetProperty("system")))
                Assert.Equal(JsonValueKind.String, actual.GetProperty("system").GetProperty(key).ValueKind);
            Assert.Equal(Names(expected.GetProperty("props")), Names(actual.GetProperty("props")));
            foreach (var key in Names(expected.GetProperty("props")))
                Assert.Equal(expected.GetProperty("props").GetProperty(key).GetRawText(), actual.GetProperty("props").GetProperty(key).GetRawText());
            Assert.Equal("pulse-dotnet", actual.GetProperty("system").GetProperty("sdkName").GetString());
            Assert.EndsWith("Z", actual.GetProperty("timestamp").GetString());
            Assert.Equal(h.Clock.UtcNow, actual.GetProperty("timestamp").GetDateTimeOffset());
            Assert.False(actual.GetProperty("debug").GetBoolean());
        }
        private static string[] Names(JsonElement value) => value.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        [Fact]
        public async Task EscapesQuotesBackslashesEveryControlAndUnicode()
        {
            var text = "quote\"slash\\ / \u017e\u4e2d\U0001f680" + new string(Enumerable.Range(0, 32).Select(i => (char)i).ToArray());
            var h = new Harness(); await h.Send(text, props: new Dictionary<string, object> { [text] = text });
            var actual = Harness.Event(Assert.Single(h.Transport.Requests));
            Assert.Equal(text, actual.GetProperty("type").GetString());
            Assert.Equal(text, actual.GetProperty("props").GetProperty(text).GetString());
        }

        [Fact]
        public async Task ReplacesUnpairedSurrogatesWithValidUnicode()
        {
            var h = new Harness(); await h.Send("x\ud800", props: new Dictionary<string, object> { ["x"] = "\udc00" });
            var actual = Harness.Event(Assert.Single(h.Transport.Requests));
            Assert.Equal("x\ufffd", actual.GetProperty("type").GetString());
            Assert.Equal("\ufffd", actual.GetProperty("props").GetProperty("x").GetString());
        }

        [Theory]
        [InlineData("sr-RS")]
        [InlineData("de-DE")]
        public async Task NumbersAreInvariantAndBooleansBecomeStrings(string culture)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var props = new Dictionary<string, object> { ["true"] = true, ["false"] = false, ["byte"] = (byte)2,
                    ["sbyte"] = (sbyte)-2, ["short"] = (short)-3, ["ushort"] = (ushort)3, ["int"] = -4, ["uint"] = 4u,
                    ["long"] = long.MinValue, ["ulong"] = ulong.MaxValue, ["float"] = 1.25f, ["double"] = 2.5,
                    ["decimal"] = 3.75m, ["text"] = "hello" };
                var h = new Harness(); await h.Send("purchase", 4.99, props);
                var actual = Harness.Event(Assert.Single(h.Transport.Requests));
                Assert.Equal(4.99, actual.GetProperty("value").GetDouble());
                var values = actual.GetProperty("props");
                Assert.Equal("true", values.GetProperty("true").GetString());
                Assert.Equal("false", values.GetProperty("false").GetString());
                Assert.Equal(3.75m, values.GetProperty("decimal").GetDecimal());
                Assert.Equal(ulong.MaxValue, values.GetProperty("ulong").GetUInt64());
                Assert.Equal(long.MinValue, values.GetProperty("long").GetInt64());
                Assert.Equal(1.25, values.GetProperty("float").GetDouble());
                Assert.Equal(props.Count, values.EnumerateObject().Count());
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Fact]
        public async Task UnsupportedAndNonfinitePropertiesAreDroppedWithoutPoisoningBatch()
        {
            var h = new Harness(new PulseOptions { Debug = true });
            await h.Send(props: new Dictionary<string, object> { ["nan"] = double.NaN, ["inf"] = float.PositiveInfinity,
                ["nested"] = new Dictionary<string, object>(), ["null"] = null, ["date"] = DateTime.UtcNow, ["ok"] = 2 });
            Assert.Equal("{\"ok\":2}", Harness.Event(Assert.Single(h.Transport.Requests)).GetProperty("props").GetRawText());
            Assert.Equal(5, h.Logs.Count);
        }

        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData(" \t\n")]
        public async Task InvalidTypesAreRejected(string type)
        { var h = new Harness(); await h.Send(type); Assert.Empty(h.Transport.Requests); }

        [Fact]
        public async Task TypeUsesUtf16LimitAndValueMustBeFinite()
        {
            var h = new Harness(); h.Runtime.Track(new string('a', 201)); h.Runtime.Track("nan", double.NaN);
            h.Runtime.Track("inf", double.NegativeInfinity); h.Runtime.Track(new string('a', 200));
            await h.Client.ProcessAsync(true); Assert.Single(Harness.Events(h.Transport.Requests));
        }

        [Theory]
        [InlineData(8184, true)] [InlineData(8185, false)]
        public async Task PropsByteLimitIncludesJsonPunctuation(int count, bool accepted)
        {
            var h = new Harness(); await h.Send(props: new Dictionary<string, object> { ["x"] = new string('a', count) });
            Assert.Equal(accepted ? 1 : 0, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task PropsLimitUsesUtf8AndEscapedControlBytes()
        {
            var h = new Harness(); h.Runtime.Track("unicode", props: new Dictionary<string, object> { ["x"] = new string('\u4e2d', 2800) });
            h.Runtime.Track("control", props: new Dictionary<string, object> { ["x"] = new string('\0', 1400) });
            await h.Client.ProcessAsync(true); Assert.Empty(h.Transport.Requests);
        }

        [Fact]
        public async Task IdentityIsOffByDefaultAndAppVersionCanBeOverridden()
        {
            var h = new Harness(new PulseOptions { AppVersion = "custom" }); await h.Send();
            var system = Harness.Event(Assert.Single(h.Transport.Requests)).GetProperty("system");
            Assert.False(system.TryGetProperty("bundleId", out _)); Assert.False(system.TryGetProperty("signingCertSha256", out _));
            Assert.Equal("custom", system.GetProperty("appVersion").GetString());
            var enabled = new Harness(new PulseOptions { SendAppIdentity = true }); await enabled.Send();
            system = Harness.Event(Assert.Single(enabled.Transport.Requests)).GetProperty("system");
            Assert.Equal("dev.example.synthetic", system.GetProperty("bundleId").GetString());
            Assert.Equal(new string('a', 64), system.GetProperty("signingCertSha256").GetString());
        }
    }
}
