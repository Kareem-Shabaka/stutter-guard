# Architecture

Four projects, one direction of dependency: `App` -> `UI` -> `Core`. Nothing in `Core` references WPF,
which is what lets the self-test executable run the engine headlessly.

```text
src/DpcTweaker.Core/     no UI. The engine, the journal, the catalog, the interrupt planner.
  Engine.cs              apply / revert / heal, one transaction per tweak
  Infra/Journal.cs       write-ahead record of every original value
  Infra/JournalRestorer  generates undo-all.ps1 from the journal, never from memory
  Infra/BackupSet.cs     timestamped .reg / .pow / device-CSV copies
  Infra/RegistryOps.cs   read and write, with every value kind round-tripping
  Infra/TaskSched.cs     schtasks wrappers; boot and logon task XML
  Interrupts/AffinityPlanner.cs   which core each device gets, and why
  Interrupts/CoreBenchmark.cs     per-core wake jitter and speed
  Interrupts/InterruptCheck.cs    post-restart verification by per-CPU counters
  Interrupts/ProcessOverlap.cs    names processes sitting on an interrupt core
  Tweaks/*.cs            the catalog; each tweak declares evidence, cost, risk and revert
  Detection/*.cs         topology, PCI and PnP nodes, displays, services, scheduled tasks
  Catalog/DriverAuditor  driver version comparison against known-bad releases
  Diagnostics/LatencyTriage.cs    findings, each stated against a budget with its cost
src/DpcTweaker.UI/       view models, page composition, row behaviour. No XAML.
src/DpcTweaker.App/      WPF shell: one window, one template per page type, styles.
tests/DpcTweaker.SelfTest/  dependency-free executable, 1,246 assertions
tools/                   the measurement instruments behind the published numbers
docs/                    this file, the measurement method, release notes
```

## The transaction

Every state change follows one path, and this is the reason the project can claim reversibility:

1. `Journal.RecordOriginal` writes the **current** value and flushes to disk. The flush happens before
   the mutation, so a power cut cannot leave a change without its record.
2. The mutation runs. If it throws, `RollbackTransaction` removes exactly what the transaction added, so
   a failed tweak leaves no phantom entry in the undo script.
3. `SidecarWriter` regenerates `undo-all.ps1` on every flush, which is why the script is never stale.

A tweak that cannot be expressed as "record the old value, then write the new one" does not belong in
the catalog. That single constraint is why process affinity is *reported* rather than set: a mask is
runtime state that vanishes at shutdown, so there would be nothing for the journal to restore.

## Why the journal is versioned

`Journal.Version` is currently 2. Enums are stored **by name**, not by ordinal, so adding a value to an
enum cannot silently reinterpret a stored one. Migration is in memory on load and additive; the recorded
originals are never rewritten by it. A file with no version field reads as version 0, which is what makes
the migration run at all.

## Why the generated script matters

`undo-all.ps1` is generated from the journal, not written by hand, and it is the project's actual
reversibility guarantee: it works in Safe Mode, with no app, no installer and no network. If a change
cannot be expressed in that script, it should not ship - and that is a rule in
[CONTRIBUTING.md](../CONTRIBUTING.md), not only a preference.

## Testing

`tests/DpcTweaker.SelfTest` is a console executable with no third-party dependencies. It asserts engine
behaviour, the interrupt planner's invariants (a catch-all may never intersect the processors devices
hold), registry round-tripping for every value kind including `REG_MULTI_SZ` elements that contain
newlines, the preset ladder and its two absolute rules, and hostile-input handling.

It references `DpcTweaker.Core` only. **It does not exercise the interface** - so a green run is evidence
about the engine, never about the window.
