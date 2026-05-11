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
- Process priority adjustment with rollback support.
- Startup safety, one-click rollback, and exit-time power plan recovery.
- JSON-based profile editing, import, export, revision history, and rollback.
- Device strategy pack structure for future model-specific strategy bundles.
- Diagnostics for capabilities, device fingerprint, GPU extension readiness, storage, and logs.
- Appearance customization for window, sidebar, and main content backgrounds.
- JSON localization with Simplified Chinese and English.

## Planned

- More complete background-app policy handling.
- Separate strategies for battery and plugged-in modes.
- GPU vendor extension detection and device-gated frequency / power options.
- Combining, removing, and repackaging app profiles inside device strategy packs.
- Community sync, downloads, ratings, reproduction success rate, and device compatibility scoring.
- Target-experience options such as target FPS, temperature limit, noise preference, power saving, and stability preference.

## Structure

```text
PerformanceScheduler.sln
PerformanceScheduler.slnx
src/
  PerformanceScheduler.App/             # WPF app, views, view models, app settings
  PerformanceScheduler.Core/            # Core models, abstractions, matching, orchestration
  PerformanceScheduler.Infrastructure/  # Win32, powercfg, SQLite, file logging implementations
tests/
  PerformanceScheduler.Tests/           # xUnit tests
profiles/                               # Built-in sample app profiles
community/                              # Sample community catalog and device strategy data
locales/                                # Localization files
docs/                                   # Design notes
```

## Requirements

- Windows 10 / Windows 11
- .NET 10 SDK
- VS Code or Visual Studio

## Run

```powershell
dotnet build PerformanceScheduler.sln
dotnet run --project .\src\PerformanceScheduler.App\PerformanceScheduler.App.csproj
```

Run tests:

```powershell
dotnet test .\tests\PerformanceScheduler.Tests\PerformanceScheduler.Tests.csproj
```

## Do Not Commit

Do not upload local runtime data, build outputs, or private machine data to a public repository. These files are normally generated again when another developer builds or runs the app.

- `bin/`, `obj/`, `artifacts/`, `publish/`.
- `runtime/`, `logs/`, `appearance/`.
- `*.db`, `*.sqlite`, `*.sqlite3`, `*.log`, `*.dmp`.
- `.env`, certificates, secrets, publishing profiles, private screenshots, or machine-identifying data.
- Personal migration packages or hardware-specific bundles unless intentionally sanitized.

Source code, tests, sample profiles, sample community catalogs, localization files, docs, solution files, README files, and a license file are normally safe to publish.

## Safety

SuiXinTiao can adjust power plans and process priorities, so rollback, explainability, and disable switches are first-class requirements. Unsupported capabilities should be clearly disabled or marked as unavailable in the UI. GPU frequency, voltage, and vendor-specific behavior should be added only as device-gated extensions.

See [Safety Baseline](docs/safety-baseline.md) for the current rollback and process-priority guardrails.

## License

This project is licensed under the Apache License 2.0.

If you redistribute SuiXinTiao or derivative works based on its source code, keep the `LICENSE` and `NOTICE` files and clearly state that the work is based on SuiXinTiao source code.
