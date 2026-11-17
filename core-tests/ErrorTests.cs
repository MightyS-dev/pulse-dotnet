using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ErrorTests
    {
        // One shared vector table: input -> output. Every Pulse SDK carries the same rows.
        public static IEnumerable<object[]> Vectors() => new[]
        {
            new object[] { "mail jane.doe+x@example.co.uk now", "mail <filtered> now" },
            new object[] { "GET https://example.invalid/v1/items?token=abc&id=7 failed", "GET https://example.invalid/v1/items<filtered> failed" },
            new object[] { "see https://example.invalid/docs/page#section-2 ok", "see https://example.invalid/docs/page<filtered> ok" },
            new object[] { "id 123e4567-e89b-12d3-a456-426614174000 missing", "id <filtered> missing" },
            new object[] { "host 172.16.10.25 refused", "host <filtered> refused" },
            new object[] { "peer 2001:0db8:85a3:0000:0000:8a2e:0370:7334 down", "peer <filtered> down" },
            new object[] { "peer 2001:db8::ff00:42:8329 down", "peer <filtered> down" },
            new object[] { "order 1234567 not found", "order <filtered> not found" },
            new object[] { "crash at MyApp+0x1234567 abs 0x00000071a2b3c4d5", "crash at MyApp+0x1234567 abs 0x00000071a2b3c4d5" },
            new object[] { "at 0x1234 now", "at 0x1234 now" },
            new object[] { "order 1234567 at MyApp+0x1234567", "order <filtered> at MyApp+0x1234567" },
            new object[] { "code 12345 kept", "code 12345 kept" },
            new object[] { "open /home/jane/projects/app/Config.json failed", "open Config.json failed" },
            new object[] { "open C:\\Work\\Jane\\app\\Config.json failed", "open Config.json failed" },
            new object[] { "MyApp.Cart.Checkout() /home/jane/src/Cart.cs:42", "MyApp.Cart.Checkout() Cart.cs:42" },
            new object[] { "file:///home/jane/src/Cart.cs here", "Cart.cs here" },
            new object[] { "Ns.Type::Method and 1/2 and a/b stay", "Ns.Type::Method and 1/2 and a/b stay" },
        };

        [Theory, MemberData(nameof(Vectors))]
        public void FilterVectorTable(string input, string expected) => Assert.Equal(expected, ErrorFilter.Apply(input));

        [Fact]
        public void FrameKeepsLineAndColumnAfterFileName()
        {
            Assert.Equal("checkout app.js:1:234567", ErrorFilter.Apply("checkout app.js:1:234567", true));
            Assert.Equal("Cart.swift:1234567", ErrorFilter.Apply("Cart.swift:1234567", true));
            Assert.Equal("order <filtered> Cart.kt:42", ErrorFilter.Apply("order 1234567 Cart.kt:42", true));
        }

        [Fact]
        public void MessageHasNoLineAndColumnExemption() =>
            Assert.Equal("failed at app.js:1:<filtered>", ErrorFilter.Apply("failed at app.js:1:234567"));

        private static Exception Thrown(string message = "boom")
        {
            try { Throw(message); return null; } catch (Exception e) { return e; }
        }
        private static void Throw(string message) => throw new InvalidOperationException(message);

        private static JsonElement ErrorOf(Harness h) =>
            Harness.Events(h.Transport.Requests).Single(e => e.GetProperty("type").GetString() == "pulse.error").GetProperty("error");

        [Fact]
        public void FramesNameTypeMethodFileAndLineWithoutDirectory()
        {
            var report = ErrorReport.FromException(Thrown(), "handled");
            Assert.Equal("InvalidOperationException", report.Type);
            Assert.True(report.Symbolicated);
            var top = report.Frames[0];
            Assert.StartsWith("Mightys.Pulse.Tests.ErrorTests.Throw() ErrorTests.cs:", top);
            Assert.DoesNotContain("/", top); Assert.DoesNotContain("\\", top);
        }

        [Fact]
        public void AnExceptionThatWasNeverThrownHasNoMethodNames()
        {
            var report = ErrorReport.FromException(new InvalidOperationException("x"), "handled");
            Assert.Empty(report.Frames); Assert.False(report.Symbolicated);
        }

        [Fact]
        public void AggregateExceptionShowsItsFirstInnerError()
        {
            var report = ErrorReport.FromException(new AggregateException(Thrown("inner problem")), "error");
            Assert.Equal("InvalidOperationException", report.Type); Assert.Equal("inner problem", report.Message);
        }

        private static void Recurse(int depth) { if (depth == 0) throw new InvalidOperationException("deep"); Recurse(depth - 1); Recurse(0); }

        [Fact]
        public void FramesAreLimitedToThirtyAndThreeHundredCharacters()
        {
            Exception deep = null;
            try { Recurse(60); } catch (Exception e) { deep = e; }
            Assert.Equal(30, ErrorReport.FromException(deep, "handled").Frames.Count);
            var json = ErrorJson.Serialize(new ErrorEvent { Kind = "handled", Type = "T", Frames = new List<string> { "x" }, Symbolicated = true }, PulseErrorDetail.Full);
            Assert.Contains("\"frames\":[\"x\"]", json);
            var long300 = ErrorReport.FromException(new InvalidOperationException(new string('m', 900)), "handled");
            Assert.Equal(500, long300.Message.Length);
        }

        [Fact]
        public void ErrorObjectIsAtMost16384BytesFramesGoFirstThenMessage()
        {
            var big = new ErrorEvent { Kind = "error", Type = "Big", Message = new string('m', 500), Symbolicated = true };
            for (var i = 0; i < 30; i++) big.Frames.Add(new string((char)('a' + i % 26), 200) + new string('\u0001', 100));
            var json = ErrorJson.Serialize(big, PulseErrorDetail.Full);
            Assert.True(Encoding.UTF8.GetByteCount(json) <= 16384);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("frames").GetArrayLength() < 30);
            Assert.Equal(500, doc.RootElement.GetProperty("message").GetString().Length);
            Assert.StartsWith("a", doc.RootElement.GetProperty("frames")[0].GetString());
            // Frames alone cannot make it fit: the message is cut too.
            var huge = new ErrorEvent { Kind = "error", Type = new string('\u0001', 200), Message = new string('\u0001', 500), Symbolicated = true };
            Assert.True(Encoding.UTF8.GetByteCount(ErrorJson.Serialize(huge, PulseErrorDetail.Full)) <= 16384);
        }

        [Fact]
        public async Task HandledErrorIsSentWithTypeFilteredMessageFramesAndProps()
        {
            var h = new Harness();
            h.Runtime.TrackError(Thrown("fail for jane@example.com at /home/jane/App.cs"), new Dictionary<string, object> { ["screen"] = "cart" });
            await h.Client.ProcessAsync(true);
            var signal = Harness.Events(h.Transport.Requests).Single(e => e.GetProperty("type").GetString() == "pulse.error");
            var error = signal.GetProperty("error");
            Assert.Equal("handled", error.GetProperty("kind").GetString());
            Assert.Equal("InvalidOperationException", error.GetProperty("type").GetString());
            Assert.Equal("fail for <filtered> at App.cs", error.GetProperty("message").GetString());
            Assert.True(error.GetProperty("symbolicated").GetBoolean());
            Assert.True(error.GetProperty("frames").GetArrayLength() > 0);
            Assert.Equal("cart", signal.GetProperty("props").GetProperty("screen").GetString());
            Assert.Equal(JsonValueKind.Null, signal.GetProperty("value").ValueKind);
        }

        [Fact]
        public async Task TypeOnlyOmitsMessageFramesAndSymbolicated()
        {
            var h = new Harness(new PulseOptions { ErrorDetail = PulseErrorDetail.TypeOnly });
            h.Runtime.TrackError(Thrown("secret"), null); await h.Client.ProcessAsync(true);
            var error = ErrorOf(h);
            Assert.Equal("InvalidOperationException", error.GetProperty("type").GetString());
            Assert.False(error.TryGetProperty("message", out _)); Assert.False(error.TryGetProperty("frames", out _));
            Assert.False(error.TryGetProperty("symbolicated", out _));
        }

        [Fact]
        public async Task AtMostTenErrorsPerSessionAndTypeThenTheCountStartsOver()
        {
            var h = new Harness();
            for (var i = 0; i < 15; i++) h.Runtime.TrackError(Thrown(), null);
            for (var i = 0; i < 3; i++) h.Runtime.TrackError(new ArgumentException("other"), null);
            await h.Client.ProcessAsync(true);
            var errors = Harness.Events(h.Transport.Requests).Where(e => e.GetProperty("type").GetString() == "pulse.error").ToArray();
            Assert.Equal(10, errors.Count(e => e.GetProperty("error").GetProperty("type").GetString() == "InvalidOperationException"));
            Assert.Equal(3, errors.Count(e => e.GetProperty("error").GetProperty("type").GetString() == "ArgumentException"));
            h.Clock.Advance(1900);
            h.Runtime.TrackError(Thrown(), null); await h.Client.ProcessAsync(true);
            Assert.Equal(14, Harness.Events(h.Transport.Requests).Count(e => e.GetProperty("type").GetString() == "pulse.error"));
        }

        [Fact]
        public async Task UnhandledErrorKindErrorIsQueuedAtOnce()
        {
            var h = new Harness();
            h.Runtime.CaptureUnhandled(Thrown(), "error"); await h.Client.ProcessAsync(true);
            Assert.Equal("error", ErrorOf(h).GetProperty("kind").GetString());
        }

        [Fact]
        public async Task CrashIsWrittenSynchronouslyAndSentAtTheNextLaunch()
        {
            var first = new Harness();
            first.Runtime.CaptureUnhandled(Thrown("it died"), "crash");
            Assert.True(first.Storage.Files.ContainsKey(first.Client.CrashName));
            Assert.Equal(0, first.Client.Count);
            Assert.Empty(first.Transport.Requests);
            var second = new Harness(storage: first.Storage);
            await second.Client.ProcessAsync(true);
            var error = ErrorOf(second);
            Assert.Equal("crash", error.GetProperty("kind").GetString());
            Assert.Equal("it died", error.GetProperty("message").GetString());
            Assert.False(second.Storage.Files.ContainsKey(second.Client.CrashName));
            var third = new Harness(storage: second.Storage); await third.Client.ProcessAsync(true);
            Assert.Empty(third.Transport.Requests);
        }

        // The worker is held where it reads the queue file: nothing is loaded when the crash arrives.
        private sealed class SlowQueueStorage : IStorage
        {
            private readonly object gate = new object();
            internal readonly MemoryStorage Inner = new MemoryStorage();
            internal readonly System.Threading.ManualResetEventSlim Release = new System.Threading.ManualResetEventSlim(false);
            public byte[] Read(string name) { if (name.StartsWith("queue-")) Release.Wait(TimeSpan.FromSeconds(20)); lock (gate) return Inner.Read(name); }
            public void Append(string name, byte[] bytes) { lock (gate) Inner.Append(name, bytes); }
            public void Replace(string name, byte[] bytes) { lock (gate) Inner.Replace(name, bytes); }
            public void Delete(string name) { lock (gate) Inner.Delete(name); }
        }

        private static (PulseRuntime Runtime, PulseClient Client, SlowQueueStorage Storage) StartedWithTheWorkerNotLoaded(byte[] storedOptOut = null)
        {
            var storage = new SlowQueueStorage(); var clock = new FakeClock(); PulseClient client = null;
            if (storedOptOut != null) storage.Inner.Files["opt-out"] = storedOptOut;
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, new FakeTransport(), storage, new FakeEnvironment(),
                null, true, () => 0.5, optOut), null, () => PulseClient.ReadStoredOptOut(storage));
            runtime.Initialize("synthetic-key");
            return (runtime, client, storage);
        }

        [Fact]
        public async Task CrashRightAfterInitializeIsKeptWhileTheWorkerHasLoadedNothingAndSentAtTheNextLaunch()
        {
            var (runtime, client, storage) = StartedWithTheWorkerNotLoaded();
            try
            {
                runtime.CaptureUnhandled(Thrown("right at the start"), "crash");
                Assert.True(storage.Inner.Files.ContainsKey(client.CrashName));
                var next = new MemoryStorage();
                foreach (var file in storage.Inner.Files) next.Files[file.Key] = file.Value.ToArray();
                var second = new Harness(storage: next); await second.Client.ProcessAsync(true);
                var error = ErrorOf(second);
                Assert.Equal("crash", error.GetProperty("kind").GetString());
                Assert.Equal("right at the start", error.GetProperty("message").GetString());
            }
            finally { storage.Release.Set(); }
        }

        [Fact]
        public async Task CrashRightAfterInitializeWithAStoredOptOutIsDeletedAtTheNextLaunchWithoutSending()
        {
            var (runtime, client, storage) = StartedWithTheWorkerNotLoaded();
            try
            {
                runtime.CaptureUnhandled(Thrown(), "crash");
                Assert.True(storage.Inner.Files.ContainsKey(client.CrashName));
                var next = new MemoryStorage();
                foreach (var file in storage.Inner.Files) next.Files[file.Key] = file.Value.ToArray();
                next.Files["opt-out"] = new byte[] { 1 };
                var second = new Harness(storage: next); await second.Client.ProcessAsync(true);
                Assert.Empty(second.Transport.Requests); Assert.False(next.Files.ContainsKey(second.Client.CrashName));
            }
            finally { storage.Release.Set(); }
        }

        [Fact]
        public async Task OptOutDropsSavedCrashes()
        {
            var first = new Harness();
            first.Runtime.CaptureUnhandled(Thrown(), "crash");
            first.Runtime.OptOut = true;
            await Harness.Until(() => !first.Storage.Files.ContainsKey(first.Client.CrashName));
            first.Runtime.CaptureUnhandled(Thrown(), "crash");
            Assert.False(first.Storage.Files.ContainsKey(first.Client.CrashName));
            first.Runtime.TrackError(Thrown(), null); Assert.Equal(0, first.Client.Count);
        }

        [Fact]
        public async Task OptOutBeforeTheNextLaunchLoadsDeletesTheCrashFile()
        {
            var first = new Harness();
            first.Runtime.CaptureUnhandled(Thrown(), "crash");
            var storage = first.Storage; storage.Files["opt-out"] = new byte[] { 1 };
            var second = new Harness(storage: storage); await second.Client.ProcessAsync(true);
            Assert.Empty(second.Transport.Requests); Assert.False(storage.Files.ContainsKey(second.Client.CrashName));
        }

        [Fact]
        public void CaptureErrorsFalseIgnoresUnhandledCapture()
        {
            var h = new Harness(new PulseOptions { CaptureErrors = false });
            Assert.False(h.Runtime.CaptureErrorsEnabled);
            h.Runtime.CaptureUnhandled(Thrown(), "error"); h.Runtime.CaptureUnhandled(Thrown(), "crash");
            Assert.Equal(0, h.Client.Count); Assert.False(h.Storage.Files.ContainsKey(h.Client.CrashName));
            Assert.True(new Harness().Runtime.CaptureErrorsEnabled);
        }

        [Fact]
        public void CaptureNeverThrowsEvenWhenStorageFailsAndTheCrashStaysQueued()
        {
            var h = new Harness(); h.Storage.Fail = true;
            h.Runtime.CaptureUnhandled(Thrown(), "crash"); h.Runtime.CaptureUnhandled(Thrown(), "error");
            h.Runtime.TrackError(Thrown(), null); h.Runtime.TrackError(null, null); h.Runtime.CaptureUnhandled(null, "crash");
            Assert.Equal(3, h.Client.Count);
        }

        [Fact]
        public async Task CrashAfterAnExistingCrashKeepsBoth()
        {
            var h = new Harness();
            h.Runtime.CaptureUnhandled(Thrown("one"), "crash"); h.Runtime.CaptureUnhandled(new ArgumentException("two"), "crash");
            var second = new Harness(storage: h.Storage); await second.Client.ProcessAsync(true);
            Assert.Equal(2, Harness.Events(second.Transport.Requests).Count(e => e.GetProperty("type").GetString() == "pulse.error"));
        }

        [Fact]
        public async Task ErrorSignalsAreNotAutomaticSignals()
        {
            var h = new Harness(new PulseOptions { AutomaticSignals = false });
            h.Runtime.TrackError(Thrown(), null); await h.Client.ProcessAsync(true);
            Assert.Single(Harness.Events(h.Transport.Requests), e => e.GetProperty("type").GetString() == "pulse.error");
        }
    }
}
