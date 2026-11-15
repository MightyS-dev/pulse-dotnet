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
    public sealed class DeliveryTests
    {
        [Fact]
        public async Task BatchesOnCountBeforeInterval()
        {
            var h = new Harness(new PulseOptions { MaxBatchSize = 3 });
            h.Runtime.Track("one"); h.Runtime.Track("two"); await h.Client.ProcessAsync(); Assert.Empty(h.Transport.Requests);
            h.Runtime.Track("three"); await h.Client.ProcessAsync();
            Assert.Equal(3, Harness.Events(h.Transport.Requests).Length); Assert.Equal(0, h.Client.Count);
        }

        [Fact]
        public async Task BatchesAtInclusiveTimeBoundary()
        {
            var h = new Harness(); h.Runtime.Track("one"); await h.Client.ProcessAsync();
            h.Clock.Advance(19.999); await h.Client.ProcessAsync(); Assert.Empty(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(); Assert.Single(h.Transport.Requests);
        }

        [Fact]
        public async Task PauseAndFocusFlushDrainQueueAndAwaitDiskAcknowledgment()
        {
            var h = new Harness(); h.Runtime.Track("before.pause");
            var flush = h.Runtime.Flush(); Assert.False(flush.IsCompleted);
            await h.Client.ProcessAsync(); await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(h.Transport.Requests);
            Assert.Empty(QueueJournal.Decode(h.Storage.Read(h.Client.QueueName), 1000));
        }

        [Fact]
        public async Task FlushWaitsForInflightRequestAndDoesNotDuplicateIt()
        {
            var h = new Harness(); var response = new TaskCompletionSource<TransportResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Transport.Handler = (_, __) => response.Task;
            h.Runtime.Track("event"); var flush = h.Runtime.Flush(); var process = h.Client.ProcessAsync();
            Assert.False(flush.IsCompleted); Assert.Single(h.Transport.Requests);
            response.SetResult(new TransportResponse(202)); await process; await flush;
            await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
        }

        [Theory]
        [InlineData(0)] [InlineData(500)] [InlineData(503)] [InlineData(599)]
        public async Task RetriedBatchIsByteIdenticalIncludingGzip(int status)
        {
            var h = new Harness(); h.Transport.Reply(status, 202);
            await h.Send(props: new Dictionary<string, object> { ["large"] = new string('z', 2000) });
            Assert.Single(h.Transport.Requests); h.Runtime.Track("later");
            await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(1); await h.Client.ProcessAsync(true);
            var requests = h.Transport.Requests.ToArray(); Assert.Equal(3, requests.Length);
            Assert.True(requests[0].IsGzip); Assert.Equal(requests[0].Body, requests[1].Body);
            Assert.Equal("later", Harness.Event(requests[2]).GetProperty("type").GetString());
        }

        [Fact]
        public async Task ThrownNetworkFailureIsRetried()
        {
            var h = new Harness(); h.Transport.Handler = (_, __) => throw new IOException("Synthetic network failure.");
            await h.Send(); Assert.Equal(1, h.Client.Count);
            h.Transport.Handler = null; h.Clock.Advance(1); await h.Client.ProcessAsync(true);
            Assert.Equal(0, h.Client.Count);
            Assert.Equal(h.Transport.Requests.First().Body, h.Transport.Requests.Last().Body);
        }

        [Theory]
        [InlineData(429, false)] [InlineData(429, true)]
        [InlineData(503, false)] [InlineData(503, true)]
        public async Task RetryAfterIsHonoredForSecondsAndHttpDate(int status, bool date)
        {
            var h = new Harness(); var retry = date ? h.Clock.UtcNow.AddSeconds(60).ToString("R") : "60";
            h.Transport.Responses.Enqueue(new TransportResponse(status, retryAfter: retry));
            await h.Send(); h.Clock.Advance(59.999); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(2, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task BackoffDoublesAndCapsAtFiveMinutes()
        {
            var h = new Harness(); h.Transport.Handler = (_, __) => Task.FromResult(new TransportResponse(500));
            await h.Send();
            foreach (var delay in new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 300, 300 })
            {
                var before = h.Transport.Requests.Count; h.Clock.Advance(delay - 0.001);
                await h.Client.ProcessAsync(true); Assert.Equal(before, h.Transport.Requests.Count);
                h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(before + 1, h.Transport.Requests.Count);
            }
        }

        // Replaces the former "split once" rule: splitting continues until single bad signals are isolated.
        [Theory]
        [InlineData(400)] [InlineData(413)]
        public async Task InvalidBatchIsSplitUntilBadSignalsAreIsolatedAndInvalidOnesAreNeverRetried(int status)
        {
            var h = new Harness(new PulseOptions { MaxBatchSize = 4, Gzip = false }); h.Transport.Reply(status, status, 202);
            for (var i = 0; i < 4; i++) h.Runtime.Track("event", i);
            await h.Client.ProcessAsync();
            var requests = h.Transport.Requests.ToArray();
            // [0,1,2,3] fails, [0,1] fails, then [0], [1] and [2,3] succeed.
            Assert.Equal(new[] { 4, 2, 1, 1, 2 }, requests.Select(r => Harness.Events(new[] { r }).Length));
            Assert.Equal(0, h.Client.Count); h.Clock.Advance(1000); await h.Client.ProcessAsync(true); Assert.Equal(5, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task SingleBadSignalIsDroppedWithoutRetry()
        { var h = new Harness(); h.Transport.Reply(400); await h.Send(); h.Clock.Advance(500); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests); Assert.Equal(0, h.Client.Count); }

        [Fact]
        public async Task UnauthorizedHaltsUntilRestartAndRetainsQueue()
        {
            var h = new Harness(); h.Transport.Reply(401); h.Runtime.Track("one"); var flush = h.Runtime.Flush();
            await h.Client.ProcessAsync(); await flush; h.Runtime.Track("two"); h.Clock.Advance(300);
            await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests); Assert.Equal(2, h.Client.Count);
            var restarted = new Harness(storage: h.Storage); await restarted.Client.ProcessAsync(true);
            Assert.Equal(new[] { "one", "two" }, Harness.Events(restarted.Transport.Requests).Select(e => e.GetProperty("type").GetString()));
        }

        [Fact]
        public async Task GzipFallbackIsImmediateAndLastsUntilRestart()
        {
            var h = new Harness(); h.Transport.Reply(415, 202);
            var props = new Dictionary<string, object> { ["large"] = new string('a', 2000) };
            await h.Send(props: props); var requests = h.Transport.Requests.ToArray(); Assert.Equal(2, requests.Length);
            Assert.True(requests[0].IsGzip); Assert.False(requests[1].IsGzip);
            Assert.Equal(Harness.JsonBytes(requests[0]), requests[1].Body);
            await h.Send(props: props); Assert.False(h.Transport.Requests.Last().IsGzip);
            var restarted = new Harness(storage: h.Storage); await restarted.Send(props: props); Assert.True(restarted.Transport.Requests.Last().IsGzip);
        }

        [Fact]
        public async Task GzipRoundTripsToExactlyTheUncompressedJson()
        {
            var a = new Harness(); var b = new Harness(new PulseOptions { Gzip = false });
            var input = SignalJson.Create("large", 1.25, new Dictionary<string, object> { ["x"] = new string('\u017e', 2000) },
                a.Clock.UtcNow, "fixed-session", null, _ => { });
            a.Client.Enqueue(input); b.Client.Enqueue(input); await a.Client.ProcessAsync(true); await b.Client.ProcessAsync(true);
            Assert.Equal(Assert.Single(b.Transport.Requests).Body, Harness.JsonBytes(Assert.Single(a.Transport.Requests)));
        }

        [Theory]
        [InlineData(1024, false)] [InlineData(1025, true)]
        public async Task GzipThresholdIsStrictlyGreaterThanOneKiB(int desiredSize, bool compressed)
        {
            var h = new Harness();
            var input = SignalJson.Create("s", null, new Dictionary<string, object> { ["x"] = "" }, h.Clock.UtcNow, "fixed", null, _ => { });
            var baseline = Encoding.UTF8.GetByteCount(SignalJson.Serialize(input, SignalJson.SystemJson(new FakeEnvironment(), new PulseOptions()), false)) + 2;
            input = SignalJson.Create("s", null, new Dictionary<string, object> { ["x"] = new string('a', desiredSize - baseline) }, h.Clock.UtcNow, "fixed", null, _ => { });
            h.Client.Enqueue(input); await h.Client.ProcessAsync(true);
            var request = Assert.Single(h.Transport.Requests); Assert.Equal(desiredSize, Harness.JsonBytes(request).Length); Assert.Equal(compressed, request.IsGzip);
        }

        [Fact]
        public async Task RequestBodyAndBatchCountStayInsideServerLimits()
        {
            var h = new Harness(new PulseOptions { MaxQueueSize = 2000, MaxBatchSize = 9000, Gzip = false });
            for (var i = 0; i < 1200; i++) h.Runtime.Track("event", i, new Dictionary<string, object> { ["x"] = new string('a', 8000) });
            await h.Client.ProcessAsync(true);
            Assert.Equal(1200, Harness.Events(h.Transport.Requests).Length);
            Assert.All(h.Transport.Requests, request => { Assert.InRange(request.Body.Length, 1, 1048576); Assert.InRange(Harness.Events(new[] { request }).Length, 1, 1000); });
        }

        [Fact]
        public async Task DataIsPersistedBeforeTransportStarts()
        {
            var h = new Harness();
            QueueEntry[] disk = null;
            h.Transport.Handler = (r, _) =>
            {
                disk = QueueJournal.Decode(h.Storage.Read(h.Client.QueueName), 1000).ToArray();
                return Task.FromResult(new TransportResponse(202));
            };
            await h.Send("durable"); var request = Assert.Single(h.Transport.Requests);
            Assert.NotNull(disk);
            Assert.Equal("[" + string.Join(",", disk.Select(e => e.Json)) + "]", Encoding.UTF8.GetString(Harness.JsonBytes(request)));
        }

        [Fact]
        public async Task CanceledFlushNeverThrows()
        {
            var h = new Harness(); h.Transport.Reply(500); h.Runtime.Track("offline");
            using var cancellation = new CancellationTokenSource(); var flush = h.Runtime.Flush(cancellation.Token);
            await h.Client.ProcessAsync(); cancellation.Cancel(); await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, h.Client.Count);
        }
    }
}
