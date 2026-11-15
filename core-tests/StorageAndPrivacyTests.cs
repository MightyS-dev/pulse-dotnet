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
    public sealed class StorageAndPrivacyTests
    {
        [Fact]
        public async Task RestartBetweenWriteAndSendRestoresOriginalSignalsAndSessions()
        {
            var h = new Harness(); h.Runtime.Track("offline"); await h.Client.ProcessAsync();
            Assert.Empty(h.Transport.Requests); Assert.Single(QueueJournal.Decode(h.Storage.Read(h.Client.QueueName), 1000));
            var restarted = new Harness(storage: h.Storage); await restarted.Client.ProcessAsync();
            var restored = Harness.Event(Assert.Single(restarted.Transport.Requests)); Assert.Equal("offline", restored.GetProperty("type").GetString());
            restarted.Runtime.Track("new.session"); await restarted.Client.ProcessAsync(true);
            Assert.NotEqual(restored.GetProperty("sessionId").GetString(), Harness.Event(restarted.Transport.Requests.Last()).GetProperty("sessionId").GetString());
        }

        // A torn append (app killed mid-write) loses only the partial last record; earlier records survive and the
        // journal is compacted. Formerly the whole queue was discarded.
        [Theory]
        [InlineData(1)] [InlineData(7)] [InlineData(32)] [InlineData(-1)]
        public async Task TornJournalKeepsEarlierRecordsAndDropsOnlyTheTornTail(int remove)
        {
            var h = new Harness(); foreach (var type in new[] { "one", "two", "three" }) { h.Runtime.Track(type); await h.Client.ProcessAsync(); }
            Assert.True(h.Storage.Appends >= 2);
            var bytes = h.Storage.Read(h.Client.QueueName);
            // -1 keeps only the first 3 bytes of the last record's length prefix.
            var lastRecordStart = RecordOffsets(bytes)[2];
            bytes = remove > 0 ? bytes.Take(bytes.Length - remove).ToArray() : bytes.Take(lastRecordStart + 3).ToArray();
            h.Storage.Replace(h.Client.QueueName, bytes);
            var restarted = new Harness(storage: h.Storage); restarted.Transport.Reply(500); await restarted.Client.ProcessAsync();
            Assert.Equal(2, restarted.Client.Count);
            var compacted = QueueJournal.Decode(restarted.Storage.Read(restarted.Client.QueueName), 1000, out var intact);
            Assert.True(intact); Assert.Equal(new[] { "one", "two" }, compacted.Select(e => Type(e.Json)));
            restarted.Clock.Advance(1); await restarted.Client.ProcessAsync(true); Assert.Equal(0, restarted.Client.Count);
            Assert.Equal(new[] { "one", "two" }, Harness.Events(new[] { restarted.Transport.Requests.Last() }).Select(e => e.GetProperty("type").GetString()));
            await restarted.Send("healthy"); Assert.Equal(3, restarted.Transport.Requests.Count);
        }

        // Exact rule for corruption that is not at the tail: the affected record and every later record are dropped,
        // earlier records survive. A damaged header discards the whole journal. Nothing throws.
        [Theory]
        [InlineData(0, 0)] [InlineData(1, 1)] [InlineData(2, 2)] [InlineData(-1, 0)]
        public async Task CorruptByteDropsAffectedRecordAndLaterOnes(int record, int survivors)
        {
            var h = new Harness(); foreach (var type in new[] { "one", "two", "three" }) { h.Runtime.Track(type); await h.Client.ProcessAsync(); }
            var bytes = h.Storage.Read(h.Client.QueueName);
            if (record < 0) bytes[0] ^= 0x1; else bytes[RecordOffsets(bytes)[record] + 20] ^= 0x1;
            h.Storage.Replace(h.Client.QueueName, bytes);
            Assert.Equal(survivors, QueueJournal.Decode(bytes, 1000, out var intact).Count); Assert.False(intact);
            var restarted = new Harness(storage: h.Storage); await restarted.Client.ProcessAsync(true);
            Assert.Equal(new[] { "one", "two", "three" }.Take(survivors),
                Harness.Events(restarted.Transport.Requests).Select(e => e.GetProperty("type").GetString()));
            await restarted.Send("healthy"); Assert.Equal("healthy", Harness.Event(restarted.Transport.Requests.Last()).GetProperty("type").GetString());
        }

        private static string Type(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("type").GetString();

        private static int[] RecordOffsets(byte[] journal)
        {
            var offsets = new List<int>(); var position = 5;
            while (position < journal.Length) { offsets.Add(position); position += 4 + BitConverter.ToInt32(journal, position) + 32; }
            return offsets.ToArray();
        }

        [Fact]
        public async Task FullQueueDropsOldestAndCompactsOnDisk()
        {
            var h = new Harness(new PulseOptions { MaxQueueSize = 3 });
            for (var i = 0; i < 5; i++) { h.Runtime.Track("event", i); await h.Client.ProcessAsync(); }
            Assert.Equal(3, h.Client.Count);
            var restarted = new Harness(storage: h.Storage); await restarted.Client.ProcessAsync(true);
            Assert.Equal(new[] { 2d, 3d, 4d }, Harness.Events(restarted.Transport.Requests).Select(e => e.GetProperty("value").GetDouble()));
            Assert.True(h.Storage.Appends > 0); Assert.True(h.Storage.Replaces > 1);
        }

        [Fact]
        public async Task QueueAgeBoundaryIsInclusiveAndStaleSignalsExpire()
        {
            var h = new Harness(); h.Runtime.Track("at.boundary"); await h.Client.ProcessAsync();
            h.Clock.Advance(7 * 86400); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            var stale = new Harness(); stale.Runtime.Track("stale"); await stale.Client.ProcessAsync();
            stale.Clock.Advance(7 * 86400 + 0.001); await stale.Client.ProcessAsync(true); Assert.Empty(stale.Transport.Requests);
            var restarted = new Harness(storage: stale.Storage); await restarted.Client.ProcessAsync(true); Assert.Empty(restarted.Transport.Requests);
        }

        [Fact]
        public async Task RestartPrunesStaleRecordsAndCurrentOptionsLimitRestoredQueue()
        {
            var h = new Harness(); for (var i = 0; i < 5; i++) h.Runtime.Track("old", i); await h.Client.ProcessAsync();
            var smaller = new Harness(new PulseOptions { MaxQueueSize = 2 }, h.Storage);
            await smaller.Client.ProcessAsync(true); Assert.Equal(new[] { 3d, 4d }, Harness.Events(smaller.Transport.Requests).Select(e => e.GetProperty("value").GetDouble()));
            h.Runtime.Track("expired"); await h.Client.ProcessAsync();
            var older = new Harness(storage: h.Storage); older.Clock.Advance(8 * 86400); await older.Client.ProcessAsync(true); Assert.Empty(older.Transport.Requests);
        }

        [Fact]
        public async Task UserIdIsHashedBeforeQueueingAndNeverStoredRaw()
        {
            var h = new Harness(); const string raw = "synthetic-private-identity";
            h.Runtime.SetUser(raw); h.Runtime.Track("identified"); await h.Client.ProcessAsync();
            Assert.DoesNotContain(raw, Encoding.UTF8.GetString(h.Storage.Read(h.Client.QueueName)));
            await h.Client.ProcessAsync(true); var request = Assert.Single(h.Transport.Requests);
            Assert.DoesNotContain(raw, Encoding.UTF8.GetString(Harness.JsonBytes(request)));
            var hash = Harness.Event(request).GetProperty("userHash").GetString();
            Assert.Equal(SignalJson.Hash(raw), hash); Assert.Matches("^[0-9a-f]{64}$", hash);
            h.Runtime.SetUser(null); await h.Send("anonymous"); Assert.False(Harness.Event(h.Transport.Requests.Last()).TryGetProperty("userHash", out _));
            var restarted = new Harness(storage: h.Storage); await restarted.Send("still.anonymous");
            Assert.False(Harness.Event(Assert.Single(restarted.Transport.Requests)).TryGetProperty("userHash", out _));
        }

        [Fact]
        public async Task OptOutClearsQueuePersistsAndRejectsNewEventsAcrossRestart()
        {
            var h = new Harness(); h.Runtime.Track("old"); await h.Client.ProcessAsync();
            h.Runtime.OptOut = true; h.Runtime.Track("dropped"); var flush = h.Runtime.Flush(); await h.Client.ProcessAsync(true); await flush;
            Assert.Equal(0, h.Client.Count); Assert.Empty(QueueJournal.Decode(h.Storage.Read(h.Client.QueueName), 1000));
            var restarted = new Harness(storage: h.Storage); restarted.Runtime.Track("before.load"); await restarted.Client.ProcessAsync(true);
            Assert.True(restarted.Runtime.OptOut); Assert.Empty(restarted.Transport.Requests);
            restarted.Runtime.Track("after.load"); Assert.Equal(0, restarted.Client.Count);
            restarted.Runtime.OptOut = false; await restarted.Send("allowed"); Assert.Single(restarted.Transport.Requests);
        }

        [Fact]
        public async Task ExplicitPreInitializationOptOutOverridesStoredPreference()
        {
            var h = new Harness(initialize: false); h.Runtime.Track("before"); h.Runtime.OptOut = true;
            h.Runtime.Track("after"); h.Runtime.Initialize("synthetic-key"); await h.Client.ProcessAsync(true);
            Assert.Empty(h.Transport.Requests); Assert.True(h.Runtime.OptOut);
            var restarted = new Harness(storage: h.Storage); await restarted.Client.ProcessAsync(true); Assert.True(restarted.Runtime.OptOut);
        }

        [Fact]
        public async Task OptOutCancelsInflightTransportAndDoesNotRetry()
        {
            var h = new Harness(); var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Transport.Handler = async (_, ct) => { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { canceled.SetResult(true); } return new TransportResponse(0); };
            h.Runtime.Track("inflight"); var processing = h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests);
            h.Runtime.OptOut = true; await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await processing;
            h.Clock.Advance(1000); await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests); Assert.Equal(0, h.Client.Count);
        }

        [Fact]
        public async Task CorruptPrivacyMarkerFailsClosed()
        {
            var storage = new MemoryStorage(); storage.Replace("opt-out", Encoding.ASCII.GetBytes("corrupt"));
            var h = new Harness(storage: storage); await h.Send(); Assert.True(h.Runtime.OptOut); Assert.Empty(h.Transport.Requests);
        }

        [Fact]
        public async Task StorageFailurePreventsSendingAndRecoversWithoutLosingQueue()
        {
            var h = new Harness(); h.Storage.Fail = true; await h.Send(); Assert.Empty(h.Transport.Requests); Assert.Equal(1, h.Client.Count);
            h.Storage.Fail = false; await h.Client.ProcessAsync(true); Assert.Single(h.Transport.Requests); Assert.Equal(0, h.Client.Count);
        }

        [Fact]
        public async Task DifferentApplicationKeysNeverReplayEachOthersQueues()
        {
            var h = new Harness(); h.Runtime.Track("private.to.app"); await h.Client.ProcessAsync();
            var other = new Harness(storage: h.Storage, key: "other-synthetic-key"); await other.Client.ProcessAsync(true); Assert.Empty(other.Transport.Requests);
            var restored = new Harness(storage: h.Storage); await restored.Client.ProcessAsync(true); Assert.Single(restored.Transport.Requests);
        }

        [Fact]
        public void FileStorageAppendsAndAtomicallyReplacesIgnoringAbandonedTemporaryFile()
        {
            var directory = Path.Combine(Path.GetDirectoryName(typeof(StorageAndPrivacyTests).Assembly.Location), ".tmp", Guid.NewGuid().ToString("N"));
            try
            {
                var storage = new FileStorage(directory); Assert.Null(storage.Read("queue.bin"));
                storage.Replace("queue.bin", new byte[] { 1, 2 }); storage.Append("queue.bin", new byte[] { 3 });
                Assert.Equal(new byte[] { 1, 2, 3 }, storage.Read("queue.bin"));
                File.WriteAllBytes(Path.Combine(directory, "queue.bin.tmp"), new byte[] { 99 });
                Assert.Equal(new byte[] { 1, 2, 3 }, new FileStorage(directory).Read("queue.bin"));
                storage.Replace("queue.bin", new byte[] { 4 }); Assert.Equal(new byte[] { 4 }, storage.Read("queue.bin"));
                storage.Delete("queue.bin"); Assert.Null(storage.Read("queue.bin"));
                Assert.Throws<ArgumentException>(() => storage.Read("../outside.bin"));
            }
            finally
            {
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".tmp"));
                Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(directory));
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
