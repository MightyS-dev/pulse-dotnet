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
        /// <summary>Extra system fields the host knows and the runtime cannot tell, such as screenWidth, screenHeight, screenScale,
        /// colorScheme, orientation, fontScale, connectionType or deviceType on a phone. Read once; they win over detected values and are
        /// ignored when you supply your own <see cref="EnvironmentInfo"/>. Only the documented system field names are sent.</summary>
        public IDictionary<string, string> HostFields { get; set; }
        /// <summary>Like <see cref="HostFields"/> for values that change. Called each time a signal is captured, so keep it cheap and never block.</summary>
        public Func<IDictionary<string, string>> DynamicHostFields { get; set; }
        /// <summary>Opens an address for <see cref="PulseControl.OpenStore"/>, for example MAUI <c>Launcher.OpenAsync</c>. Return true when it was handed to the system. Default: the system browser through the shell on desktop systems, nothing elsewhere.</summary>
        public Func<string, bool> OpenUrl { get; set; }
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
        /// <summary>Sends a handled error as <c>pulse.error</c> (kind handled) with its type, filtered message and stack frames. Never throws.
        /// <param name="exception">The error you caught.</param>
        /// <param name="props">Optional properties: strings, finite numbers and booleans.</param></summary>
        public static void TrackError(Exception exception, IDictionary<string, object> props = null) => host.Runtime.TrackError(exception, props);
        /// <summary>Records a screen view (<c>pulse.screen_view</c>). Names are trimmed to 128 characters; empty names and repeats of the current screen are ignored.
        /// <param name="name">A fixed screen name, never a URL or text the user typed.</param></summary>
        public static void Screen(string name) => host.Runtime.Screen(name, false);
        /// <summary>Reports a link that opened the app as <c>pulse.deep_link</c>: scheme, host and path (numeric and uuid-like segments become <c>:id</c>), plus the three utm_ campaign tags. The query, the fragment and every other parameter are never sent. Call after <see cref="Initialize"/>.
        /// <param name="url">The full link, for example the activation URI.</param></summary>
        public static void DeepLink(string url) => host.Runtime.DeepLink(url);
        /// <summary>Call when the host app comes to the foreground (for example MAUI <c>Window.Activated</c>). Repeats without a background in between are ignored.
        /// It counts the foreground, restarts the screen timer and sends <c>pulse.app_opened</c> when the portal has switched that event on. Nothing calls it automatically on plain .NET. Call after <see cref="Initialize"/>.
        /// <param name="coldStart">True for the first foreground after the process started.</param></summary>
        public static void ReportForeground(bool coldStart = false) => host.Runtime.ReportForeground(coldStart);
        /// <summary>Call when the host app goes to the background (for example MAUI <c>Window.Deactivated</c> or <c>Stopped</c>). Time in the background is excluded from <c>previousScreenSeconds</c>.
        /// Sends <c>pulse.app_backgrounded</c> when the portal has switched it on. Call <see cref="OnPauseAsync"/> after it to flush.</summary>
        public static void ReportBackground() => host.Runtime.ReportBackground();
        /// <summary>Reports a permission status you read yourself (the SDK never asks for permissions): <c>notification</c> with granted, denied, notDetermined, provisional or ephemeral; <c>tracking</c> with authorized, denied, restricted or notDetermined.
        /// The status is added to every signal, and a change from the last known status sends <c>pulse.permission_changed</c> once.
        /// <param name="name">notification or tracking.</param>
        /// <param name="status">The status in the words above.</param></summary>
        public static void ReportPermission(string name, string status) => host.Runtime.ReportPermission(name, status);
        /// <summary>Sends the queue present at the call. Cancellation ends only the wait.
        /// <param name="cancellationToken">Cancels the caller's wait.</param>
        /// <returns>A task that completes when the queue is drained, saved or the wait is cancelled.</returns></summary>
        public static Task Flush(CancellationToken cancellationToken = default) => host.Runtime.Flush(cancellationToken);
        /// <summary>App Control: required updates and maintenance windows set in the portal. It starts by itself after <see cref="Initialize"/>, has no screen of its own on .NET
        /// and never blocks or throws. Read <see cref="PulseControl.State"/>, subscribe to <see cref="PulseControl.Changed"/> and draw your own screen.
        /// Call <see cref="PulseControl.OnForeground"/> (or <see cref="ReportForeground"/>) when the app comes back to the front. Turn it off with <see cref="PulseOptions.Control"/>.</summary>
        public static PulseControl Control => host.Runtime.Control;
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
        private readonly object storageGate = new object();
        internal readonly PulseRuntime Runtime;
        internal readonly InstallId InstallId;
        internal ErrorHooks Hooks => hooks;
        internal UsageTracker Usage;
        private readonly IClock clock;
        private readonly IStorage testStorage;
        private readonly ITransport testTransport;
        private readonly Func<bool> debugDefault;
        private readonly bool statistics;
        private PulsePlatformOptions platform = new PulsePlatformOptions();
        private HttpClientTransport ownedTransport;
        private readonly ErrorHooks hooks;
        private bool initialized, stopping;
        private Task stop;

        internal PulseHost(IClock clock = null, IStorage storage = null, ITransport transport = null, Func<bool> debugDefault = null, bool usage = true)
        {
            this.statistics = usage;
            this.clock = clock ?? new SystemClock(); testStorage = storage; testTransport = transport;
            this.debugDefault = debugDefault ?? (() => BuildMode.EntryIsDebugBuild());
            Runtime = new PulseRuntime(this.clock);
            hooks = new ErrorHooks(Runtime);
            InstallId = new InstallId(Storage);
            Runtime.InstallPlatform(CreateClient, Log, () => PulseClient.ReadStoredOptOut(Storage()), InstallId.Hash, InstallId.Forget);
        }

        // One lock for every storage call of this host, so the install id and the queue never touch storage at once.
        private IStorage Storage() => new SerializedStorage(testStorage ?? new FileStorage(platform.StorageDirectory ?? PulseStorage.DefaultDirectory), storageGate);
        private void Log(string message) { try { platform.Logger?.Invoke(message); } catch (Exception) { } }
        private PulseClient CreateClient(string key, PulseOptions options, bool? optOut)
        {
            var storage = Storage();
            var environment = platform.EnvironmentInfo ?? new DotNetEnvironmentInfo(platform.HostFields, platform.DynamicHostFields);
            var transport = testTransport ?? (ownedTransport = new HttpClientTransport());
            // Read or create the install id before the client's worker starts using storage.
            var usage = Usage = statistics ? new UsageTracker(Storage, installIsNew: InstallId.ConsumeCreated) : null;
            if (optOut != true && !StoredOptOutSafe(storage)) { InstallId.Prime(); usage?.Prime(); }
            var client = new PulseClient(key, options, clock, transport, storage, environment, Log, initialOptOut: optOut, usage: usage);
            client.LaunchProvider = LaunchTime.SinceProcessStart;
            initialized = true;
            return client;
        }

        private static bool StoredOptOutSafe(IStorage storage)
        { try { return PulseClient.ReadStoredOptOut(storage); } catch (Exception) { return true; } }

        internal void Initialize(string appKey, PulseOptions options, PulsePlatformOptions requested = null)
        {
            try
            {
                lock (gate)
                {
                    if (initialized || stopping) return;
                    platform = new PulsePlatformOptions { StorageDirectory = requested?.StorageDirectory,
                        EnvironmentInfo = requested?.EnvironmentInfo, Logger = requested?.Logger,
                        HostFields = requested?.HostFields, DynamicHostFields = requested?.DynamicHostFields, OpenUrl = requested?.OpenUrl };
                    // The default for PulseOptions.Debug is decided here, once. An explicit option still wins.
                    PulseInfo.DebugBuild = debugDefault();
                    Runtime.Control.Opener = requested?.OpenUrl ?? DefaultOpenUrl;
                    Runtime.Initialize(appKey, options);
                    if (initialized && Runtime.CaptureErrorsEnabled) hooks.Install();
                }
            }
            catch (Exception) { }
        }

        // The system browser through the shell, for web addresses only. Where the platform has no shell (phones) this fails quietly.
        private static bool DefaultOpenUrl(string address)
        {
            try
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return false;
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        internal Task PauseAsync() => Runtime.FlushInBackground(TimeSpan.FromMinutes(1));
        internal Task FocusChangedAsync(bool hasFocus)
        {
            if (!hasFocus) return PauseAsync();
            Runtime.Control.OnForeground();
            return Task.CompletedTask;
        }
        internal Task ShutdownAsync()
        {
            lock (gate)
            {
                if (stop != null) return stop;
                stopping = true; hooks.Uninstall();
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

    // Serializes calls to the wrapped storage, because the queue worker and the install id both use it.
    internal sealed class SerializedStorage : IStorage
    {
        private readonly IStorage inner;
        private readonly object gate;
        internal SerializedStorage(IStorage inner, object gate) { this.inner = inner; this.gate = gate; }
        public byte[] Read(string name) { lock (gate) return inner.Read(name); }
        public void Append(string name, byte[] bytes) { lock (gate) inner.Append(name, bytes); }
        public void Replace(string name, byte[] bytes) { lock (gate) inner.Replace(name, bytes); }
        public void Delete(string name) { lock (gate) inner.Delete(name); }
    }
}
