using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class RuntimeTests
    {
        [Fact]
        public async Task PreInitializationQueueKeepsNewest100AndOriginalUserState()
        {
            var h = new Harness(initialize: false); h.Runtime.SetUser("early.synthetic");
            for (var i = 0; i < 110; i++) h.Runtime.Track("early", i);
            h.Runtime.SetUser(null); h.Runtime.Initialize("synthetic-key"); await h.Client.ProcessAsync(true);
            var events = Harness.Events(h.Transport.Requests); Assert.Equal(100, events.Length);
            Assert.Equal(Enumerable.Range(10, 100).Select(i => (double)i), events.Select(e => e.GetProperty("value").GetDouble()));
            Assert.All(events, e => Assert.Equal(SignalJson.Hash("early.synthetic"), e.GetProperty("userHash").GetString()));
        }

        [Fact]
        public async Task InitializationBeforePlatformBootstrapStillDeliversBufferedCalls()
        {
            var clock = new FakeClock(); var runtime = new PulseRuntime(clock); runtime.Track("early"); runtime.Initialize("synthetic"); runtime.Track("later");
            var transport = new FakeTransport(); PulseClient client = null;
            runtime.InstallPlatform((key, options, optOut) => client = new PulseClient(key, options, clock, transport,
                new MemoryStorage(), new FakeEnvironment(), runWorker: false, initialOptOut: optOut), null);
            await client.ProcessAsync(true); Assert.Equal(2, Harness.Events(transport.Requests).Length);
        }

        [Fact]
        public async Task SessionsRollAfterThirtyMinutesOfInactivity()
        {
            var h = new Harness(); await h.Send("one"); h.Clock.Advance(1799); await h.Send("two");
            h.Clock.Advance(1800); await h.Send("three"); var events = Harness.Events(h.Transport.Requests);
            var first = events[0].GetProperty("sessionId").GetString(); Assert.True(Guid.TryParseExact(first, "N", out _));
            Assert.Equal(first, events[1].GetProperty("sessionId").GetString()); Assert.NotEqual(first, events[2].GetProperty("sessionId").GetString());
        }

        [Fact]
        public async Task ConcurrentTenThreadsTimesOneThousandHaveNoLossOrDuplication()
        {
            var h = new Harness(new PulseOptions { MaxQueueSize = 10000, MaxBatchSize = 1000 });
            var threads = Enumerable.Range(0, 10).Select(thread => new Thread(() =>
            { for (var i = 0; i < 1000; i++) h.Runtime.Track("concurrent", thread * 1000 + i); })).ToArray();
            foreach (var thread in threads) thread.Start(); foreach (var thread in threads) thread.Join();
            Assert.Equal(10000, h.Client.Count); await h.Client.ProcessAsync(true);
            var values = Harness.Events(h.Transport.Requests).Select(e => e.GetProperty("value").GetInt32()).OrderBy(i => i).ToArray();
            Assert.Equal(Enumerable.Range(0, 10000), values); Assert.Equal(0, h.Client.Count);
        }

        [Fact]
        public async Task AutomaticWorkerWakesOnBatchAndFlushWithoutPollingPerFrame()
        {
            var transport = new FakeTransport(); var client = new PulseClient("synthetic", new PulseOptions { MaxBatchSize = 2 },
                new SystemClock(), transport, new MemoryStorage(), new FakeEnvironment());
            try
            {
                var clock = new SystemClock();
                client.Enqueue(SignalJson.Create("a", null, null, clock.UtcNow, "session", null, _ => { }));
                client.Enqueue(SignalJson.Create("b", null, null, clock.UtcNow, "session", null, _ => { }));
                await Harness.Until(() => transport.Requests.Count == 1);
                client.Enqueue(SignalJson.Create("c", null, null, clock.UtcNow, "session", null, _ => { }));
                await client.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(3, Harness.Events(transport.Requests).Length);
            }
            finally { await client.StopAsync(); }
        }

        [Fact]
        public async Task PublicCallsNeverThrowForInvalidInputOrFailingLoggerAndFactory()
        {
            var h = new Harness(new PulseOptions { Debug = true });
            h.Runtime.Track(null); h.Runtime.Track(new string('x', 201)); h.Runtime.Track("bad", double.NaN);
            h.Runtime.Initialize(null); h.Runtime.Initialize("ignored"); h.Runtime.SetUser(""); h.Runtime.OptOut = true; await h.Runtime.Flush(new CancellationToken(true));
            var runtime = new PulseRuntime(h.Clock);
            runtime.InstallPlatform((_, __, ___) => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
            runtime.Initialize("synthetic", new PulseOptions { Debug = true }); runtime.Track("safe"); runtime.SetUser("safe"); runtime.OptOut = true; await runtime.Flush();
            Pulse.Track(null); Pulse.Initialize(null); Pulse.Initialize("synthetic", new PulseOptions { Host = "invalid" });
            Pulse.SetUser(null); Pulse.OptOut = true; await Pulse.Flush(); Assert.True(Pulse.OptOut); Pulse.Reset();
        }

        // Editor with domain reload disabled: Reset must return only after the old client's final save, so the next
        // client never loads a queue that is still being written. Kept in this class because it uses the static facade.
        [Fact]
        public void ResetWaitsForTheOldClientsFinalSave()
        {
            var storage = new SlowStorage(); PulseClient client = null;
            var transport = new FakeTransport { Handler = (_, __) => Task.FromResult(new TransportResponse(0)) };
            Pulse.InstallPlatform((key, options, optOut) => client = new PulseClient(key, options, new SystemClock(), transport,
                storage, new FakeEnvironment(), initialOptOut: optOut), false, null, () => PulseClient.ReadStoredOptOut(storage));
            try
            {
                Pulse.Initialize("synthetic"); Pulse.Track("saved.on.reset");
                Pulse.Reset();
                var saved = QueueJournal.Decode(storage.Read(client.QueueName), 1000);
                Assert.Contains(saved, e => e.Json.Contains("saved.on.reset"));
                Assert.False(Pulse.OptOut);
            }
            finally { Pulse.Reset(); }
        }

        private sealed class SlowStorage : IStorage
        {
            private readonly MemoryStorage storage = new MemoryStorage();
            private readonly object gate = new object();
            public byte[] Read(string name) { lock (gate) return storage.Read(name); }
            public void Append(string name, byte[] bytes) { Thread.Sleep(150); lock (gate) storage.Append(name, bytes); }
            public void Replace(string name, byte[] bytes) { Thread.Sleep(150); lock (gate) storage.Replace(name, bytes); }
            public void Delete(string name) { lock (gate) storage.Delete(name); }
        }

        [Fact]
        public async Task OptionsAreSnapshottedAndInvalidHostCanBeCorrected()
        {
            var h = new Harness(initialize: false); h.Runtime.Initialize("synthetic", new PulseOptions { Host = "ftp://example.invalid" }); Assert.Null(h.Client);
            var options = new PulseOptions { Host = "http://localhost:9123/prefix", Debug = true, MaxBatchSize = 1 };
            h.Runtime.Initialize("synthetic", options); options.Host = "https://example.invalid"; options.Debug = false; options.MaxBatchSize = 999;
            h.Runtime.Track("safe"); await h.Client.ProcessAsync(); var request = Assert.Single(h.Transport.Requests);
            Assert.Equal("http://localhost:9123/prefix/api/v1/signals", request.Url.AbsoluteUri); Assert.True(Harness.Event(request).GetProperty("debug").GetBoolean());
        }
    }
}
