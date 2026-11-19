# Contributing

## Build and test

Needs the .NET 10 SDK on Windows x64.

```powershell
dotnet build -c Release
dotnet run --project tests/DpcTweaker.SelfTest -c Release   # must print "0 failed"
build.cmd                                                    # publish\StutterGuard.exe
```

The self-test executable has no third-party dependencies by design, so it runs anywhere the SDK does.
A pull request that changes behaviour without an assertion covering it is likely to be asked for one.

Note what the test project does **not** cover: it references `DpcTweaker.Core` and `DpcTweaker.UI` - the
view models are plain `net10.0` so their rules can be asserted headlessly - but it never constructs a
window. XAML, binding resolution and the shell are not exercised by it, so a green run says nothing about
the interface. If you change the UI, a build is the minimum and a screenshot is better.

## The contract every tweak has to satisfy

This project's central claim is that it ships no placebo. The compiler enforces part of that: a new
tweak declares these fields or it does not build.

| Field | Allowed values | Meaning |
| :--- | :--- | :--- |
| `Evidence` | `Measured`, `Documented`, `Anecdotal` | What backs the claim. `Measured` means you measured it and can say how. `Documented` means a primary source describes the mechanism - Microsoft Learn, the WDK, Windows Internals, vendor documentation. `Anecdotal` means community reports only. |
| `CostClass` | `Free`, `Cheap`, `Expensive`, `Unclassified` | What the user gives up. `Unclassified` exists so an unfinished row is unselectable rather than silently safe. |
| `Risk` | `Low`, `Medium`, `High` | What can break. |
| `What` | text | The exact change, including the registry path or command. |
| `Why` | text | The mechanism, not a benefit claim. |
| `Cost` | text | What the user visibly loses, in their terms. |
| `CanBreak` | text | Failure modes. If you cannot name one, you have not finished thinking about it. |
| `NeedsReboot` | bool | Whether the change is inert until restart. |
| `Revert` | code | How the previous value is restored. Must work from the journal alone. |

Rules that follow from the project's invariants:

1. **No `Anecdotal` tweak may be selected by `Essential`, `Safe` or `Costly`.** Only `Everything`
   reaches those rows, and the user has to tick them.
2. **No `Unclassified` tweak is selectable at all.**
3. **A source of truth must exist for `Documented`.** Put it in a comment or the `Why` text. "Everyone
   knows" is not a source. Reddit, YouTube and content farms locate a question; they never settle one.
4. **A magnitude needs an instrument.** Do not write "a large DPC" or "milliseconds of jitter" without
   saying what measured it and under what conditions. Microsoft's published budgets are citable: 100 µs
   per DPC routine, 25 µs per ISR.
5. **Every state change is journalled before it runs.** A tweak that cannot be expressed as "record the
   old value, then write the new one" does not belong in the catalog. Process affinity, for example, is
   runtime state that vanishes at shutdown, so this app reports such overlaps instead of setting masks.

## Never

- **Do not touch kernel memory, patch a binary, or alter services or drivers used by anti-cheat.**
  Vanguard (`vgk.sys`), Ricochet, Easy Anti-Cheat, BattlEye, Xbox Game Pass and core Windows security
  components are out of scope permanently.
- **Do not force MSI mode or custom IRQ affinity onto NVMe drives or PCIe root bridges.** A wrong value
  there can stop the machine starting.
- **Do not pin a device to CPU 0 or to its SMT sibling.** On an SMT CPU those are one core, and that
  core belongs to the system.
- **Do not bypass `undo-all.ps1`.** If a change cannot be reverted from the generated script alone, the
  script is wrong or the tweak is.

## Style

British spelling, as the rest of the repository uses. Comments explain *why*, not what the next line
does. Public copy - README, release notes, UI strings - states what the product does; it never narrates
what an earlier version got wrong. Internal records (`AUDIT_CHANGELOG.md`, commit messages) stay blunt.

## Submitting

- One logical change per commit, with a message that says what the change does and why.
- Say in the pull request which assertions prove it, or why none can.
- If it touches the UI, say whether you ran it. "It compiles" is not the same claim.
