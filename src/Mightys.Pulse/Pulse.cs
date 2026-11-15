using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    /// <summary>Host platform options passed to <see cref="Pulse.Initialize"/>.</summary>
    public sealed class PulsePlatformOptions
    {
        /// <summary>Directory for the offline queue. Defaults to a per-app folder under the local application data folder.</summary>
        public string StorageDirectory { get; set; }
        /// <summary>Environment snapshot. Defaults to <see cref="DotNetEnvironmentInfo"/>.</summary>
        public IEnvironmentInfo EnvironmentInfo { get; set; }
        /// <summary>Receives diagnostic strings when <see cref="PulseOptions.Debug"/> is true. Do not block in it.</summary>
        public Action<string> Logger { get; set; }
    }

    /// <summary>Static entry point of MightyS Pulse. Public calls never throw into the host app and never block the calling thread.</summary>
    public static class Pulse
    {
        internal static readonly TimeSpan FinalSaveTimeout = TimeSpan.FromSeconds(2);
        private static readonly PulseHost host = new PulseHost();
        /// <summary>Starts the SDK. Call once per process; later successful calls are ignored.
        /// <param name="appKey">Your app key from the Pulse dashboard.</param>
        /// <param name="options">Optional delivery options.</param>
        /// <param name="platform">Optional storage, environment and logger overrides.</param></summary>
        public static void Initialize(string appKey, PulseOptions options = null, PulsePlatformOptions platform = null) => host.Initialize(appKey, options, platform);
        /// <summary>Queues a named signal.
        /// <param name="type">Signal name, at most 200 UTF-16 units.</param>
        /// <param name="value">Optional finite number.</param>
        /// <param name="props">Optional properties: strings, finite numbers and booleans.</param></summary>
        public static void Track(string type, double? value = null, IDictionary<string, object> props = null) => host.Runtime.Track(type, value, props);
        /// <summary>Sets an optional user id. Only its SHA-256 hash is kept and sent; pass null to clear it.
        /// <param name="stableUserId">Stable user id, or null.</param></summary>
        public static void SetUser(string stableUserId) => host.Runtime.SetUser(stableUserId);
        /// <summary>Sends the queue present at the call. Cancellation ends only the wait.
        /// <param name="cancellationToken">Cancels the caller's wait.</param>
        /// <returns>A task that completes when the queue is drained, saved or the wait is cancelled.</returns></summary>
        public static Task Flush(CancellationToken cancellationToken = default) => host.Runtime.Flush(cancellationToken);
        /// <summary>Opt-out switch. Setting it to true clears the queue, forgets the user and is remembered across restarts.</summary>
        public static bool OptOut { get => host.Runtime.OptOut; set => host.Runtime.OptOut = value; }
        /// <summary>Call when the app is suspended or stopped; requests a best effort flush.</summary>
        public static Task OnPauseAsync() => host.PauseAsync();
        /// <summary>Call when window focus changes; losing focus requests a flush.
        /// <param name="hasFocus">True when the app gained focus.</param></summary>
        public static Task OnFocusChangedAsync(bool hasFocus) => host.FocusChangedAsync(hasFocus);
        /// <summary>Bounded final save, without waiting for HTTP. Terminal for this process.</summary>
        public static Task ShutdownAsync() => host.ShutdownAsync();
    }

    internal sealed class PulseHost
    {
        private readonly object gate = new object();
        internal readonly PulseRuntime Runtime;
        private readonly IClock clock;
        private readonly IStorage testStorage;
        private readonly ITransport testTransport;
        private PulsePlatformOptions platform = new PulsePlatformOptions();
        private HttpClientTransport ownedTransport;
        private bool initialized, stopping;
        private Task stop;

        internal PulseHost(IClock clock = null, IStorage storage = null, ITransport transport = null)
        {
            this.clock = clock ?? new SystemClock(); testStorage = storage; testTransport = transport;
            Runtime = new PulseRuntime(this.clock);
            Runtime.InstallPlatform(CreateClient, Log, () => PulseClient.ReadStoredOptOut(Storage()));
        }

        private IStorage Storage() => testStorage ?? new FileStorage(platform.StorageDirectory ?? PulseStorage.DefaultDirectory);
        private void Log(string message) { try { platform.Logger?.Invoke(message); } catch (Exception) { } }
        private PulseClient CreateClient(string key, PulseOptions options, bool? optOut)
        {
            var storage = Storage();
            var environment = platform.EnvironmentInfo ?? new DotNetEnvironmentInfo();
            var transport = testTransport ?? (ownedTransport = new HttpClientTransport());
            var client = new PulseClient(key, options, clock, transport, storage, environment, Log, initialOptOut: optOut);
            initialized = true;
            return client;
        }

        internal void Initialize(string appKey, PulseOptions options, PulsePlatformOptions requested = null)
        {
            try
            {
                lock (gate)
                {
                    if (initialized || stopping) return;
                    platform = new PulsePlatformOptions { StorageDirectory = requested?.StorageDirectory,
                        EnvironmentInfo = requested?.EnvironmentInfo, Logger = requested?.Logger };
                    Runtime.Initialize(appKey, options);
                }
            }
            catch (Exception) { }
        }

        internal Task PauseAsync() => Runtime.FlushInBackground(TimeSpan.FromMinutes(1));
        internal Task FocusChangedAsync(bool hasFocus) => hasFocus ? Task.CompletedTask : PauseAsync();
        internal Task ShutdownAsync()
        {
            lock (gate)
            {
                if (stop != null) return stop;
                stopping = true;
                // StopAsync begins on a worker even if synchronous storage is slow.
                stop = BoundedStopAsync(Task.Run(async () =>
                {
                    try { await Runtime.StopAsync().ConfigureAwait(false); }
                    finally { ownedTransport?.Dispose(); }
                }));
                return stop;
            }
        }

        private static async Task BoundedStopAsync(Task save)
        { try { await Task.WhenAny(save, Task.Delay(Pulse.FinalSaveTimeout)).ConfigureAwait(false); } catch (Exception) { } }
    }
}
