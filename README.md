# MightyS Pulse for .NET

Privacy-first analytics for apps and games. No device IDs, offline queue, tiny.

MightyS Pulse lets a .NET app or game send named signals (events) with an optional number and a few properties, and see
them in a dashboard. It is a plain library for `netstandard2.0` and `net8.0` with no package dependencies, so it can be
used from desktop, mobile, console and game apps.

## Install

```text
dotnet add package Mightys.Pulse
```

or in a project file:

```xml
<PackageReference Include="Mightys.Pulse" Version="0.9.0" />
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

Call `Initialize` once at startup. `Track` is thread safe. Public calls never throw into your app and never block the
calling thread.

## What is sent and what is never sent

Sent:

- The signals you track: name (up to 200 characters), optional number, your properties and a UTC time. Properties may be
  strings, finite numbers and booleans (sent as `"true"` or `"false"`); other values are dropped.
- A random session id that rotates after 30 minutes without a signal.
- OS name and version, app version, locale, and the SDK name and version.
- An optional user id. `Pulse.SetUser("id")` hashes it with SHA-256 on the device; the raw id is never stored or sent.
  The user id is not kept across restarts, so call `SetUser` again at startup.

Never sent: an advertising id, a device identifier, a cookie, a fingerprint, a machine name, a location, contacts, or
automatic screen or gameplay events.

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
| `PulseOptions.Debug` | `false` | Marks signals as test data and sends diagnostic messages to `Logger`. |
| `PulseOptions.AppVersion` | detected | Overrides the detected app version. |
| `PulsePlatformOptions.StorageDirectory` | per-app folder, see Requirements | Folder for the offline queue. |
| `PulsePlatformOptions.EnvironmentInfo` | detected | OS and app details to send, for hosts where detection does not fit. |
| `PulsePlatformOptions.Logger` | none | Receives diagnostic strings when `Debug` is on. Do not block in it. |

## Delivery and offline behavior

- Signals are batched and sent in the background. The first batch goes out right after `Initialize`; later batches follow
  every `FlushIntervalSeconds` or when a batch is full.
- While the device is offline, signals wait in a queue on disk. The queue survives restarts and is sent when the
  connection is back.
- Failed sends are retried with exponential backoff (1 second up to 5 minutes), and a server `Retry-After` is respected.
  If the app key is rejected (HTTP 401), sending stops until the next start and the queue is kept. Signals the server
  rejects as invalid are dropped.
- Delivery is at least once: a batch can arrive twice, so treat duplicates as normal.
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
`new DotNetEnvironmentInfo("Android", osVersion, appVersion, buildNumber, locale)`.

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
Pulse.OptOut = true;   // stops collection, clears the queue, forgets the user id
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
