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
<PackageReference Include="Mightys.Pulse" Version="0.9.6" />
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

Call `Initialize` once at startup. `Track` is thread safe. Public calls never throw into your app and never block the
calling thread.

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

The SDK sends three signals by itself, so you can count installs, updates and sessions without any code:
`pulse.session_started` (once per session, that is on the first signal of the run and after 30 minutes without one),
`pulse.new_install` (once, in the first session after install) and `pulse.app_updated` (once per release, with the
properties `previousVersion` and `previousBuild`). They follow opt-out and the `Debug` flag like any other signal, and
they are rare by design.

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
capture, `AutomaticSignals = false` stops `pulse.session_started`, `pulse.new_install` and `pulse.app_updated`, even when
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
| `PulseOptions.AutomaticSignals` | `true` | Sends `pulse.session_started`, `pulse.new_install`, `pulse.app_updated` and `pulse.screen_view`. Set `false` to stop the first three even when the dashboard has them on. See Settings from the portal. |
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
| The app loses focus | `Pulse.OnFocusChangedAsync(false)` | Sends the queue now. |
| The app goes to the background or is suspended | `Pulse.OnPauseAsync()` | Sends the queue now (best effort: the OS may suspend the app first). |
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
