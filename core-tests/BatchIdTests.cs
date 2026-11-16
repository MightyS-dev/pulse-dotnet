using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class BatchIdTests
    {
        private static readonly Regex Format = new Regex("^[0-9a-f]{32}$");
        private static string[] Ids(Harness h) => h.Transport.Requests.Select(r => r.BatchId).ToArray();

        [Fact]
        public async Task BatchIdIs32LowercaseHex()
        {
            var h = new Harness(); await h.Send();
            Assert.Matches(Format, Assert.Single(h.Transport.Requests).BatchId);
        }

        [Fact]
        public async Task TwoConsecutiveBatchesHaveDifferentIds()
        {
            var h = new Harness(); await h.Send("a"); await h.Send("b");
            var ids = Ids(h); Assert.Equal(2, ids.Length); Assert.NotEqual(ids[0], ids[1]);
        }

        [Theory]
        [InlineData(0)] [InlineData(500)] [InlineData(429)] [InlineData(503)]
        public async Task RetryAfterFailureKeepsIdAndContent(int status)
        {
            var h = new Harness(); h.Transport.Reply(status, 202);
            await h.Send(props: new Dictionary<string, object> { ["large"] = new string('z', 2000) });
            h.Clock.Advance(1000); await h.Client.ProcessAsync(true);
            var requests = h.Transport.Requests.ToArray(); Assert.Equal(2, requests.Length);
            Assert.Equal(requests[0].BatchId, requests[1].BatchId);
            Assert.Equal(Harness.JsonBytes(requests[0]), Harness.JsonBytes(requests[1]));
        }

        [Fact]
        public async Task SignalAddedBeforeRetryStaysOutOfTheRetriedBatchAndGoesInNextBatchWithNewId()
        {
            var h = new Harness(); h.Transport.Reply(500, 202);
            await h.Send("first"); h.Runtime.Track("later");
            h.Clock.Advance(1000); await h.Client.ProcessAsync(true);
            var requests = h.Transport.Requests.ToArray(); Assert.Equal(3, requests.Length);
            Assert.Equal(requests[0].BatchId, requests[1].BatchId);
            Assert.Equal(Harness.JsonBytes(requests[0]), Harness.JsonBytes(requests[1]));
            Assert.NotEqual(requests[1].BatchId, requests[2].BatchId);
            Assert.Equal("later", Harness.Event(requests[2]).GetProperty("type").GetString());
        }

        [Fact]
        public async Task RetryAfterProcessRestartUsesSavedIdAndSameSignals()
        {
            var h = new Harness(); h.Transport.Reply(500);
            await h.Send("one"); h.Runtime.Track("two");
            var restarted = new Harness(storage: h.Storage); restarted.Clock.Advance(1000);
            await restarted.Client.ProcessAsync(true);
            var first = h.Transport.Requests.First(); var again = restarted.Transport.Requests.First();
            Assert.Equal(first.BatchId, again.BatchId);
            Assert.Equal(Harness.JsonBytes(first), Harness.JsonBytes(again));
        }

        // Rewrites the saved in-flight batch into the exact 0.9.4 layout (magic MSB1, no id) with a valid checksum.
        private static void DowngradeBatchTo094(Harness h)
        {
            var bytes = h.Storage.Read(h.Client.BatchName); var body = bytes.Take(bytes.Length - 32).ToArray();
            int offset;
            using (var input = new MemoryStream(body))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true)) { reader.ReadString(); reader.ReadString(); offset = (int)input.Position; }
            var rest = body.Skip(offset).ToArray();
            byte[] payload;
            using (var output = new MemoryStream())
            {
                using (var writer = new BinaryWriter(output, Encoding.UTF8, true)) { writer.Write("MSB1"); writer.Write(rest); }
                payload = output.ToArray();
            }
            using (var sha = SHA256.Create()) h.Storage.Files[h.Client.BatchName] = payload.Concat(sha.ComputeHash(payload)).ToArray();
        }

        [Fact]
        public async Task QueueFrom094WithPendingBatchAndLooseSignalsLoadsAndSendsEverythingWithFreshIds()
        {
            var h = new Harness(); h.Transport.Reply(500);
            await h.Send("one"); h.Runtime.Track("two");
            await h.Client.ProcessAsync(); // saves the journal with the loose signal
            DowngradeBatchTo094(h);
            var restarted = new Harness(storage: h.Storage); restarted.Clock.Advance(1000);
            await restarted.Client.ProcessAsync(true);
            var requests = restarted.Transport.Requests.ToArray();
            Assert.Equal(new[] { "one", "two" }, Harness.Events(requests).Select(e => e.GetProperty("type").GetString()).Where(t => t == "one" || t == "two"));
            Assert.All(requests, r => Assert.Matches(Format, r.BatchId));
            Assert.Equal(requests.Length, requests.Select(r => r.BatchId).Distinct().Count());
            Assert.Equal(0, restarted.Client.Count);
        }

        [Fact]
        public async Task SplitOn413GivesEachHalfItsOwnNewId()
        {
            var h = new Harness(new PulseOptions { MaxBatchSize = 4, Gzip = false }); h.Transport.Reply(413, 202, 202);
            for (var i = 0; i < 4; i++) h.Runtime.Track("event", i);
            await h.Client.ProcessAsync();
            var ids = Ids(h); Assert.Equal(3, ids.Length); Assert.Equal(3, ids.Distinct().Count());
        }

        [Fact]
        public async Task Gzip413RetryUncompressedKeepsIdAndJson()
        {
            var h = new Harness(); h.Transport.Reply(413, 202);
            await h.Send(props: new Dictionary<string, object> { ["large"] = new string('z', 2000) });
            var requests = h.Transport.Requests.ToArray(); Assert.Equal(2, requests.Length);
            Assert.True(requests[0].IsGzip); Assert.False(requests[1].IsGzip);
            Assert.Equal(requests[0].BatchId, requests[1].BatchId);
            Assert.Equal(Harness.JsonBytes(requests[0]), Harness.JsonBytes(requests[1]));
        }

        [Fact]
        public async Task SuccessfulBatchIsDroppedWithItsId()
        {
            var h = new Harness(); await h.Send();
            Assert.Null(h.Storage.Read(h.Client.BatchName));
            Assert.Equal(0, h.Client.Count);
        }
    }
}
