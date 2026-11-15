using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class RegressionTests
    {
        [Fact]
        public async Task RestartRetainsExactInflightBytesWithoutMergingNewEvents()
        {
            var h = new Harness(); h.Transport.Reply(500);
            await h.Send("original", props: new Dictionary<string, object> { ["large"] = new string('x', 2000) });
            var original = Assert.Single(h.Transport.Requests);
            h.Runtime.Track("newer"); await h.Client.ProcessAsync();
            var restarted = new Harness(storage: h.Storage);
            await restarted.Client.ProcessAsync(true); Assert.Empty(restarted.Transport.Requests);
            restarted.Clock.Advance(1); await restarted.Client.ProcessAsync(true);
            var requests = restarted.Transport.Requests.ToArray(); Assert.Equal(2, requests.Length);
            Assert.Equal(original.IsGzip, requests[0].IsGzip); Assert.Equal(original.Body, requests[0].Body);
            Assert.Equal("newer", Harness.Event(requests[1]).GetProperty("type").GetString());
            Assert.Null(h.Storage.Read(h.Client.BatchName));
        }

        [Fact]
        public async Task CorruptInflightRecordPreservesValidJournal()
        {
            var h = new Harness(); h.Transport.Reply(500); await h.Send("retained");
            var bytes = h.Storage.Read(h.Client.BatchName); bytes[bytes.Length - 1] ^= 1; h.Storage.Replace(h.Client.BatchName, bytes);
            var restarted = new Harness(storage: h.Storage); await restarted.Client.ProcessAsync(true);
            Assert.Equal("retained", Harness.Event(Assert.Single(restarted.Transport.Requests)).GetProperty("type").GetString());
        }

        [Fact]
        public async Task NewEventsDuringPersistenceAreNotSentUntilDurable()
        {
            var clock = new FakeClock(); var transport = new FakeTransport(); var storage = new InjectingStorage();
            var client = new PulseClient("synthetic", new PulseOptions(), clock, transport, storage, new FakeEnvironment(), runWorker: false);
            var added = false; var durableAtSend = new List<string[]>();
            storage.AfterReplace = name =>
            {
                if (name != client.QueueName || added) return;
                added = true; client.Enqueue(Input("during.write", clock));
            };
            transport.Handler = (_, __) =>
            { durableAtSend.Add(QueueJournal.Decode(storage.Read(client.QueueName), 1000).Select(e => e.Json).ToArray()); return Task.FromResult(new TransportResponse(202)); };
            client.Enqueue(Input("before.write", clock)); await client.ProcessAsync(true);
            var requests = transport.Requests.ToArray(); Assert.Equal(2, requests.Length);
            for (var i = 0; i < requests.Length; i++)
                Assert.Equal("[" + string.Join(",", durableAtSend[i]) + "]", Encoding.UTF8.GetString(Harness.JsonBytes(requests[i])));
            Assert.Equal(new[] { "before.write", "during.write" }, Harness.Events(requests).Select(e => e.GetProperty("type").GetString()));
        }

        [Fact]
        public async Task WorkerSendsOnTimeWithoutCountOrExplicitFlush()
        {
            var clock = new SystemClock(); var transport = new FakeTransport();
            var client = new PulseClient("synthetic", new PulseOptions { FlushIntervalSeconds = 0.05 }, clock,
                transport, new MemoryStorage(), new FakeEnvironment());
            try
            {
                client.Enqueue(Input("timer", clock)); await Harness.Until(() => transport.Requests.Count == 1);
                Assert.Equal("timer", Harness.Event(Assert.Single(transport.Requests)).GetProperty("type").GetString());
            }
            finally { await client.StopAsync(); }
        }

        [Fact]
        public async Task TenThreadsCaptureWhileAutomaticWorkerPersistsAndSends()
        {
            var clock = new FakeClock(); var transport = new FakeTransport(); var runtime = new PulseRuntime(clock); PulseClient client = null;
            runtime.InstallPlatform((key, options, optOut) => client = new PulseClient(key, options, clock, transport,
                new MemoryStorage(), new FakeEnvironment(), initialOptOut: optOut), null);
            runtime.Initialize("synthetic", new PulseOptions { MaxQueueSize = 10000, MaxBatchSize = 1000 });
            try
            {
                var threads = Enumerable.Range(0, 10).Select(t => new Thread(() =>
                { for (var i = 0; i < 1000; i++) runtime.Track("parallel", t * 1000 + i); })).ToArray();
                foreach (var thread in threads) thread.Start(); foreach (var thread in threads) thread.Join();
                await runtime.Flush().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(Enumerable.Range(0, 10000), Harness.Events(transport.Requests).Select(e => e.GetProperty("value").GetInt32()).OrderBy(i => i));
            }
            finally { await client.StopAsync(); }
        }

        [Theory]
        [InlineData(0, 1, 1)] [InlineData(1, 1.5, 3)]
        public async Task JitterIsAppliedWithOneSecondLowerBound(double random, double first, double second)
        {
            var h = new Harness(random: () => random); h.Transport.Reply(500, 500, 202); await h.Send();
            h.Clock.Advance(first - 0.001); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(2, h.Transport.Requests.Count);
            h.Clock.Advance(second - 0.001); await h.Client.ProcessAsync(true); Assert.Equal(2, h.Transport.Requests.Count);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(3, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task ActualFileJournalSurvivesNewClientInstance()
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".tmp"));
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new FakeClock(); var firstTransport = new FakeTransport();
                var first = new PulseClient("synthetic", new PulseOptions(), clock, firstTransport, new FileStorage(directory), new FakeEnvironment(), runWorker: false);
                first.Enqueue(Input("on.disk", clock)); await first.ProcessAsync(); Assert.Empty(firstTransport.Requests);
                var secondTransport = new FakeTransport();
                var second = new PulseClient("synthetic", new PulseOptions(), clock, secondTransport, new FileStorage(directory), new FakeEnvironment(), runWorker: false);
                await second.ProcessAsync(); Assert.Equal("on.disk", Harness.Event(Assert.Single(secondTransport.Requests)).GetProperty("type").GetString());
            }
            finally
            {
                Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(directory));
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task StoredRetryAndJournalContainOnlyTheHashAndOptInClearsUserState()
        {
            var h = new Harness(); h.Runtime.SetUser("private.synthetic.value"); h.Transport.Reply(500); await h.Send();
            Assert.All(h.Storage.Files.Values, bytes => Assert.DoesNotContain("private.synthetic.value", Encoding.UTF8.GetString(bytes)));
            h.Runtime.OptOut = true; h.Runtime.SetUser("ignored.while.opted.out"); await h.Client.ProcessAsync(true);
            h.Runtime.OptOut = false; await h.Send("anonymous");
            Assert.False(Harness.Event(h.Transport.Requests.Last()).TryGetProperty("userHash", out _));
        }

        [Fact]
        public async Task BatchSizeCannotExceedOneThousandEvenForSmallEvents()
        {
            var h = new Harness(new PulseOptions { MaxQueueSize = 1200, MaxBatchSize = int.MaxValue });
            for (var i = 0; i < 1200; i++) h.Runtime.Track("small", i);
            await h.Client.ProcessAsync(true);
            Assert.Equal(new[] { 1000, 200 }, h.Transport.Requests.Select(r => Harness.Events(new[] { r }).Length));
        }

        private static SignalInput Input(string type, IClock clock) => SignalJson.Create(type, null, null, clock.UtcNow, "synthetic-session", null, _ => { });

        private sealed class InjectingStorage : IStorage
        {
            private readonly MemoryStorage storage = new MemoryStorage();
            internal Action<string> AfterReplace;
            public byte[] Read(string name) => storage.Read(name);
            public void Append(string name, byte[] bytes) => storage.Append(name, bytes);
            public void Replace(string name, byte[] bytes) { storage.Replace(name, bytes); AfterReplace?.Invoke(name); }
            public void Delete(string name) => storage.Delete(name);
        }
    }
}
