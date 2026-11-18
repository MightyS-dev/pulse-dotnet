# Changelog

All notable changes to this package are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package follows
[Semantic Versioning](https://semver.org/).

## [0.9.8] - 2026-11-18

Session length: the SDK reports when the app goes to the background, so sessions show their real length. `pulse.app_backgrounded` is on by default and does not count toward your plan.

## [0.9.7] - 2026-11-16

### Added

- App Control: required updates, suggested updates and maintenance windows set in the dashboard, with no screen of its own on .NET.
  `Pulse.Control.State`, `Pulse.Control.Changed`, `CheckNowAsync()`, `OpenStore()`, `Dismiss()` and `OnForeground()`. It checks once after
  `Initialize` and when the app comes to the front (at most once in 60 seconds), with 3 and 5 second limits and no retry loop, and fails
  open: any problem keeps the last known state or `Normal`. The request carries no identity and does not depend on the analytics
  opt-out. Option `PulseOptions.Control` (`Automatic`, `Manual`, `Off`) and `PulsePlatformOptions.OpenUrl`.
- Errors and crashes with where they happened: unhandled exceptions are sent as `pulse.error` with the error type, a
  filtered message and readable stack frames (type, method, file and line). A crash is saved at once and sent at the next
  start. `Pulse.TrackError(exception, props)` reports an error you handled. Options: `CaptureErrors` (default `true`) and
  `ErrorDetail` (`Full` or `TypeOnly`). At most 10 reports per error type and session. Emails, URL queries, UUIDs, IP
  addresses, long numbers and folders of paths are removed on the device.
- More automatic data: `previousScreenSeconds` on screen views (background time excluded when the host calls
  `Pulse.ReportForeground` and `Pulse.ReportBackground`), `launchMs` on the first `pulse.session_started` of a run,
  `foregroundsToday` and `previousForegroundSeconds`, permission statuses (`Pulse.ReportPermission`) with
  `pulse.permission_changed`, `Pulse.DeepLink(url)` with `pulse.deep_link`, and the opt-in `pulse.app_opened` and
  `pulse.app_backgrounded` that the dashboard must switch on (`enabledSignals`). Types listed in `pausedSignals` are not sent.
  Quota guards: per session 20 app_opened, 20 app_backgrounded, 10 deep_link, 5 permission_changed, 100 screen_view; per install
  and local day 300 automatic signals; the number of dropped ones is sent once as `droppedAutomatic`.
- Heatmap option `PulseOptions.CaptureHeatmaps` (default `true`, can only switch heatmaps off). Plain .NET has no pointer
  source and never sends heatmap points or pictures.

### Fixed

- `timeZone` is sent as a canonical zone id. A legacy alias reported by the system (for example `Egypt`) is mapped to its
  Area/Location id (`Africa/Cairo`), or left out when it cannot be mapped.

## [0.9.6] - 2026-11-16

### Fixed

- A signal that is dropped because its name or value is not valid no longer starts a session or keeps one alive.
- Flush returns at once when nothing is waiting to be sent, even if the device storage cannot be read or written.

## [0.9.5] - 2026-11-16

### Fixed

- A batch that is sent again after a lost answer is stored only once.

## [0.9.4] - 2026-11-16

### Added

- Screen views: `Pulse.Screen("Name")` sends `pulse.screen_view` with `screen` and `previousScreen`. Repeats of the
  current screen are ignored. The new option `PulseOptions.AutomaticScreenViews` (default `true`) controls integrations
  that capture screens by themselves; the .NET package has none, so you call `Pulse.Screen` yourself, for example from a
  Shell navigation event in a MAUI app.
- Settings from the portal: the SDK reads the app's settings once per launch and applies them from the next launch, so
  the dashboard can switch screen views, automatic signals and groups of context fields on or off without a package
  update. Everything stays on by default and whenever the settings cannot be read. The new option
  `PulseOptions.AutomaticSignals` (default `true`) turns the automatic signals off from code. Code options only switch things
  off; they never override the dashboard.

### Fixed

- Signals captured in the same millisecond keep their order: timestamps are strictly increasing.

## [0.9.3] - 2026-11-15

### Added

- Every signal now carries more context, with no setup: device type, architecture, .NET runtime version, app name, whether
  the app is a debug build, language, preferred language, region, text direction, time zone and UTC offset.
- Usage statistics on every signal: first session date, number of sessions, distinct days used (all time and last 30
  days), previous and average session length, seconds since the session started, and a flag during the first session
  after install. They are kept in one small file next to the install id and deleted on opt-out.
- Three automatic signals: `pulse.session_started` (once per session), `pulse.new_install` and `pulse.app_updated`
  (with `previousVersion` and `previousBuild`).
- `PulsePlatformOptions.HostFields` and `DynamicHostFields` let a MAUI, WPF or Avalonia app add values only it knows,
  such as screen size or colour scheme.

## [0.9.2] - 2026-11-15

### Changed

- Users are counted without any setup: the SDK sends a random anonymous install id (hashed) until you call `Pulse.SetUser`.
  The id is stored in the SDK's own folder next to the offline queue and is deleted on opt-out.
- `DotNetEnvironmentInfo` now reports a device model: pass your own (for example MAUI `DeviceInfo.Model`) or get PC on
  Windows, Desktop on Linux and the hardware model on macOS (Mac when unreadable). It is empty on Android and iOS unless you pass one.

## [0.9.1] - 2026-11-15

### Changed

- Signals from Debug builds of your app are now marked as test data by default; Release builds send real data.
  `PulseOptions.Debug` still overrides this in both directions.

### Added

- README: a Test mode section that explains how signals are marked as test or real.

## [0.9.0] - 2026-11-15

Initial public release.

### Added

- Named signals with an optional number and properties (`Pulse.Track`).
- Random session ids that rotate after 30 minutes of inactivity.
- Optional user id (`Pulse.SetUser`); only its SHA-256 hash leaves the device.
- Opt-out (`Pulse.OptOut`): one call, remembered across restarts, clears the queue.
- No advertising id, no device identifier, no cookies, no fingerprinting.
- Batched, gzipped delivery: the first batch is sent at once, later batches follow an interval (20 seconds by default)
  or a batch size.
- Offline queue on disk that survives restarts, with retries and exponential backoff.
- Public calls never throw into the host app and never block the calling thread.
- Lifecycle calls for desktop, mobile and game hosts: `OnFocusChangedAsync`, `OnPauseAsync`, `ShutdownAsync`.
- Options for host, flush interval, batch and queue size, queue age, gzip, debug mode, app version, queue folder,
  environment snapshot and logger.
- Targets `netstandard2.0` and `net8.0`, no package dependencies, MIT licence.
