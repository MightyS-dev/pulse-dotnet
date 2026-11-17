using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse.Tests
{
    // A clock the test moves by hand. Delay completes when the clock has been advanced far enough, or when it is cancelled.
    internal sealed class ManualClock : IClock
    {
        private readonly object gate = new object();
        private readonly List<(DateTimeOffset At, TaskCompletionSource<bool> Done)> waiters = new List<(DateTimeOffset, TaskCompletionSource<bool>)>();
        private DateTimeOffset now = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow { get { lock (gate) return now; } }
        internal int Waiters { get { lock (gate) return waiters.Count; } }
        internal void Set(DateTimeOffset value) { lock (gate) now = value; Release(); }
        internal void Advance(TimeSpan by) { lock (gate) now += by; Release(); }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            (DateTimeOffset At, TaskCompletionSource<bool> Done) entry;
            lock (gate) { entry = (now + delay, done); waiters.Add(entry); }
            cancellationToken.Register(() =>
            {
                lock (gate) waiters.Remove(entry);
                done.TrySetCanceled();
            });
            return done.Task;
        }

        private void Release()
        {
            List<(DateTimeOffset At, TaskCompletionSource<bool> Done)> due;
            lock (gate) { due = waiters.Where(w => w.At <= now).ToList(); foreach (var w in due) waiters.Remove(w); }
            foreach (var w in due) w.Done.TrySetResult(true);
        }
    }

    // A transport that accepts App Control requests. Everything else (batches, settings) gets a plain answer.
    internal sealed class ControlTransport : ITransport, IControlTransport
    {
        internal readonly ConcurrentQueue<TransportRequest> Requests = new ConcurrentQueue<TransportRequest>();
        internal Func<TransportRequest, CancellationToken, Task<TransportResponse>> Handler;
        internal int Count => Requests.Count;

        public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            if (!request.IsControl) return Task.FromResult(new TransportResponse(request.IsGet ? 404 : 202));
            Requests.Enqueue(request);
            return Handler != null ? Handler(request, cancellationToken) : Task.FromResult(new TransportResponse(404));
        }

        internal void Answer(int status, string body = "") => Handler = (r, c) => Task.FromResult(new TransportResponse(status, body));
    }

    internal static class ControlBodies
    {
        internal static string Rules(string update = "{}", string blocked = "[]", string maintenance = "null", string announcement = "null",
            string whatsNew = "null", string disabled = "[]", int version = 5, bool off = false) =>
            "{\"format\":1,\"version\":" + version + ",\"off\":" + (off ? "true" : "false") + ",\"update\":" + update + ",\"blocked\":" + blocked +
            ",\"maintenance\":" + maintenance + ",\"announcement\":" + announcement + ",\"whatsNew\":" + whatsNew +
            ",\"texts\":{\"custom\":{}},\"disabledSdkVersions\":" + disabled + "}";

        internal const string RequiredIos = "{\"ios\":{\"min\":\"2.0.0\",\"latest\":\"2.0.0\",\"mode\":\"required\",\"storeVersion\":\"2.0.0\",\"storeUrl\":\"https://apps.apple.com/app/id1\"}}";
        internal const string SuggestedIos = "{\"ios\":{\"min\":null,\"latest\":\"2.0.0\",\"mode\":\"suggested\",\"storeVersion\":\"2.0.0\",\"storeUrl\":\"https://apps.apple.com/app/id1\"}}";

        internal static string Maintenance(DateTimeOffset starts, DateTimeOffset ends, int headsUp = 60) =>
            "{\"startsAt\":\"" + starts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\",\"endsAt\":\"" + ends.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") +
            "\",\"headsUpMinutes\":" + headsUp + ",\"exemptAboveStore\":false,\"platforms\":null}";
    }

    // One App Control client wired to fakes. The platform is ios, the app version 1.0.0, the language English.
    internal sealed class ControlRig
    {
        internal const string Key = "synthetic-key";
        internal readonly ManualClock Clock = new ManualClock();
        internal readonly ControlTransport Transport = new ControlTransport();
        internal readonly MemoryStorage Storage;
        internal readonly List<ControlState> Changes = new List<ControlState>();
        internal readonly List<string> Logs = new List<string>();
        internal readonly ControlClient Client;

        internal ControlRig(string appVersion = "1.0.0", string platform = "ios", bool debug = false, ControlMode mode = ControlMode.Automatic,
            MemoryStorage storage = null, ITransport transport = null, string bundleId = "dev.example.synthetic")
        {
            Storage = storage ?? new MemoryStorage();
            var template = new ControlContext
            {
                Platform = platform, AppName = "Demo", AppVersion = appVersion, BundleId = bundleId, Locales = new[] { "en-US" },
                ToLocal = t => t.ToOffset(TimeSpan.Zero)
            };
            Client = new ControlClient(Key, "https://in.mightys.dev", transport ?? Transport, Storage, Clock, template,
                PulseInfo.SdkName + "@" + PulseInfo.Version, () => debug, mode, Logs.Add);
            Client.Changed += s => { lock (Changes) Changes.Add(s); };
        }

        internal static string CacheName => ControlIds.CacheName(Key);

        // Puts a last good answer on the device, as if an earlier launch had saved it.
        internal void SeedCache(string body, TimeSpan age, string seen = "1.0.0")
        {
            Storage.Files[CacheName] = ControlCache.Serialize(new ControlCacheData
            {
                Body = body, ETag = ControlRules.Parse(body)?.ETag, OkAt = Clock.UtcNow - age, Seen = seen
            });
        }

        internal int ChangeCount { get { lock (Changes) return Changes.Count; } }

        internal async Task Run(string body = null, int? status = null)
        {
            if (body != null || status.HasValue) Transport.Answer(status ?? 200, body ?? "");
            Client.Start();
            await Client.Startup;
        }

        internal static async Task Until(Func<bool> condition, int milliseconds = 10000)
        {
            using var timeout = new CancellationTokenSource(milliseconds);
            while (!condition()) await Task.Delay(5, timeout.Token);
        }

        // Moves the clock and waits for the scheduler to show the expected kind (the clock alone, no network).
        internal async Task AdvanceTo(TimeSpan by, ControlKind expected)
        {
            Clock.Advance(by);
            await Until(() => Client.State.Kind == expected);
        }
    }
}
