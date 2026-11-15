# Changelog

All notable changes to this package are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package follows
[Semantic Versioning](https://semver.org/).

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
