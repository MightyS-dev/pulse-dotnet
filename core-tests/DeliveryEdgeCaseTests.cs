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
    // Opt-out visibility, retry and backoff edge cases, batch splitting, flush coalescing and wire details.
    public sealed class DeliveryEdgeCaseTests
    {
        // A settings toggle that reads OptOut and writes it back must not erase a saved opt-out.
        [Fact]
        public async Task SavedOptOutIsVisibleBeforeTheWorkerLoadsSoAToggleCannotOverwriteIt()
        {
            var storage = new MemoryStorage(); storage.Replace("opt-out", new byte[] { 1 });
            var h = new Harness(storage: storage);
            Assert.True(h.Runtime.OptOut);
            h.Runtime.OptOut = h.Runtime.OptOut; // UI toggle bound to the current value.
            h.Runtime.Track("must.not.send"); await h.Client.ProcessAsync(true);
            Assert.Empty(h.Transport.Requests);
            var restarted = new Harness(storage: storage); Assert.True(restarted.Runtime.OptOut);
            await restarted.Client.ProcessAsync(true); Assert.True(restarted.Runtime.OptOut);
        }

        [Fact]
        public async Task SavedOptOutIsVisibleBeforeInitializeAndDropsEarlySignals()
        {
            var storage = new MemoryStorage(); storage.Replace("opt-out", new byte[] { 1 });
            var h = new Harness(storage: storage, initialize: false);
            Assert.True(h.Runtime.OptOut);
            h.Runtime.Track("early"); h.Runtime.OptOut = h.Runtime.OptOut; h.Runtime.Initialize("synthetic-key");
            await h.Client.ProcessAsync(true); Assert.Empty(h.Transport.Requests); Assert.True(h.Runtime.OptOut);
            Assert.Equal(new byte[] { 1 }, storage.Read("opt-out"));
        }

        [Fact]
        public void SavedOptInIsReportedAsFalseBeforeLoad()
        {
            var storage = new MemoryStorage(); storage.Replace("opt-out", new byte[] { 0 });
            Assert.False(new Harness(storage: storage).Runtime.OptOut);
            Assert.False(new Harness(initialize: false).Runtime.OptOut);
        }

        // Statuses 408 and 425 are transient; unknown non-success statuses keep the batch and back off instead of dropping.
        [Theory]
        [InlineData(408)] [InlineData(425)] [InlineData(403)] [InlineData(404)] [InlineData(409)]
        [InlineData(422)] [InlineData(302)] [InlineData(418)] [InlineData(415)] [InlineData(100)]
        public async Task TransientAndUnknownStatusesRetainTheExactBatchAndBackOff(int status)
        {
            var h = new Harness(); h.Transport.Reply(status, 202);
            await h.Send("kept"); Assert.Equal(1, h.Client.Count); Assert.False(h.Transport.Requests.Single().IsGzip);
            h.Clock.Advance(0.999); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true);
            Assert.Equal(2, h.Transport.Requests.Count); Assert.Equal(0, h.Client.Count);
            Assert.Equal(h.Transport.Requests.First().Body, h.Transport.Requests.Last().Body);
        }

        [Fact]
        public async Task UnknownStatusBacksOffExponentially()
        {
            var h = new Harness(); h.Transport.Handler = (_, __) => Task.FromResult(new TransportResponse(404));
            await h.Send();
            foreach (var delay in new[] { 1, 2, 4, 8 })
            {
                var before = h.Transport.Requests.Count; h.Clock.Advance(delay - 0.001);
                await h.Client.ProcessAsync(true); Assert.Equal(before, h.Transport.Requests.Count);
                h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(before + 1, h.Transport.Requests.Count);
            }
            Assert.Equal(1, h.Client.Count);
        }

        // Retry-After is capped at one hour, for seconds and for HTTP dates.
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task RetryAfterIsCappedAtOneHour(bool date)
        {
            var h = new Harness();
            var retry = date ? h.Clock.UtcNow.AddDays(3).ToString("R") : "86400";
            h.Transport.Responses.Enqueue(new TransportResponse(429, retryAfter: retry));
            await h.Send(); h.Clock.Advance(3599.999); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(2, h.Transport.Requests.Count);
        }

        // A persisted retry deadline further away than the cap (older build or corrected clock) is reset on restore.
        [Fact]
        public async Task RestoredRetryDeadlineBeyondTheCapIsReset()
        {
            var h = new Harness(); h.Transport.Responses.Enqueue(new TransportResponse(503, retryAfter: "3600"));
            await h.Send("deadline");
            var restarted = new Harness(storage: h.Storage);
            restarted.Clock.Advance(-1800); // The device clock was corrected backwards by 30 minutes.
            await restarted.Client.ProcessAsync(true);
            Assert.Equal(h.Transport.Requests.First().Body, Assert.Single(restarted.Transport.Requests).Body);
        }

        [Fact]
        public async Task RestoredRetryDeadlineWithinTheCapIsKept()
        {
            var h = new Harness(); h.Transport.Responses.Enqueue(new TransportResponse(503, retryAfter: "3000"));
            await h.Send("deadline");
            var restarted = new Harness(storage: h.Storage); restarted.Clock.Advance(2999);
            await restarted.Client.ProcessAsync(true); Assert.Empty(restarted.Transport.Requests);
            restarted.Clock.Advance(1); await restarted.Client.ProcessAsync(true); Assert.Single(restarted.Transport.Requests);
        }

        // Isolation keeps splitting until each bad signal is alone; all good signals are delivered.
        [Theory]
        [InlineData(400)] [InlineData(413)]
        public async Task SplittingIsolatesEveryBadSignalWithBoundedRequests(int status)
        {
            var bad = new HashSet<int> { 2, 5 };
            var h = new Harness(new PulseOptions { MaxBatchSize = 8, Gzip = false });
            var delivered = new List<int>();
            h.Transport.Handler = (request, _) =>
            {
                var values = Harness.Events(new[] { request }).Select(e => e.GetProperty("value").GetInt32()).ToArray();
                if (values.Any(bad.Contains)) return Task.FromResult(new TransportResponse(status));
                delivered.AddRange(values); return Task.FromResult(new TransportResponse(202));
            };
            for (var i = 0; i < 8; i++) h.Runtime.Track("event", i);
            await h.Client.ProcessAsync();
            Assert.Equal(new[] { 0, 1, 3, 4, 6, 7 }, delivered.OrderBy(v => v));
            Assert.Equal(0, h.Client.Count);
            Assert.InRange(h.Transport.Requests.Count, 1, 2 * 8 - 1);
            var count = h.Transport.Requests.Count; h.Clock.Advance(1000); await h.Client.ProcessAsync(true);
            Assert.Equal(count, h.Transport.Requests.Count);
        }

        // A 413 for a gzip body (possible expansion ratio limit) is retried once uncompressed before splitting.
        [Fact]
        public async Task GzipPayloadTooLargeIsRetriedUncompressedBeforeSplitting()
        {
            var h = new Harness(); h.Transport.Reply(413, 202);
            var props = new Dictionary<string, object> { ["large"] = new string('q', 4000) };
            h.Runtime.Track("one", props: props); h.Runtime.Track("two", props: props); await h.Client.ProcessAsync(true);
            var requests = h.Transport.Requests.ToArray(); Assert.Equal(2, requests.Length);
            Assert.True(requests[0].IsGzip); Assert.False(requests[1].IsGzip);
            Assert.Equal(Harness.JsonBytes(requests[0]), requests[1].Body); Assert.Equal(0, h.Client.Count);
            // Compression stays enabled for later batches.
            await h.Send("three", props: props); Assert.True(h.Transport.Requests.Last().IsGzip);
        }

        [Fact]
        public async Task UncompressedRetryThatStillFailsWith413IsSplit()
        {
            var h = new Harness(); h.Transport.Reply(413, 413, 202, 202);
            var props = new Dictionary<string, object> { ["large"] = new string('q', 4000) };
            h.Runtime.Track("one", props: props); h.Runtime.Track("two", props: props); await h.Client.ProcessAsync(true);
            Assert.Equal(new[] { 2, 2, 1, 1 }, h.Transport.Requests.Select(r => Harness.Events(new[] { r }).Length));
            Assert.Equal(0, h.Client.Count);
        }

        // Repeated pause and focus events while offline share one flush wait instead of piling up waiters.
        [Fact]
        public async Task BackgroundFlushesAreCoalescedWhileOffline()
        {
            var h = new Harness(); h.Transport.Handler = (_, __) => Task.FromResult(new TransportResponse(0));
            h.Runtime.Track("offline");
            var first = h.Runtime.FlushInBackground(TimeSpan.FromMinutes(1));
            for (var i = 0; i < 50; i++) Assert.Same(first, h.Runtime.FlushInBackground(TimeSpan.FromMinutes(1)));
            await h.Client.ProcessAsync();
            Assert.Equal(1, h.Client.WaiterCount); Assert.False(first.IsCompleted);
            h.Runtime.ReleaseFlushWaiters(); await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, h.Client.WaiterCount); Assert.Equal(1, h.Client.Count);
            Assert.NotSame(first, h.Runtime.FlushInBackground(TimeSpan.FromMinutes(1)));
        }

        [Fact]
        public async Task BackgroundFlushDeadlineEndsTheWait()
        {
            var h = new Harness(); h.Transport.Handler = (_, __) => Task.FromResult(new TransportResponse(0));
            h.Runtime.Track("offline");
            var flush = h.Runtime.FlushInBackground(TimeSpan.FromMilliseconds(50));
            await h.Client.ProcessAsync(); await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, h.Client.WaiterCount); Assert.Equal(1, h.Client.Count);
        }

        // The blocked-main-thread release completes a Flush that a caller is blocking on, without delivery.
        [Fact]
        public async Task ReleasedFlushUnblocksASynchronousWait()
        {
            var h = new Harness(); h.Transport.Handler = (_, __) => Task.FromResult(new TransportResponse(0));
            h.Runtime.Track("offline"); var flush = h.Runtime.Flush();
            var release = Task.Run(async () => { await Task.Delay(50); h.Runtime.ReleaseFlushWaiters(); });
            Assert.True(BlockingWait(flush, TimeSpan.FromSeconds(5))); await release;
            Assert.Equal(1, h.Client.Count);
        }

        // The final save is bounded and does not wait for a transport that never completes (for example a
        // request that needs the blocked main thread). Signals captured during the request are saved.
        [Fact]
        public async Task StopSavesQueueWithinTwoSecondsWhileTransportHangs()
        {
            var h = new Harness(); var never = new TaskCompletionSource<TransportResponse>();
            h.Transport.Handler = (_, __) => never.Task;
            h.Runtime.Track("in.flight"); var processing = h.Client.ProcessAsync(true);
            Assert.Single(h.Transport.Requests);
            h.Runtime.Track("during.request");
            var stop = h.Runtime.StopAsync();
            Assert.True(BlockingWait(stop, Pulse.FinalSaveTimeout)); await processing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(h.Runtime.Flush().IsCompleted);
            var restarted = new Harness(storage: h.Storage); restarted.Clock.Advance(1); await restarted.Client.ProcessAsync(true);
            Assert.Equal(new[] { "in.flight", "during.request" }, Harness.Events(restarted.Transport.Requests).Select(e => e.GetProperty("type").GetString()));
        }

        // Only the OS version number is sent; never the Android firmware build id.
        [Theory]
        [InlineData("Android OS 14 / API-34 (UP1A.231005.007/10754064)", "14")]
        [InlineData("Android OS 8.1.0 / API-27 (OPM1.171019.026/4565142)", "8.1")]
        [InlineData("Android OS 13 / API-33 (TP1A.220624.014/S918BXXU1AWBD)", "13")]
        [InlineData("Android OS 15 / API-35 (AP3A.240905.015.A2/12231197)", "15")]
        [InlineData("Android OS / API-34 (UP1A.231005.007/10754064)", "")]
        [InlineData("iOS 17.0.3", "17.0.3")]
        [InlineData("iPadOS 17.1", "17.1")]
        [InlineData("iPhone OS 16.6.1", "16.6.1")]
        [InlineData("Windows 11  (10.0.22631) 64bit", "11")]
        [InlineData("Windows 10  (10.0.19045) 64bit", "10")]
        [InlineData("Mac OS X 14.1.1", "14.1.1")]
        [InlineData("macOS 27.2.0", "27.2")]
        [InlineData("10.0.26100.0", "10.0.26100")]
        [InlineData("iOS 18.0.3 (private)", "18.0.3")]
        [InlineData("macOS 15.0", "15.0")]
        [InlineData("Linux 6.5 Ubuntu 23.10 64bit", "6.5")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("Unknown", "")]
        public void OsVersionParserKeepsOnlyTheVersionNumber(string description, string expected)
        {
            var parsed = SignalJson.OsVersionNumber(description);
            Assert.Equal(expected, parsed);
            Assert.DoesNotContain("API", parsed); Assert.DoesNotContain("/", parsed);
        }

        // Props keep caller order, because the server keeps the first entries in input order under its caps.
        [Fact]
        public async Task PropsKeepCallerOrderAndDuplicateKeysKeepTheirFirstPosition()
        {
            var h = new Harness();
            await h.Send(props: new Dictionary<string, object> { ["zeta"] = 1, ["alpha"] = "a", ["mid"] = true });
            Assert.Equal("{\"zeta\":1,\"alpha\":\"a\",\"mid\":\"true\"}", Harness.Event(h.Transport.Requests.Last()).GetProperty("props").GetRawText());
            await h.Send(props: new Dictionary<string, object> { ["b\ud800"] = "first", ["a"] = 2, ["b\udc00"] = "last" });
            Assert.Equal("{\"b�\":\"last\",\"a\":2}", Harness.Event(h.Transport.Requests.Last()).GetProperty("props").GetRawText());
        }

        // Queue age is capped at 29 days, one day inside the server's 30-day timestamp window.
        [Theory]
        [InlineData(60, 29)] [InlineData(29.5, 29)] [InlineData(3, 3)]
        public void QueueAgeIsCappedAtTwentyNineDays(double requested, double expected)
            => Assert.Equal(expected, new PulseOptions { MaxQueueAgeDays = requested }.Snapshot().MaxQueueAgeDays);

        // The request timeout scales with the body size.
        [Theory]
        [InlineData(0, 10)] [InlineData(1024, 10)] [InlineData(16384, 11)] [InlineData(1048576, 74)] [InlineData(int.MaxValue, 90)]
        public void RequestTimeoutScalesWithBodySize(int bytes, int expected) => Assert.Equal(expected, TransportRequest.TimeoutFor(bytes));

        // Deliberately blocking, as game code or a quit callback would be.
        private static bool BlockingWait(Task task, TimeSpan timeout) => task.Wait(timeout);
    }
}
