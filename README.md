# MightyS Pulse for .NET

Privacy-first analytics for .NET apps and games. No device IDs, offline queue, no dependencies.

Docs: https://pulse.mightys.dev/docs/dotnet/

MightyS Pulse lets a .NET app or game send named signals (events) with an optional number and a few properties, and see
them in a dashboard. It is a plain library for `netstandard2.0` and `net8.0` with no package dependencies, so it can be
used from desktop, mobile, console and game apps.

## Install

```text
dotnet add package Mightys.Pulse
```

or in a project file:

```xml
<PackageReference Include="Mightys.Pulse" Version="0.9.8" />
```

## Quick start

```csharp
using Mightys.Pulse;
using System.Collections.Generic;

Pulse.Initialize("YOUR_APP_KEY");           // the app key from your Pulse dashboard
Pulse.Track("level_complete", 42);          // a named signal with an optional number
Pulse.Track("purchase", 4.99, new Dictionary<string, object> { ["sku"] = "gems_100" });
await Pulse.Flush();                        // optional: send what is queued now
```

That is all it takes: users are counted without any further setup. If your app has its own user id, you can call
`Pulse.SetUser("your-user-id")` to use it instead of the anonymous install id (optional).

In a console app or any short process, send the queue and close cleanly before it exits:

```csharp
await Pulse.Flush(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); // send, waiting at most 10 seconds
await Pulse.ShutdownAsync();                                                    // save what is left for the next run
```

Call `Initialize` once at startup. `Track` is thread safe. Public calls never throw into your app and never block the
calling thread.

## See your first signal

1. Sign in at [app.pulse.mightys.dev](https://app.pulse.mightys.dev) and open the app whose key you used.
2. A debug or development build sends test data (see Test mode below). Turn on the **Test mode** switch in the bar
   above the figures to see it; turn it off again for real users.
3. Open **Live** in the app menu: signals appear there as they arrive. The Overview and the other screens follow shortly.

## What is sent and what is never sent

Sent:

- The signals you track: name (up to 200 characters), optional number, your properties and a UTC time. Properties may be
  strings, finite numbers and booleans (sent as `"true"` or `"false"`); other values are dropped.
- A random session id that rotates after 30 minutes without a signal.
- Device and app context, grouped below. A value the platform cannot give is left out, never guessed.
- A hashed anonymous install id, see below.
- An optional user id. `Pulse.SetUser("id")` hashes it with SHA-256 on the device; the raw id is never stored or sent.
  It replaces the install id until you call `Pulse.SetUser(null)`. The user id is not kept across restarts, so call
  `SetUser` again at startup.

### Device, app, settings and usage fields

Every signal carries these, read by the SDK with no setup and no permission:

| Group | Fields |
| --- | --- |
| Device | `platform`, `osName`, `osVersion`, `deviceModel` (yours if you pass one to `DotNetEnvironmentInfo`; otherwise PC on Windows, Desktop on Linux, the hardware model on macOS, empty on Android and iOS), `deviceType` (`desktop` on Windows, macOS and Linux), `architecture` (`x86_64`, `arm64`, `armv7`, `x86`), `runtimeVersion` (for example `.NET 9.0.7`) |
| App and install | `appName` (the entry assembly's product name), `appVersion`, `appBuildNumber` (only when you pass one), `isDebugBuild`, `sdkName`, `sdkVersion` |
| Language and time | `locale`, `language`, `preferredLanguage`, `region`, `layoutDirection`, `timeZone` (IANA id), `utcOffsetMinutes` |
| Usage statistics | `firstSessionDate`, `totalSessionsCount`, `distinctDaysUsed`, `distinctDaysUsedLastMonth`, `previousSessionSeconds`, `averageSessionSeconds`, `sessionSeconds`, `isNewInstall` (first session only) |

Values that only your app knows can be added without any other code change. Pass them in `PulsePlatformOptions`:
`HostFields` for fixed values (read once) and `DynamicHostFields` for values that change (read each time a signal is
captured, so keep it cheap):

```csharp
Pulse.Initialize("YOUR_APP_KEY", null, new PulsePlatformOptions
{
    HostFields = new Dictionary<string, string> { ["screenWidth"] = "1440", ["screenHeight"] = "900", ["screenScale"] = "2" },
    DynamicHostFields = () => new Dictionary<string, string> { ["colorScheme"] = IsDark() ? "dark" : "light" }
});
```

Accepted keys: `deviceType` (`phone`, `tablet`, `desktop`, `tv`, `watch`, `vision`, `car`, `web`), `deviceBrand`, `osApiLevel`,
`installSource`, `isSimulator`, `screenWidth`, `screenHeight`, `screenScale`, `screenWidthPx`, `screenHeightPx`, `screenDpi`,
`screenCount`, `orientation`, `colorScheme`, `fontScale`, `reduceMotion`, `boldText`, `invertColors`, `increaseContrast`,
`reduceTransparency`, `differentiateWithoutColor`, `connectionType`, `lowPowerMode` and the detected keys above (your value
wins). Values are strings, at most 64 characters, and must match the key's format; other keys and bad values are dropped.
On Android and iOS `deviceType` is only sent when you pass it. The SDK does not read the screen, the network or the
battery itself.

The usage statistics are kept in one small file named `usage` next to the install id and the queue. It holds the first
session date, the session count, recent distinct days and the durations of completed sessions of the last 90 days. Turning
opt-out on deletes it together with the install id. If it cannot be written, the numbers live in memory for that run.

### Automatic signals

The SDK sends these signals by itself or when your host reports the event: `pulse.session_started`, `pulse.new_install`,
`pulse.app_updated`, `pulse.screen_view`, `pulse.permission_changed`, `pulse.deep_link`, `pulse.app_opened`,
`pulse.app_backgrounded`, `pulse.heatmap` (taps and clicks, see Heatmaps) and, for errors, `pulse.error`. `pulse.app_opened` is off until the
dashboard switches it on. `pulse.app_backgrounded` is on by default when the host reports a background transition. Automatic fields such as `previousScreenSeconds`, `launchMs`, `foregroundsToday`,
`previousForegroundSeconds`, `notificationPermission`, `trackingPermission` and `droppedAutomatic` add no signals.
Defaults and what each event costs against your plan are listed at https://pulse.mightys.dev/docs/automatic-signals/.

| Signal | Default | Plan |
| --- | --- | --- |
| `pulse.session_started` | On | Free |
| `pulse.app_opened` | Off, opt in from the dashboard | Free |
| `pulse.app_backgrounded` | On, when the host reports background | Free |
| `pulse.new_install` | On | Free |
| `pulse.app_updated` | On | Free |

These five lifecycle types do not count toward your plan. The SDK saves the session end when the app goes to the background,
even if the background signal cannot be sent. The signal includes `foregroundSeconds` and the system field `sessionSeconds`;
the next launch reports `previousSessionSeconds`. A background transition flushes the queue immediately. If delivery cannot
finish, the saved signal is sent on the next launch with its original timestamp.

To switch things off: `PulseOptions.AutomaticSignals` (all automatic signals), `PulseOptions.AutomaticScreenViews`
(automatic screen capture), `PulseOptions.CaptureErrors` (error capture), `PulseOptions.CaptureHeatmaps` (heatmaps), `Pulse.OptOut` (everything), and in the
dashboard any single type (`disabledSignals`) or field group. A code option can only switch things off. All of them follow
the `Debug` flag like any other signal, and the SDK limits itself: per session at most 20 `pulse.app_opened`, 20
`pulse.app_backgrounded`, 10 `pulse.deep_link`, 5 `pulse.permission_changed` and 100 `pulse.screen_view`, and at most 300
automatic signals per install and local day. The number of dropped signals is sent once as `droppedAutomatic`.

### Heatmaps

Heatmaps (`pulse.heatmap`) are off until the owner switches them on in the dashboard. They collect only the screen name, the
position of a tap and its kind (tap or rage), never text, recordings, inputs or the keyboard. Plain .NET has no pointer
source, so this package sends no heatmap points and no screenshots; `PulseOptions.CaptureHeatmaps` is accepted and only
ever switches heatmaps off.

Plain .NET has no foreground, permission or deep link concept, so nothing here happens by itself. Your host reports it:

| Host event | Call |
| --- | --- |
| The app comes to the foreground (MAUI `Window.Activated`) | `Pulse.ReportForeground(coldStart)` |
| The app goes to the background (MAUI `Window.Deactivated` or `Stopped`) | `await Pulse.OnPauseAsync()` (also reports background) |
| The app is opened by a link | `Pulse.DeepLink(url)` |
| You read a permission status | `Pulse.ReportPermission("notification", "granted")` |

Call them after `Initialize`. Repeated reports of the same state are ignored. `ReportBackground` also stops the screen timer, so
`previousScreenSeconds` (whole seconds the previous screen was visible) never includes background time. The SDK never asks for a permission:
it only records the status you pass (`notification`: granted, denied, notDetermined, provisional, ephemeral; `tracking`:
authorized, denied, restricted, notDetermined). `pulse.deep_link` carries `scheme`, `host` and `path` (numeric and uuid-like segments become `:id`, at most 128
characters) and the three `utm_source`, `utm_medium`, `utm_campaign` tags if present. The query, the fragment and every other
parameter are never read or sent. `launchMs` is the time from process start to the first signal, left out when it is longer than ten minutes.

`pulse.app_opened` is sent only when the dashboard lists it in `enabledSignals` for your app; with no saved settings it is not sent.
`pulse.app_backgrounded` is on by default, unless automatic signals are off or the type is in `disabledSignals` or `pausedSignals`.
A background signal 30 minutes or more after the last signal is dropped and never starts a session. Your own `Track` calls and errors are never limited.

### Screen views

Call `Pulse.Screen("Name")` when a screen appears. It sends `pulse.screen_view` with the property `screen` and, when
there was an earlier screen, `previousScreen`. Names are trimmed and cut to 128 characters, an empty name is ignored, and
calling it again with the same name as the current screen sends nothing. Use a fixed screen name, never a URL, a title
that contains user text or anything the user typed. It follows opt-out and the `Debug` flag like any other signal.

```csharp
Pulse.Screen("Settings");
```

Plain .NET has no screen concept, so nothing is captured automatically. In a MAUI app, one call from a Shell navigation
event covers every page:

```csharp
Shell.Current.Navigating += (_, e) => Pulse.Screen(e.Target.Location.OriginalString.Split('?')[0]);
```

`PulseOptions.AutomaticScreenViews` (default `true`) only controls integrations that report screens by themselves; the
.NET package has none, and `Pulse.Screen` always works.

### Settings from the portal

The dashboard can switch things off for your app without a package update: screen views, the automatic signals (all of
them or one type), and groups of context fields (device, screen, settings and accessibility, network, app, usage). The
SDK reads these settings once per launch, in the background, saves them next to the offline queue and applies them from
the next launch, so a change in the dashboard shows up after your users restart the app. Reading them never delays
`Track` or `Flush`.

Everything is on by default, and stays on whenever the settings cannot be read (no network, an error, nothing saved
yet). The system fields that every signal needs (platform, OS, app version and build, device model, locale, SDK name and
version, bundle id) are never switched off. A code option can only switch things off, never force them on: `AutomaticScreenViews = false` stops automatic screen
capture, `AutomaticSignals = false` stops automatic lifecycle signals, including `pulse.app_backgrounded`, even when
the dashboard has them on. Setting either to `true` does not override the dashboard (the server drops what the
dashboard switched off). A manual `Pulse.Screen` call is sent whenever the dashboard allows screen views. Turning opt-out on deletes the saved settings with the rest.

### Anonymous install id

The SDK creates a random install id on first launch and keeps it in the app's own storage folder, next to the offline
queue. It is random, not derived from any device identifier, not shared between apps, and deleted when the app and its
data are removed. It is sent only as a SHA-256 hash. Turning opt-out on deletes it, and turning opt-out off creates a new
one.

Never sent: an advertising id, a device identifier, a cookie, a fingerprint, a machine name, a location, contacts, or
automatic gameplay events.

The SDK does not read or send the device's IP address as data. As with any HTTP request, the server sees the connection
address; it uses it only to derive a two-letter country code and does not store it.

Keep personal data out of signal names and properties. Queued signals are stored on the device without encryption.

## Options

Pass options to `Initialize`:

```csharp
Pulse.Initialize("YOUR_APP_KEY",
    new PulseOptions { FlushIntervalSeconds = 30, MaxQueueSize = 5000 },
    new PulsePlatformOptions { Logger = Console.WriteLine });
```

| Option | Default | Meaning |
| --- | --- | --- |
| `PulseOptions.Host` | `https://in.mightys.dev` | Base URL of the ingest host (HTTP or HTTPS, no user info, query or fragment). |
| `PulseOptions.FlushIntervalSeconds` | 20 | Seconds between batches after the first one. Maximum 3600. |
| `PulseOptions.MaxBatchSize` | 100 | Signals per batch, 1 to 1000. |
| `PulseOptions.MaxQueueSize` | 1000 | Signals kept queued, 1 to 1000000. The oldest are dropped first. |
| `PulseOptions.MaxQueueAgeDays` | 7 | Age after which a queued signal is dropped. Maximum 29. |
| `PulseOptions.Gzip` | `true` | Compress request bodies larger than 1024 bytes. |
| `PulseOptions.Debug` | Debug build: `true`, otherwise `false` | Marks signals as test data and sends diagnostic messages to `Logger`. See Test mode. |
| `PulseOptions.AppVersion` | detected | Overrides the detected app version. |
| `PulseOptions.AutomaticScreenViews` | `true` | Lets platform integrations send screen views by themselves. See Screen views. Wins over the dashboard when you set it. |
| `PulseOptions.AutomaticSignals` | `true` | Allows automatic lifecycle signals, including `pulse.app_backgrounded`. Set `false` to stop them even when the dashboard has them on. Manual screen views follow the dashboard. See Settings from the portal. |
| `PulseOptions.CaptureErrors` | `true` | Captures unhandled errors and crashes. Set `false` to install no handler at all. See Errors and crashes. |
| `PulseOptions.CaptureHeatmaps` | `true` | Follows the dashboard for heatmaps of taps and clicks (off until you switch them on there). Set `false` to never capture them. Plain .NET has no pointer source, so this package never sends heatmap points or pictures; the option exists so one options object works across platforms. See Heatmaps. |
| `PulseOptions.ErrorDetail` | `Full` | `Full` sends type, filtered message and stack frames; `TypeOnly` sends the error type only. |
| `PulseOptions.Control` | `ControlMode.Automatic` | App Control: `Automatic` and `Manual` check by themselves and keep the state (this package has no screen of its own), `Off` makes no request. See App Control. |
| `PulsePlatformOptions.OpenUrl` | system browser on desktop | Opens the address behind `Pulse.Control.OpenStore()`, for example with MAUI `Launcher.OpenAsync`. |
| `PulsePlatformOptions.StorageDirectory` | per-app folder, see Requirements | Folder for the offline queue. |
| `PulsePlatformOptions.EnvironmentInfo` | detected | OS and app details to send, for hosts where detection does not fit. |
| `PulsePlatformOptions.Logger` | none | Receives diagnostic strings when `Debug` is on. Do not block in it. |

## Test mode

Every signal carries a `debug` flag; `true` means test data, and the dashboard's test mode switch chooses whether you see
test signals or real ones. One app key serves both. If you do not set `PulseOptions.Debug`, a Debug build of your app sends
test signals and a Release build sends real ones. The library checks how your app's entry assembly was compiled; an
attached debugger and environment variables make no difference. Hosts that have no entry assembly
count as real, so set `Debug` yourself there. An explicit `true` or `false` always wins, and the choice is made once, in
`Initialize`:

```csharp
Pulse.Initialize("YOUR_APP_KEY", new PulseOptions {
#if DEBUG
    Debug = true,
#endif
});
```

## Delivery and offline behavior

- Signals are batched and sent in the background. The first batch goes out right after `Initialize`; later batches follow
  every `FlushIntervalSeconds` or when a batch is full.
- While the device is offline, signals wait in a queue on disk. The queue survives restarts and is sent when the
  connection is back.
- Failed sends are retried with exponential backoff (1 second up to 5 minutes), and a server `Retry-After` is respected.
  If the app key is rejected (HTTP 401), sending stops until the next start and the queue is kept. Signals the server
  rejects as invalid are dropped.
- Delivery is at least once: a batch can arrive twice, so treat duplicates as normal. Each batch carries an id, so a batch sent again after a lost answer is stored only once.
- A signal can still be lost if the process is killed before it is saved, when the queue limits are reached, or on opt-out.

`Flush` waits until the signals queued at the call have been sent. While offline it stays pending, and the cancellation
token ends only your wait. `OnPauseAsync` waits at most one minute for a flush. `ShutdownAsync` saves the queue to disk
(waiting at most two seconds) and does not wait for the network.

## App lifecycle

A desktop, mobile or game host should make three calls. Each returns a `Task`, never throws and needs no UI thread.

| When | Call | What it does |
| --- | --- | --- |
| The app gains focus | `Pulse.OnFocusChangedAsync(true)` | Reports foreground and resumes foreground timing. |
| The app loses focus | `Pulse.OnFocusChangedAsync(false)` | Reports background and sends the queue now. |
| The app goes to the background or is suspended | `Pulse.OnPauseAsync()` | Saves the session end, reports background and sends the queue now (best effort: the OS may suspend the app first). |
| The app is closing | `Pulse.ShutdownAsync()` | Saves the queue and stops the SDK for this process. It cannot be restarted. |

```csharp
using Mightys.Pulse;
using System.IO;

// Mobile apps must pass a writable folder; desktop apps may omit the platform options.
Pulse.Initialize("YOUR_APP_KEY", new PulseOptions(), new PulsePlatformOptions
{
    StorageDirectory = Path.Combine(appDataFolder, "MightysPulse")
});

// In your host's focus, background and exit handlers:
_ = Pulse.OnFocusChangedAsync(false);
_ = Pulse.OnPauseAsync();
await Pulse.ShutdownAsync();   // before the process ends
```

The offline queue folder must be stable, used by one app only, and used by one running process at a time. There is no
default on Android and iOS and no fallback to the working directory, so pass your app's data folder as
`StorageDirectory` there. On mobile, also pass `EnvironmentInfo` built from your framework's device API, for example
`new DotNetEnvironmentInfo("Android", osVersion, appVersion, buildNumber, locale, deviceModel)`.

## Errors and crashes

By default the SDK reports errors that nobody handled, so you can see where the app broke:

- `AppDomain.UnhandledException` is reported as a crash. It is saved to disk before the process ends and sent the next
  time the app starts.
- `TaskScheduler.UnobservedTaskException` is reported as an error. The task is not marked as observed, so the runtime
  behaves exactly as before.

Handlers that were there before keep running, the SDK never swallows an error and never throws from its own handler.

Each report is a `pulse.error` signal with the error type, a message, up to 30 stack frames (type, method, file name and
line, for example `MyApp.CartViewModel.Checkout() CartViewModel.cs:42`) and `symbolicated`, which is `false` only when the
runtime gave no method names. Line numbers need the app's `.pdb` files next to the build.

Report an error you caught yourself:

```csharp
try { await cart.CheckoutAsync(); }
catch (Exception ex)
{
    Pulse.TrackError(ex, new Dictionary<string, object> { ["step"] = "checkout" });
    throw;
}
```

What is filtered: before anything leaves the device, the message and every frame lose email addresses, query strings and
fragments of URLs, UUIDs, IPv4 and IPv6 addresses, runs of six or more digits, and the folders of absolute paths (the
file name stays). Matches become `<filtered>`.

To send only the error type, without message and frames:

```csharp
Pulse.Initialize("YOUR_APP_KEY", new PulseOptions { ErrorDetail = PulseErrorDetail.TypeOnly });
```

To turn the automatic capture off completely, set `CaptureErrors = false`. `Pulse.TrackError` still works. At most 10
error signals per error type are sent in one session, so a loop cannot flood the dashboard. Opting out stops capture and
deletes a crash that is waiting to be sent. Native crashes of the runtime itself and minified names are not readable yet.

## App Control

App Control lets you require an update, suggest one or put the app into maintenance from the dashboard. It runs by itself after
`Pulse.Initialize`. This package has no built-in screen: you read the state and draw your own.

```csharp
Pulse.Control.Changed += (sender, state) =>
{
    // Runs on a thread pool thread: switch to the UI thread first.
    switch (state.Kind)
    {
        case ControlKind.UpdateRequired:
        case ControlKind.VersionBlocked:
        case ControlKind.Maintenance:
            ShowFullScreen(state.Title, state.Body, state.ButtonText, state.AllowContinue);
            break;
        case ControlKind.UpdateSuggested:
            ShowDialog(state.Title, state.Body, state.ButtonText, state.DismissText);
            break;
        case ControlKind.Normal:
            HideEverything();
            break;
    }
};
// The button: Pulse.Control.OpenStore();  "Not now": Pulse.Control.Dismiss();  "Try again": await Pulse.Control.CheckNowAsync();
```

MAUI: subscribe in `App` or your first page and call `Pulse.Control.OnForeground()` from `Window.Activated`. WPF and WinForms: call it from
`Application.Activated` or the window's `Activated` event. Plain `Pulse.ReportForeground()` does the same.

| Member | Meaning |
| --- | --- |
| `Pulse.Control.State` | The current state: `Kind` (`Normal`, `UpdateSuggested`, `UpdateRequired`, `VersionBlocked`, `Maintenance`, `MaintenanceUpcoming`, `Announcement`, `WhatsNew`), ready-to-show `Title`, `Body`, `ButtonText` in the language of the device (12 built in, English otherwise), `Url`, `StartsAt`, `EndsAt`, `Version`, `AllowContinue`, `IsTestBuild`, `DismissText` and `Extras` (a card or banner that applies at the same time). |
| `Pulse.Control.Changed` | Raised when the state changes, also when a scheduled window starts or ends. |
| `Pulse.Control.CheckNowAsync()` | Checks now. Never throws. |
| `Pulse.Control.OpenStore()` | Opens the store address of the current state in the system browser when the platform allows it (set `PulsePlatformOptions.OpenUrl` elsewhere). |
| `Pulse.Control.Dismiss()` | Dismisses a suggestion (for 24 hours), a card or the preview lock of a debug build. Does nothing for a lock in a release build. |
| `Pulse.Control.OnForeground()` | Tells the SDK the app came to the front. It checks again, at most once in 60 seconds. |

Set `PulseOptions.Control = ControlMode.Off` to make no request at all. Opting out of analytics does not switch App Control off, and
the request carries no app key, no install id and no user id.

For the notes you send with a store submission: "The app checks on launch for required updates and maintenance windows."

### How it fails safely

1. Starting the SDK never waits for the network, and a check never runs on the thread that calls you.
2. One request per check, 3 seconds to the response headers and 5 seconds in all, no retry loop, and at most one automatic check in 60 seconds.
3. Any failure (no network, a server error, an answer that cannot be read) keeps the last known state, or `Normal`. Nothing is ever locked because of something unknown.
4. A lock older than 30 days is ignored, a maintenance window from the saved answer ends at its end time and never lasts more than 72 hours.
5. There is always a way out: a required update turns into a suggestion when the store version is not newer than the installed one, after three returns from the store on the same version, or when there is no store link. The SDK never closes the app.

In a debug build the rules apply but a lock is shown as a preview (`IsTestBuild`) that can be continued.

## Requirements

| Target | Used by |
| --- | --- |
| `net8.0` | .NET 8 and later |
| `netstandard2.0` | Older .NET runtimes |

The runtime needs managed threads and a writable file system. Default queue folder:

| OS | Folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\MightyS\Pulse\<app>` |
| macOS | `~/Library/Application Support/MightyS/Pulse/<app>` |
| Linux | `$XDG_DATA_HOME/MightyS/Pulse/<app>`, usually `~/.local/share/MightyS/Pulse/<app>` |
| Android and iOS | none: set `StorageDirectory` |

## Opt-out

```csharp
Pulse.OptOut = true;   // stops collection, clears the queue, forgets the user id and deletes the install id
```

The choice is remembered across restarts and applies to every app that shares the same queue folder.

## Build from source

```text
dotnet build -c Release
dotnet test tests/DotNet -c Release
dotnet test tests/SharedCore -c Release
dotnet pack src/Mightys.Pulse -c Release -o artifacts
```

The test projects target `net10.0`, so they need the .NET 10 SDK. A console sample is in `samples/Quickstart`.

## Licence

MIT. See `LICENSE.md`.
