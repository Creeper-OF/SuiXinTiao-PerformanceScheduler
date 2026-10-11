# SuiXinTiao

> SuiXinTiao is a foreground-first energy scheduling app for Windows laptops and desktop PCs. It applies app profiles, power-state rules, and safety rollback to balance responsiveness, temperature, power draw, and stability.

[中文 README](README.md)

## Status

SuiXinTiao is currently an early prototype / MVP. It already includes a runnable WPF desktop app, console page, scheduling configuration page, diagnostics, appearance customization, safety rollback, Chinese / English localization, and baseline tests.

The project does not aim to bypass BIOS, GPU driver, or operating-system limits. It prioritizes public Windows APIs and user-mode behavior so scheduling changes remain reversible, explainable, and extensible.

## Terms

- App Profile: scheduling rules for a game, application, or usage scenario.
- Device Strategy Pack: a group of app profiles prepared for a specific laptop model or hardware combination.
- Migration Package: an import/export package containing app profiles, device strategy packs, and optional app settings.
- Auto Scheduling: automatically matching and applying a profile based on the current foreground app.
- Safety Rollback: returning to a safer state after abnormal exits, unstable settings, or manual rollback.

## Implemented

- Foreground app detection through Win32 foreground window monitoring.
- App profile matching by process name, executable name, and window title.
- Auto scheduling that reacts to foreground app changes.
- Power plan switching and restoration through Windows `powercfg`.
- If `powercfg` or a hardware provider is unavailable, capability detection degrades only the affected capability instead of aborting unrelated read-only checks; explicit cancellation remains cancellation.
- Process priority adjustment with rollback support.
- Startup safety, one-click rollback, and exit-time power plan recovery; incomplete configuration-import recovery pauses automatic scheduling and cannot be bypassed by the resume action.
- Focus-loss protection: if the foreground window disappears or process inspection fails, the active scheduling session is restored first; failed recovery continues to block new scheduling.
- Settings write protection: if a bound setting cannot be persisted, its previous valid value is restored and the failure is shown in logs and the status center instead of leaving a half-saved value active.
- Startup recovery also checks pending rollback state when the session marker is missing, and blocks new scheduling until recovery is confirmed.
- If automatic startup rollback is disabled while recovery state is pending, the app stays suspended instead of writing new adjustments over it.
- Failed action results and cancellation paths use an independent rollback token before cancellation is propagated.
- File logging degrades to in-memory diagnostics when its directory or file is unavailable, so logging failures do not block startup, scheduling, or rollback.
- A per-session mutex prevents multiple app instances from sharing and overwriting the same startup and rollback state.
- Rollback state capture, reads, and recovery are serialized per rollback manager, preventing concurrent captures from losing pending entries or concurrent rollbacks from repeating system writes.
- Monitoring start, stop, and manual rollback share a lifecycle gate; stopping first disables foreground-event input and waits for interval, event-driven, and active evaluation work to settle.
- Profile, device-strategy, and settings stores serialize reads, saves, imports, and snapshot operations; direct Profile imports also reject directories and reparse-point paths before reading.
- Direct Profile imports accept only a valid JSON object or array; scalar, `null`, and empty-root payloads are rejected instead of being reported as a successful empty import.
- JSON-based profile editing, import, export, revision history, and rollback.
- Runtime device strategy selection: compatible same-hardware or same-model packs can supplement the local Profile library, with AC/battery-aware selection and local Profiles taking precedence on ID conflicts.
- Device strategy pack activation is explicit: imported community packs start disabled and can be enabled or disabled from the local strategy center.
- Diagnostics for capabilities, device fingerprint, GPU extension readiness, run audits, layered logs, read-only foreground observation, and default-redacted support packages.
- Optional user-installed PresentMon Console capture for average FPS, average frame time, 95th / 99th percentile frame time, and frame sample count, with conflict-aware degradation.
- Windows system hardware observation for system CPU usage, CPU actual frequency, thermal-zone temperature, busiest-GPU-engine utilization, and GPU memory summaries through standard performance counters; thermal-zone and memory values are diagnostic references, not chip-level readings.
- Optional NVIDIA NVML read-only observation for GPU utilization, temperature, power, and graphics clock when the installed driver exposes `nvml.dll`; no GPU settings are modified.
- Resource pressure summary that classifies valid system CPU/GPU usage as low, moderate, high, or unknown; diagnostic-only and never an automatic tuning command.
- Foreground/system load comparison that distinguishes foreground-owned load from possible background contention; diagnostic-only and never an automatic background restriction.
- Conservative resource advisories that suggest reviewing background load or identify foreground-owned load; no automatic restrictions are applied.
- Target-experience foundations: Profiles can store FPS, frame-time, temperature, power, noise, and control-mode preferences; diagnostics use an observation window, stable samples, and hysteresis for read-only state and recommendations, without hardware writes.
- Multi-app arbitration foundations: manual observations inventory background candidates, protection state, classification, and CPU usage; the first sample remains unknown and insufficient evidence never throttles a process.
- GPU safety boundaries: public capability detection, multi-adapter identification, NVIDIA NVML read-only telemetry, and reversible-limit readiness are separated; all GPU writes remain disabled in this build.
- Same-app telemetry comparison and basic trends that match foreground process name, provider, and metric kind without mixing applications or sources, including available before/after deltas for the latest successful scheduling run.
- Read-only Windows per-process GPU usage and dedicated/shared GPU memory counters; memory values are diagnostic only and do not drive scheduling decisions.
- Appearance customization for window, sidebar, and main content backgrounds.
- JSON localization with Simplified Chinese and English.
- Initial community server with versioned read-only health, manifest, application Profile, and device strategy endpoints; the client remains responsible for validation and safe import.

## Planned

- More complete background-app policy handling.
- Separate strategies for battery and plugged-in modes.
- GPU vendor extension detection and device-gated frequency / power options.
- Combining, removing, and repackaging app profiles inside device strategy packs.
- Community sync, downloads, ratings, reproduction success rate, and device compatibility scoring.
- A real target-experience closed loop with cooldown, rate limits, safety envelopes, and stoppable incremental actions.
- Runtime multi-app arbitration, per-process GPU metrics, minimum resource budgets, and multi-instance conflict merging.

## Structure

```text
客户端/
  PerformanceScheduler.sln
  PerformanceScheduler.slnx
  src/
    PerformanceScheduler.App/             # WPF app, views, view models, app settings
    PerformanceScheduler.Core/            # Core models, abstractions, matching, orchestration
    PerformanceScheduler.Infrastructure/  # Win32, powercfg, SQLite, file logging implementations
  tests/
    PerformanceScheduler.Tests/           # xUnit tests
  profiles/                               # Built-in sample app profiles
  community/                              # Client-side sample community catalog
  locales/                                # Localization files
服务端/
  src/SuiXinTiao.Community.Api/           # ASP.NET Core read-only community API
  data/                                   # Development catalog samples
docs/                                     # Design notes
```

## Documentation

- [Project Plan](docs/project-plan.md)
- [Safety Baseline](docs/safety-baseline.md)
- [Documentation Index](docs/README.md)
- [API and Module Contracts (Chinese)](docs/api-contracts.md)
- [Target Experience Control (Chinese)](docs/target-experience.md)
- [Changelog (Chinese)](CHANGELOG.md)
- [Optional PresentMon Integration (Chinese)](docs/presentmon-integration.md)
- [Windows System Hardware Telemetry (Chinese)](docs/windows-system-telemetry.md)
- [NVIDIA NVML Read-only Telemetry (Chinese)](docs/nvidia-nvml-telemetry.md)
- [Resource Pressure Summary (Chinese)](docs/resource-pressure-diagnostics.md)
- [Multi-app Arbitration Boundary (Chinese)](docs/resource-arbitration.md)
- [Hardware Validation Matrix (Chinese)](docs/hardware-validation-matrix.md)
- [Community Server (Chinese)](docs/community-server.md)

## Contributing

- [Contribution Guide](CONTRIBUTING.md)
- [Security Policy](SECURITY.md)
- [Open Source Checklist](docs/open-source-checklist.md)

## Requirements

- Windows 10 / Windows 11
- .NET 10 SDK
- VS Code or Visual Studio

## Run

```powershell
dotnet build .\客户端\PerformanceScheduler.sln
dotnet run --project .\客户端\src\PerformanceScheduler.App\PerformanceScheduler.App.csproj
```

Run tests:

```powershell
dotnet test .\客户端\tests\PerformanceScheduler.Tests\PerformanceScheduler.Tests.csproj
```

Start the community API:

```powershell
dotnet run --project .\服务端\src\SuiXinTiao.Community.Api\SuiXinTiao.Community.Api.csproj --launch-profile SuiXinTiao.Community.Api
```

## Development Notes

This repository keeps source code, sample profiles, localization files, and design docs. Build outputs, runtime logs, local databases, export packages, and private device data should stay out of commits. See [Contribution Guide](CONTRIBUTING.md) and [Open Source Checklist](docs/open-source-checklist.md) for the full project conventions.

## Safety

SuiXinTiao can adjust power plans, process priorities, and limited background efficiency hints, so rollback, explainability, and disable switches are first-class requirements. Unsupported capabilities should be clearly disabled or marked as unavailable in the UI. If other actions in the same Profile are still applied safely, the console and run history show a partial-application warning with the number of unsupported actions. Protected Windows processes and the scheduler itself never receive priority or Efficiency Mode writes. GPU frequency, voltage, and vendor-specific behavior should be added only as device-gated extensions.

Profile imports, community device-strategy imports, and migration imports validate before writing and use file snapshots for every participating store. Community content is treated as untrusted: imported Profiles are disabled by default, and device-strategy entries do not retain their default-enabled state until the user reviews them. If any stage fails or is cancelled, the stores are restored in reverse order. Individual Profile and device-strategy package saves use the same file-snapshot boundary, so a failed replacement restores the previous file and history. If restoration itself fails, the application reports the unresolved stores instead of presenting a partial import as successful, pauses automatic scheduling, and blocks the resume action until the configuration is repaired and manually confirmed in Settings. The active scheduling session is restored before the user is asked to repair the files. Snapshot and normal configuration reads reject reparse points and only process validated top-level JSON files; corrupt or inaccessible configuration files are reported as load issues instead of being silently skipped, and they keep recovery protection active. Profile history also revalidates the complete path immediately before opening a revision, preventing a runtime path redirection; atomic writes validate the destination and parent directory both before creating a temporary file and immediately before final replacement. Corrupt or misplaced UI settings use safe defaults without silently overwriting the original file, and surface a load issue until a successful save replaces it. Manual recovery confirmation also reruns the Profile-library structural safety check, so duplicate Profile IDs or multiple enabled global defaults cannot clear the protection gate.

At startup, even if the session marker is missing, pending rollback state is recovered or blocks new scheduling; disabling automatic startup rollback does not bypass pending state. If startup recovery metadata cannot be saved after scheduling actions have been applied, the app suspends scheduling and attempts rollback with an independent token instead of merely stopping monitoring; a new session is marked active only after its runtime audit record is saved, and a SQLite persistence failure rolls back the just-applied actions and pauses automatic scheduling. Unreadable protection state or unresolved rollback cannot be bypassed by the resume action. If startup failure history cannot be read, the app does not replace the existing marker with a normal session marker; a clean shutdown also refuses to mark that session clean, so the next launch still enters protection. A per-session mutex prevents multiple app instances from sharing and overwriting the same startup and rollback state; file logging or log subscriber failures degrade diagnostics only and do not block startup, scheduling, or rollback.

If a Profile reaches the repeated-unsafe-shutdown threshold but cannot be disabled because its configuration store is unavailable, the app forcibly suspends automatic scheduling and locks the resume path. The protected Profile identity remains in startup recovery state so later launches can retry the disable operation. Disabling the “pause after unsafe exit” option cannot bypass this guard; scheduling resumes only after the store is repaired and the protection state is manually confirmed.

See [Safety Baseline](docs/safety-baseline.md) for the current rollback and process-priority guardrails.

## License

This project is licensed under the Apache License 2.0.

If you redistribute SuiXinTiao or derivative works based on its source code, keep the `LICENSE` and `NOTICE` files and clearly state that the work is based on SuiXinTiao source code.
