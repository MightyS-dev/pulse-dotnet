using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    internal sealed class FakeClock : IClock
    {
        private long ticks = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero).Ticks;
        public DateTimeOffset UtcNow => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(double seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    internal sealed class FakeTransport : ITransport
    {
        internal readonly ConcurrentQueue<TransportRequest> Requests = new ConcurrentQueue<TransportRequest>();
        internal readonly ConcurrentQueue<TransportResponse> Responses = new ConcurrentQueue<TransportResponse>();
        internal Func<TransportRequest, CancellationToken, Task<TransportResponse>> Handler;
        public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Handler != null ? Handler(request, cancellationToken) :
                Task.FromResult(Responses.TryDequeue(out var result) ? result : new TransportResponse(202));
        }
        internal void Reply(params int[] statuses) { foreach (var status in statuses) Responses.Enqueue(new TransportResponse(status)); }
    }

    internal sealed class MemoryStorage : IStorage
    {
        internal readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        internal int Appends, Replaces;
        internal bool Fail;
        private void Check() { if (Fail) throw new IOException("Synthetic failure."); }
        public byte[] Read(string name) { Check(); return Files.TryGetValue(name, out var bytes) ? bytes.ToArray() : null; }
        public void Append(string name, byte[] bytes)
        { Check(); Appends++; Files[name] = Files.TryGetValue(name, out var old) ? old.Concat(bytes).ToArray() : bytes.ToArray(); }
        public void Replace(string name, byte[] bytes) { Check(); Replaces++; Files[name] = bytes.ToArray(); }
        public void Delete(string name) { Check(); Files.Remove(name); }
    }

    internal sealed class FakeEnvironment : IEnvironmentInfo
    {
        public string Platform => "desktop";
        public string OsName => "Windows";
        public string OsVersion => "11";
        public string AppVersion => "1.2.3";
        public string AppBuildNumber => "42";
        public string DeviceModel => "Desktop";
        public string Locale => "sr-RS";
        public string BundleId => "dev.example.synthetic";
        public string SigningCertSha256 => new string('a', 64);
    }

    internal sealed class Harness
    {
        internal readonly FakeClock Clock = new FakeClock();
        internal readonly FakeTransport Transport = new FakeTransport();
        internal readonly MemoryStorage Storage;
        internal readonly PulseRuntime Runtime;
        internal readonly List<string> Logs = new List<string>();
        internal PulseClient Client;
        internal Harness(PulseOptions options = null, MemoryStorage storage = null, bool initialize = true,
            string key = "synthetic-key", Func<double> random = null)
        {
            Storage = storage ?? new MemoryStorage();
            Runtime = new PulseRuntime(Clock);
            Runtime.InstallPlatform((k, o, optOut) => Client = new PulseClient(k, o, Clock, Transport,
                Storage, new FakeEnvironment(), Logs.Add, false, random ?? (() => 0.5), optOut), Logs.Add,
                () => PulseClient.ReadStoredOptOut(Storage));
            if (initialize) Runtime.Initialize(key, options);
        }
        internal async Task Send(string type = "test", double? value = null, IDictionary<string, object> props = null)
        { Runtime.Track(type, value, props); await Client.ProcessAsync(true); }
        internal static byte[] JsonBytes(TransportRequest request)
        {
            if (!request.IsGzip) return request.Body;
            using var input = new MemoryStream(request.Body);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); gzip.CopyTo(output); return output.ToArray();
        }
        internal static JsonElement[] Events(IEnumerable<TransportRequest> requests) => requests.SelectMany(r =>
        {
            using var json = JsonDocument.Parse(JsonBytes(r));
            return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }).ToArray();
        internal static JsonElement Event(TransportRequest request) => Events(new[] { request }).Single();
        internal static async Task Until(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) await Task.Delay(5, timeout.Token);
        }
    }
}
