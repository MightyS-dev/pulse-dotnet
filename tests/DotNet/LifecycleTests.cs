using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class LifecycleTests
    {
        [Fact]
        public async Task ShutdownReturnsWithinBudgetWhenStorageIsBlockedAndSaveCanFinishLater()
        {
            using var storage = new BlockingStorage();
            var host = new PulseHost(storage: storage, transport: new FakeTransport(), usage: false);
            host.Initialize("synthetic", new PulseOptions(), Platform());
            await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
            storage.Block = true; host.Runtime.Track("save-later");
            await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var timer = Stopwatch.StartNew(); var stop = host.ShutdownAsync();
                Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500));
                Assert.Same(stop, host.ShutdownAsync());
                await stop.WaitAsync(TimeSpan.FromSeconds(4));
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3.5));
            }
            finally { storage.Release.Set(); }
            await host.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var name = storage.Inner.Files.Keys.Single(k => k.EndsWith(".bin", StringComparison.Ordinal));
            Assert.Single(QueueJournal.Decode(storage.Inner.Read(name), 1000));
        }

        [Fact]
        public async Task PauseAndFocusLossDrainWithoutWaitingForNormalInterval()
        {
            var transport = new FakeTransport(); var host = new PulseHost(storage: new MemoryStorage(), transport: transport, usage: false);
            host.Initialize("synthetic", new PulseOptions { FlushIntervalSeconds = 3600 }, Platform());
            try
            {
                host.Runtime.Track("pause"); await host.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var paused = Harness.Events(transport.Requests);
                Assert.Equal(new[] { "pause", "pulse.app_backgrounded" }, paused.Select(e => e.GetProperty("type").GetString()));
                host.Runtime.Track("focus"); Assert.True(host.FocusChangedAsync(true).IsCompleted);
                await host.FocusChangedAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(4, Harness.Events(transport.Requests).Length);
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task OfflineLifecycleCallsShareOneWaitAndShutdownCancelsStuckTransport()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<TransportResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new FakeTransport { Handler = (_, __) => { entered.TrySetResult(true); return pending.Task; } };
            var storage = new MemoryStorage(); var host = new PulseHost(storage: storage, transport: transport, usage: false);
            host.Initialize("synthetic", new PulseOptions(), Platform()); host.Runtime.Track("durable");
            var first = host.PauseAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 1000; i++) Assert.Same(first, host.FocusChangedAsync(false));
            var timer = Stopwatch.StartNew(); await host.ShutdownAsync();
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            var queue = storage.Files.Single(p => p.Key.EndsWith(".bin", StringComparison.Ordinal)).Value;
            Assert.Equal(2, QueueJournal.Decode(queue, 1000).Count);
            pending.TrySetResult(new TransportResponse(202));
        }

        [Fact]
        public async Task FileQueueAndExactPendingBytesSurviveShutdownAndRestart()
        {
            var directory = TemporaryDirectory();
            try
            {
                var firstTransport = new FakeTransport(); firstTransport.Reply(500);
                var first = new PulseHost(transport: firstTransport, usage: false);
                first.Initialize("synthetic", new PulseOptions { FlushIntervalSeconds = 3600 }, Platform(directory));
                first.Runtime.SetUser("raw-synthetic-user"); first.Runtime.Track("saved", props: new Dictionary<string, object> { ["x"] = new string('a', 2000) });
                using var cancellation = new CancellationTokenSource();
                var flush = first.Runtime.Flush(cancellation.Token);
                // Wait for the first send attempt instead of a fixed delay, so a loaded machine cannot cancel too early.
                for (var i = 0; i < 500 && firstTransport.Requests.Count == 0; i++) await Task.Delay(10);
                cancellation.Cancel();
                await flush;
                await first.ShutdownAsync();
                var sent = Assert.Single(firstTransport.Requests);
                Assert.All(Directory.GetFiles(directory), f => Assert.DoesNotContain("raw-synthetic-user", Encoding.UTF8.GetString(File.ReadAllBytes(f))));
                // This test uses SystemClock for capture. Advance a real-time snapshot past the saved retry deadline.
                var secondTransport = new FakeTransport();
                var second = new PulseHost(new OffsetClock(TimeSpan.FromSeconds(10)), transport: secondTransport, usage: false);
                second.Initialize("synthetic", new PulseOptions(), Platform(directory));
                try
                {
                    await second.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                    var retried = Assert.Single(secondTransport.Requests);
                    Assert.Equal(sent.Body, retried.Body); Assert.Equal(sent.IsGzip, retried.IsGzip);
                    Assert.Equal("pulse-dotnet", Harness.Event(retried).GetProperty("system").GetProperty("sdkName").GetString());
                }
                finally { await second.ShutdownAsync(); }
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public async Task PersistedOptOutIsLoadedBeforeCollectionAndSurvivesRestart()
        {
            var directory = TemporaryDirectory();
            try
            {
                new FileStorage(directory).Replace("opt-out", new byte[] { 1 });
                var transport = new FakeTransport(); var first = new PulseHost(transport: transport, usage: false);
                first.Initialize("synthetic", new PulseOptions(), Platform(directory));
                Assert.True(first.Runtime.OptOut); first.Runtime.Track("forbidden");
                await first.PauseAsync(); await first.ShutdownAsync(); Assert.Empty(transport.Requests);
                var second = new PulseHost(transport: transport, usage: false);
                second.Initialize("synthetic", new PulseOptions(), Platform(directory));
                try
                {
                    Assert.True(second.Runtime.OptOut); second.Runtime.OptOut = false;
                    second.Runtime.Track("allowed"); await second.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Single(transport.Requests);
                }
                finally { await second.ShutdownAsync(); }
                Assert.Equal(new byte[] { 0 }, new FileStorage(directory).Read("opt-out"));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public async Task PreInitializeQueueIdentitySessionsAndConcurrentTrackingUseSharedRuntime()
        {
            var clock = new FakeClock(); var transport = new FakeTransport();
            var host = new PulseHost(clock, new MemoryStorage(), transport, usage: false);
            host.Runtime.SetUser("early-user"); for (var i = 0; i < 110; i++) host.Runtime.Track("early", i);
            host.Runtime.SetUser(null);
            host.Initialize("synthetic", new PulseOptions { MaxQueueSize = 20000, MaxBatchSize = 1000 }, Platform());
            try
            {
                await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                var early = Harness.Events(transport.Requests); Assert.Equal(100, early.Length);
                Assert.All(early, e => Assert.Equal(SignalJson.Hash("early-user"), e.GetProperty("userHash").GetString()));
                Assert.Equal(Enumerable.Range(10, 100).Select(i => (double)i), early.Select(e => e.GetProperty("value").GetDouble()));
                clock.Advance(1800); host.Runtime.Track("new.session"); await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                var later = Harness.Event(transport.Requests.Last());
                Assert.NotEqual(early[0].GetProperty("sessionId").GetString(), later.GetProperty("sessionId").GetString());
                Assert.Equal(64, later.GetProperty("userHash").GetString().Length);
                Assert.NotEqual(SignalJson.Hash("early-user"), later.GetProperty("userHash").GetString());
                var tasks = Enumerable.Range(0, 10).Select(t => Task.Run(() =>
                { for (var i = 0; i < 1000; i++) host.Runtime.Track("concurrent", t * 1000 + i); }));
                await Task.WhenAll(tasks); await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                var numbers = Harness.Events(transport.Requests).Where(e => e.GetProperty("type").GetString() == "concurrent")
                    .Select(e => e.GetProperty("value").GetInt32()).OrderBy(x => x).ToArray();
                Assert.Equal(Enumerable.Range(0, 10000), numbers);
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task InvalidInitializeCanBeCorrectedAndOptionsAreCopied()
        {
            var transport = new FakeTransport(); var host = new PulseHost(storage: new MemoryStorage(), transport: transport, usage: false);
            host.Initialize(null, null); host.Initialize("synthetic", new PulseOptions { Host = "invalid" });
            var platform = Platform(); var options = new PulseOptions { AppVersion = "original" };
            host.Initialize("synthetic", options, platform); options.AppVersion = "mutated"; platform.EnvironmentInfo = null;
            try
            {
                host.Initialize("ignored", new PulseOptions()); host.Runtime.Track("safe");
                await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                var request = Assert.Single(transport.Requests); Assert.Equal("synthetic", request.AppKey);
                Assert.Equal("original", Harness.Event(request).GetProperty("system").GetProperty("appVersion").GetString());
            }
            finally { await host.ShutdownAsync(); }
        }

        private static PulsePlatformOptions Platform(string directory = null) => new PulsePlatformOptions
        { StorageDirectory = directory, EnvironmentInfo = new DotNetEnvironmentInfo("Windows", "11", "1.2.3", "42", "en-US") };
        private static string TemporaryDirectory()
        {
            var path = Path.Combine(AppContext.BaseDirectory, ".tmp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        private sealed class OffsetClock : IClock
        {
            private readonly TimeSpan offset;
            internal OffsetClock(TimeSpan offset) { this.offset = offset; }
            public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + offset;
            public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
        }
        private sealed class BlockingStorage : IStorage, IDisposable
        {
            internal readonly MemoryStorage Inner = new MemoryStorage();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal volatile bool Block;
            private void Check()
            { if (Block) { Entered.TrySetResult(true); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException(); } }
            public byte[] Read(string name) => Inner.Read(name);
            public void Append(string name, byte[] bytes) { Check(); Inner.Append(name, bytes); }
            public void Replace(string name, byte[] bytes) { Check(); Inner.Replace(name, bytes); }
            public void Delete(string name) { Check(); Inner.Delete(name); }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }
    }
}
