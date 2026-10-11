# Hardware Validation Matrix

This matrix records validation results from real Windows devices. It is a release and high-risk-capability checklist; it does not mean that every hardware combination has already been tested.

## Result states

| State | Meaning |
| --- | --- |
| `NotRun` | The scenario has not been executed on the target device. |
| `Passed` | Core behavior, degradation behavior, and rollback all meet the acceptance criteria, with evidence retained. |
| `DegradedPass` | Core scheduling works, but some hardware capability is unavailable and must show an explicit reason. |
| `Blocked` | An unsafe or unverifiable condition was detected; related write capabilities stay disabled. |
| `Failed` | The result does not meet the criteria; logs and reproduction steps must be retained. |

Do not mark a scenario as passed only because a provider loaded, WMI returned a value, or the application did not crash immediately.

## Core matrix

| ID | Scenario | Minimum coverage | Main observations |
| --- | --- | --- | --- |
| HW-01 | Windows platform | Windows 11 x64, once as a standard user and once elevated | Startup, capability detection, foreground tracking, logging, and rollback on exit. |
| HW-02 | CPU platform | At least one Intel and one AMD device | Power-plan changes, advanced processor settings, and restoration. |
| HW-03 | GPU vendors | At least one NVIDIA, AMD, and Intel device | Adapter detection, read-only telemetry, and provider degradation. |
| HW-04 | Hybrid graphics | Integrated + discrete GPU, muxed and hybrid output | GPU metric ownership, multi-adapter aggregation, and no fabricated zeros. |
| HW-05 | Display and graphics stack | Single/multiple monitors, HAGS on/off | GPU Engine counters, PresentMon conflicts, and cancellation cleanup. |
| HW-06 | Power source | Plugged in, battery, and rapid transitions | AC/Battery profile selection, fallback, and reason logging. |
| HW-07 | Foreground lifecycle | Window switches, process exit, PID reuse, temporary unavailability | No stale process state, no PID confusion, and session de-duplication. |
| HW-08 | System lifecycle | Sleep/wake, lock/unlock, reboot, abnormal termination | Startup recovery, pending rollback, and scheduling protection. |
| HW-09 | Load scenarios | Game, browser, build, recording/voice, and multi-app load | Foreground priority, background categories, and evidence-based contention advice. |
| HW-10 | Missing sensors | No thermal zone, no GPU counters, no NVML, insufficient permissions | Independent degradation without fabricated temperature, power, or frequency. |

## Evidence record

Record at least the following for each scenario. Redact device names, user names, window titles, and absolute paths before publishing an issue or diagnostic package.

```text
Validation ID:
Local execution date:
Application version / Git commit:
Windows version and architecture:
CPU:
GPU and driver version:
Memory:
Power source:
HAGS / monitor state:
Privilege level:
Profile / device strategy package:
Expected result:
Actual result:
State: NotRun / Passed / DegradedPass / Blocked / Failed
Log or diagnostic package path:
Reproduction steps (required for failure or degradation):
Rollback verification:
Notes:
```

## Safety acceptance

- The original power plan, advanced processor settings, foreground priority, and background adjustments can be read back and restored.
- Capability failure, device mismatch, insufficient permission, or unreadable rollback state blocks new system writes.
- When GPU apply support is not explicitly declared, only identification and read-only telemetry are allowed; voltage, curve, and overclock writes remain disabled.
- Provider timeout, missing samples, or an occupied PresentMon session never becomes a zero value or an adaptive adjustment trigger.
- Process exit, PID reuse, or temporary foreground unavailability cannot apply an old process policy to a new process.
- Disabling automatic scheduling does not disable static Profiles, diagnostic observation, or rollback.

## Current status

The repository contains unit tests, provider capability detection, and read-only telemetry degradation tests. The real-device matrix still needs to be executed row by row. Do not claim full support for a CPU, GPU, or driver combination without real-device evidence.
