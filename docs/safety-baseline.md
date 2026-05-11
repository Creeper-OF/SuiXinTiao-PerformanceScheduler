# Safety Baseline

SuiXinTiao may change Windows power plans and process priorities. Those actions are useful only when they are reversible, explainable, and bounded by conservative guardrails.

This document describes the first safety baseline used by the scheduler.

## Principles

- Capture state before changing state.
- Keep rollback state durable and atomic.
- Preserve unresolved rollback items after partial failures.
- Do not raise processes to `RealTime` priority.
- Do not adjust protected Windows processes or the scheduler process itself.
- Keep scheduling decisions in Core and platform-specific actions in Infrastructure.

## Scheduling Transaction

The scheduler follows this order when a profile matches the foreground app:

1. Detect capabilities, foreground app, current power source, and active power plan.
2. Match the app against enabled profiles.
3. Capture the original foreground process priority when it would change.
4. Capture original advanced power settings for the target power plan and power source.
5. Capture original priorities for background processes that are eligible for policy changes.
6. Persist rollback state.
7. Apply the power plan, advanced power settings, foreground priority, and background policies.
8. Record the run result for diagnostics.

Dangerous actions should not be added outside this transaction path.

## Rollback State

Rollback state is written with a temporary file and then moved into place. This keeps the previous rollback state intact if the process exits while writing.

When rollback is requested, each item is restored independently:

- Restored items are removed from the pending rollback state.
- Failed items remain in the rollback state for a later retry.
- The rollback file is deleted only when every captured item has been restored.

## Process Priority Safety

Priority changes go through a shared safety policy before execution. The baseline blocks:

- `RealTime` priority.
- Windows system pseudo-processes.
- The scheduler process.
- Known protected Windows processes such as `csrss`, `lsass`, `services`, `wininit`, `winlogon`, `dwm`, and related core processes.

The scheduler reports blocked actions as unsupported instead of treating them as successful changes.

## Extension Rules

Future power, GPU, or background-limiting actions should follow the same pattern:

1. Add a clear Core model for the baseline or action result.
2. Capture the original state before applying the action.
3. Persist rollback state before touching the system.
4. Make the Infrastructure implementation reject unsafe targets by default.
5. Add tests for successful rollback, partial rollback failure, and blocked unsafe requests.
