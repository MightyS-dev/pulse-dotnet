using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class AdviceTests
    {
        [Theory]
        [InlineData("")] [InlineData("{\"unknown\":{\"nested\":[true,false,null,\"text\"]}}")]
        [InlineData("null")] [InlineData("[]")] [InlineData("malformed")]
        [InlineData("{\"recommendedSendIntervalSeconds\":-1,\"pause\":-10}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":\"60\",\"pause\":\"true\"}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":0,\"pause\":false}")]
        [InlineData("{\"RecommendedSendIntervalSeconds\":60,\"Pause\":true}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":1e999}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":60,\"recommendedSendIntervalSeconds\":30}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":60,}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":60} trailing")]
        [InlineData("{\"recommendedSendIntervalSeconds\":01}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":NaN}")]
        public async Task EmptyUnknownInvalidOrNonsensicalAdviceDoesNotChangeSchedule(string body)
        {
            var h = new Harness(); h.Transport.Responses.Enqueue(new TransportResponse(202, body));
            await h.Send("first"); h.Runtime.Track("second"); h.Clock.Advance(19.999);
            await h.Client.ProcessAsync(); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(); Assert.Equal(2, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task BooleanPauseUsesRecommendedIntervalEvenForExplicitFlushAndFullBatches()
        {
            var h = new Harness(new PulseOptions { MaxBatchSize = 2 });
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":120,\"pause\":true,\"error\":null}"));
            await h.Send("first"); h.Runtime.Track("paused.one"); h.Runtime.Track("paused.two"); var flush = h.Runtime.Flush();
            h.Clock.Advance(119.999); await h.Client.ProcessAsync(true);
            Assert.Single(h.Transport.Requests); Assert.False(flush.IsCompleted); Assert.Equal(2, h.Client.Count);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(); await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, h.Transport.Requests.Count); Assert.Equal(0, h.Client.Count);
            h.Runtime.Track("third"); h.Clock.Advance(119.999); await h.Client.ProcessAsync(); Assert.Equal(2, h.Transport.Requests.Count);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(); Assert.Equal(3, h.Transport.Requests.Count);
        }

        [Theory]
        [InlineData(1, 5, false)] [InlineData(5000, 3600, false)]
        [InlineData(1, 5, true)] [InlineData(5000, 3600, true)]
        public async Task IntervalAndBooleanPauseAreClampedToDocumentedBounds(double supplied, double expected, bool paused)
        {
            var h = new Harness();
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":" +
                supplied.ToString(CultureInfo.InvariantCulture) + ",\"pause\":" + (paused ? "true" : "false") + ",\"error\":null}"));
            await h.Send(); h.Runtime.Track("second"); h.Clock.Advance(expected - 0.001);
            await h.Client.ProcessAsync(paused); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(paused); Assert.Equal(2, h.Transport.Requests.Count);
        }

        [Fact]
        public void ParsesDocumentedResponseAndOptionalErrorCode()
        {
            var normal = ServerAdvice.Parse("{\"recommendedSendIntervalSeconds\":20,\"pause\":false,\"error\":null}");
            Assert.Equal(20, normal.RecommendedSendIntervalSeconds); Assert.Equal(false, normal.Pause); Assert.Null(normal.Error);
            var busy = ServerAdvice.Parse("{\"recommendedSendIntervalSeconds\":30,\"pause\":false,\"error\":\"busy\"}");
            Assert.Equal(30, busy.RecommendedSendIntervalSeconds); Assert.Equal(false, busy.Pause); Assert.Equal("busy", busy.Error);
            var future = ServerAdvice.Parse("{\"future\":{\"nested\":[1,true,null,1e999]},\"recommendedSendIntervalSeconds\":60,\"pause\":true,\"error\":{}}");
            Assert.Equal(60, future.RecommendedSendIntervalSeconds); Assert.Equal(true, future.Pause); Assert.Null(future.Error);
        }

        [Fact]
        public void ParserHandlesEscapedKeysAndBoundsItsInput()
        {
            Assert.Equal(20, ServerAdvice.Parse("{\"recommendedSendInterval\\u0053econds\":20}").RecommendedSendIntervalSeconds);
            Assert.Equal(true, ServerAdvice.Parse("{\"pa\\u0075se\":true}").Pause);
            Assert.Null(ServerAdvice.Parse(new string('x', 65537)).RecommendedSendIntervalSeconds);
            Assert.Null(ServerAdvice.Parse(new string('[', 33) + new string(']', 33)).RecommendedSendIntervalSeconds);
        }

        [Theory]
        [InlineData("null")] [InlineData("0")] [InlineData("1")]
        [InlineData("\"true\"")] [InlineData("\"false\"")] [InlineData("{}")] [InlineData("[]")]
        public async Task PauseRequiresABooleanAndDoesNotCoerceOtherTypes(string supplied)
        {
            var h = new Harness(); h.Transport.Responses.Enqueue(new TransportResponse(202,
                "{\"recommendedSendIntervalSeconds\":60,\"pause\":" + supplied + ",\"error\":null}"));
            await h.Send(); h.Runtime.Track("explicit"); var flush = h.Runtime.Flush();
            await h.Client.ProcessAsync(); await flush.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task UnpausedResponseKeepsCountAndExplicitFlushTriggersEnabled()
        {
            var h = new Harness(new PulseOptions { MaxBatchSize = 2 });
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":3600,\"pause\":false,\"error\":null}"));
            await h.Send(); h.Runtime.Track("count.one"); h.Runtime.Track("count.two"); await h.Client.ProcessAsync();
            Assert.Equal(2, h.Transport.Requests.Count);
            h.Runtime.Track("explicit"); var flush = h.Runtime.Flush(); await h.Client.ProcessAsync();
            await flush.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(3, h.Transport.Requests.Count);
        }

        [Theory]
        [InlineData("{\"pause\":true}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":null,\"pause\":true}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":0,\"pause\":true}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":-1,\"pause\":true}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":1e999,\"pause\":true}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":-1e999,\"pause\":true}")]
        [InlineData("{\"recommendedSendIntervalSeconds\":\"60\",\"pause\":true}")]
        public async Task PauseWithoutValidRecommendationUsesCurrentInterval(string body)
        {
            var h = new Harness(new PulseOptions { FlushIntervalSeconds = 70 });
            h.Transport.Responses.Enqueue(new TransportResponse(202, body)); await h.Send(); h.Runtime.Track("queued");
            h.Clock.Advance(69.999); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(2, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task PausedCollectionKeepsQueueCapacityAndNewestSignals()
        {
            var h = new Harness(new PulseOptions { MaxQueueSize = 3, MaxBatchSize = 2 });
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":30,\"pause\":true,\"error\":null}"));
            await h.Send("first");
            for (var i = 0; i < 5; i++) { h.Runtime.Track("queued", i); await h.Client.ProcessAsync(true); }
            Assert.Single(h.Transport.Requests); Assert.Equal(3, h.Client.Count);
            Assert.Equal(3, QueueJournal.Decode(h.Storage.Read(h.Client.QueueName), 1000).Count);
            h.Clock.Advance(30); await h.Client.ProcessAsync(true);
            Assert.Equal(new[] { 2d, 3d, 4d }, Harness.Events(h.Transport.Requests).Skip(1).Select(e => e.GetProperty("value").GetDouble()));
            Assert.Equal(0, h.Client.Count);
        }

        [Fact]
        public async Task PausedCollectionStillExpiresOldSignals()
        {
            var h = new Harness(new PulseOptions { MaxQueueAgeDays = 0.0001 });
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":20,\"pause\":true,\"error\":null}"));
            await h.Send("first"); h.Runtime.Track("expires"); await h.Client.ProcessAsync(true);
            h.Clock.Advance(19); h.Runtime.Track("fresh"); await h.Client.ProcessAsync(true);
            Assert.Equal(1, h.Client.Count); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(1); await h.Client.ProcessAsync(true);
            Assert.Equal(new[] { "first", "fresh" }, Harness.Events(h.Transport.Requests).Select(e => e.GetProperty("type").GetString()));
        }

        [Fact]
        public async Task RepeatedPauseReschedulesAndUnpausedResponseRestoresNormalInterval()
        {
            var h = new Harness();
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":10,\"pause\":true,\"error\":null}"));
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":30,\"pause\":true,\"error\":null}"));
            h.Transport.Responses.Enqueue(new TransportResponse(202, "{\"recommendedSendIntervalSeconds\":5,\"pause\":false,\"error\":null}"));
            await h.Send(); h.Runtime.Track("second"); h.Clock.Advance(10); await h.Client.ProcessAsync(true);
            h.Runtime.Track("third"); h.Clock.Advance(29.999); await h.Client.ProcessAsync(true); Assert.Equal(2, h.Transport.Requests.Count);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(3, h.Transport.Requests.Count);
            h.Runtime.Track("fourth"); h.Clock.Advance(4.999); await h.Client.ProcessAsync(); Assert.Equal(3, h.Transport.Requests.Count);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(); Assert.Equal(4, h.Transport.Requests.Count);
        }

        [Fact]
        public async Task ErrorCodeDoesNotOverrideSuccessfulHttpStatus()
        {
            var h = new Harness(); h.Transport.Responses.Enqueue(new TransportResponse(202,
                "{\"recommendedSendIntervalSeconds\":60,\"pause\":false,\"error\":\"synthetic_code\"}"));
            await h.Send(); Assert.Equal(0, h.Client.Count); h.Runtime.Track("next");
            h.Clock.Advance(59.999); await h.Client.ProcessAsync(); Assert.Single(h.Transport.Requests);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(); Assert.Equal(2, h.Transport.Requests.Count);
        }

        [Theory]
        [InlineData(429, 0, 30, 1, 30)] [InlineData(503, 0, 30, 120, 120)]
        [InlineData(429, 3, 5, 1, 8)] [InlineData(503, 3, 5, 1, 8)]
        public async Task BusyAdviceRetryAfterAndBackoffUseLongestDelay(int status, int priorFailures,
            int recommended, int retryAfter, int expected)
        {
            var h = new Harness(); for (var i = 0; i < priorFailures; i++) h.Transport.Reply(500);
            h.Transport.Responses.Enqueue(new TransportResponse(status, "{\"recommendedSendIntervalSeconds\":" +
                recommended.ToString(CultureInfo.InvariantCulture) + ",\"pause\":false,\"error\":\"busy\"}", retryAfter.ToString(CultureInfo.InvariantCulture)));
            await h.Send("retry");
            for (var i = 0; i < priorFailures; i++) { h.Clock.Advance(Math.Pow(2, i)); await h.Client.ProcessAsync(true); }
            var count = h.Transport.Requests.Count; Assert.Equal(priorFailures + 1, count); Assert.Equal(1, h.Client.Count);
            h.Clock.Advance(expected - 0.001); await h.Client.ProcessAsync(true); Assert.Equal(count, h.Transport.Requests.Count);
            h.Clock.Advance(0.001); await h.Client.ProcessAsync(true); Assert.Equal(count + 1, h.Transport.Requests.Count);
            Assert.Equal(h.Transport.Requests.First().Body, h.Transport.Requests.Last().Body); Assert.Equal(0, h.Client.Count);
        }

        [Theory]
        [InlineData(429)] [InlineData(503)]
        public async Task BusyRetryDeadlineSurvivesRestart(int status)
        {
            var h = new Harness(); h.Transport.Responses.Enqueue(new TransportResponse(status,
                "{\"recommendedSendIntervalSeconds\":30,\"pause\":false,\"error\":null}", "60")); await h.Send();
            var restarted = new Harness(storage: h.Storage); restarted.Clock.Advance(59.999);
            await restarted.Client.ProcessAsync(true); Assert.Empty(restarted.Transport.Requests);
            restarted.Clock.Advance(0.001); await restarted.Client.ProcessAsync(true);
            Assert.Equal(h.Transport.Requests.First().Body, Assert.Single(restarted.Transport.Requests).Body);
        }
    }
}
