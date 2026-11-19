# Stutter Guard v1.4.0 - Release Notes

Released from `main` at the commit this tag names. Version stamped `1.4.0` in
`Directory.Build.props`; the `FileVersion` of the binary is `1.4.0.0`.

---

## The theme

**A path that does not act on something says so, and keeps the way back.**

The previous release let you choose, per tab, how much to tune. This one is about the consequence: a path
that deliberately stops writing a setting - the Balanced path, a narrower profile, a CPU swap - used to take
the row with it. The value stayed live on the machine, and the screen offered nothing to undo it. Every
leftover now keeps a revert-only row, in the section it belongs to, with the restart advice it needs, and the
Interrupts page states in words what it left behind.

The same rule applied to the rows themselves: several described a write that was not the write.

---

## What changed

### The Balanced path

| Change | Detail |
| :--- | :--- |
| **Balanced stops pinning** | It builds no device pins, because Windows is free to park the cores a pin would use and a parked core pays a wake-up on every interrupt. A CPU where nothing depends on parking (single-CCD AMD, classic Intel) still applies normally. |
| **Balanced stops writing CPU settings** | `Power.cs` gated only on the profile, so `Balanced + Full` wrote the CPU settings into Windows' own Balanced plan while the row above promised it applied none of them. It now checks the plan target. |
| **The way back is kept** | `LeftoverTweak` is a revert-only row that reports the live value and restores from the journal. Device pins, RSS keywords and CPU power settings each keep one when a path stops building them - and each sits in its original section with its original restart advice, not in a generic pile. |
| **The screen stops promising a plan it will not carry out** | A suppressed plan keeps its planned assignments internally; the Interrupts page no longer lists them, states the reason instead, and still shows every leftover row. |
| **Three notes where they matter** | Leftover settings from an earlier configuration; an active plan that still allows parking (read live, including the class-1 value on hybrid CPUs, with the battery caveat for laptops); and, on a dual-CCD X3D machine with AMD's V-Cache service installed, which mechanism owns CCD placement on the path chosen. |

### Rows that describe what they write

| Change | Detail |
| :--- | :--- |
| **RSS text follows the clamped plan** | A kept pin of two logical processors cannot express the four-processor span an adapter's queues want. The text promised the span and the write produced one processor. The row is now built from the plan that will actually be written, and a narrowed range says it was narrowed and that the pin is untouched. |
| **Every power write is read back** | Windows clamps some settings against what the platform advertises, so a write can succeed and not be the value that took effect. A mismatch now names both numbers and rolls the journal transaction back - the same rule the registry and affinity tweaks already followed. |
| **Task disable is read back** | `schtasks /Change` can report success and leave a policy-managed task enabled. The row fails visibly instead of claiming a disable that did not happen. |
| **One power row, not two** | `power.active` starts with `power.`, so the leftover sweep emitted a duplicate beside the real row on any journal where it had been touched. |
| **The CCD picker is honest about itself** | It decides which cache domain is kept free of device interrupts and which one the exported Process Lasso rules name as the game domain. It does not place game threads: on Balanced that is AMD's V-Cache service plus Xbox Game Bar, on Ultimate it is Process Lasso or your own CPU Sets rule. The panel said the opposite. |
| **Domains are named for what was measured** | The picker is an index over the L3 groups Windows reports, so a Zen 2 part with four domains can reach all four; and a domain is only called a "CCD" where one domain really is one CCD. |

### Power screen

| Change | Detail |
| :--- | :--- |
| **Efficiency class 1** | Windows keeps a class-scoped processor setting per PPM class. With the recommended hybrid device policy this app puts network and audio interrupts on E-cores, which are class 1 - so the class-0 rows could not keep those cores awake or make them prefer performance. Four class-1 counterparts are emitted **only** on a CPU that reports a class above 0, which no Ryzen does. |
| **`PERFBOOSTPOL`** | The ceiling the boost mode works inside. Stock Balanced sets it to 60%, so "Aggressive" there is 60% of aggressive. |
| **AMD power slider** | Offered only when the machine is AMD and the target or active plan actually carries the overlay's GUID. |
| **All three graded `Documented`** | The mechanism is documented; none of them was A/B measured. They are not claimed as measured, and they do not select anything below the tier that includes them. |
| **A measured row you can actually reach** | The service with a measured latency cost behind it (DPS) is offered on the default tier, unticked, under "Latency (measured)" - instead of only being reachable by taking the whole Aggressive tier with its vendor services. |

### Tiers, healing and diagnosis

| Change | Detail |
| :--- | :--- |
| **A tier applies to its own tab** | "Use this preset" collected every page's rows and planned reverts across all of them, undoing costly tweaks on tabs the user had not touched, while the sentence beside it promised one tab. |
| **Background keeps its tier** | One page, three sections that must keep their own module names for the journal and `undo-all.ps1`. Each section had been resetting to the global default on every reload. |
| **Healing finishes what it starts** | A healed NIC property is written to the registry and only the follow-up restarts the adapter. Healing never ran the follow-up, so the change was on disk and not in effect. A failed follow-up is now reported and raises the restart banner. |
| **Triage keeps looking on Balanced** | The "DPC work is landing on game cores" check was disabled on exactly the path most users run, because it read the plan's applicability instead of the cores games use. |
| **Process Lasso: the division of labour, and no priorities** | The generated rules no longer set High priority - Bitsum's own guide warns it can cause stutter - and the advice now matches what the vendor recommends while this app owns the power plan, the interrupt placement and the policy. |

### Privacy and public copy

| Change | Detail |
| :--- | :--- |
| **The "anonymous" report is anonymous** | It printed uncatalogued service paths, task paths, task actions and task authors verbatim, and a task action is routinely `C:\Users\<name>\...`. Redaction is applied to the finished report as one string, so a field added later cannot silently leak, and a guard asserts real paths (`C:\Program Files\...`, `C:\Windows\System32\...`) survive untouched. |
| **The README states instruments, not apologies** | Which measurement moves and why a throughput benchmark cannot; the download link above the fold; figures with their conditions inline; limits stated as properties of the instrument. |
| **The code of conduct is gone** | GPL-3.0 and GitHub both make it optional; `CONTRIBUTING.md` keeps the part that was doing work. |

---

## Measured for this release

| Claim | Method | Result |
| :--- | :--- | :--- |
| Self-tests | `dotnet run --project tests/DpcTweaker.SelfTest -c Release` | **1,438 assertions, 0 failures** (1,246 at v1.3.0; 192 added) |
| Clean builds are byte-identical | `build.cmd --verify-repro` - deletes every `obj/` and `bin/`, publishes twice into two different paths | **65,456,984 bytes**, SHA-256 `4618F79B913F5548244EA6EFFF0DC8CF112187E48CA670EE27BD2025291C6FE0`, identical both times (.NET SDK 10.0.402, win-x64) |
| The shipped file carries the version | `FileVersion` of `publish\StutterGuard.exe` | `1.4.0.0`, `ProductVersion` `1.4.0` |

The release asset's own hash is generated by the build job from the bytes it uploads, and is attached as
`StutterGuard.exe.sha256`. A build on a different SDK patch level, architecture or output path can differ;
verify a download against that file, not against a hash you produced yourself.

---

## What the test project now covers - and what it still does not

The self-test references `DpcTweaker.UI` as well as `DpcTweaker.Core` from this release. The view models are
plain `net10.0` with no WPF types in them, which makes rules that only exist there assertable in a console
process: that a preset switch is scoped to one tab, that a suppressed plan lists no plan lines, that a
Background tier survives a save, a load and a reload, and that a null or re-read options map cannot take a
tab down.

**It still never constructs a window.** XAML, binding resolution and the shell are outside it, so no
assertion in this repository is evidence about what the interface looks like when it runs.

---

## What this release does not fix

- **DPC duration is still not measured by the app.** It reports interrupt *rates* and, where a driver is
  named, a documented cause. The length of an individual DPC needs kernel tracing, which the app does not do.
- **SMIs.** They are named as a remaining cause precisely because no register, mask or power setting reaches
  them.
- **CPU Sets for games.** Placement is still hard affinity. CPU Sets are documented and planned, not shipped.
- **The vendor overlay's slider semantics.** The row exists only where the plan carries the GUID, and it uses
  the same 0-3 scale as Windows' own power slider; that interpretation is the project's reading of the
  vendor overlay, not a per-vendor confirmation of each position.
- **One machine family behind the numbers.** The figures elsewhere in the documentation come from a 9950X3D
  desktop and a hybrid laptop. Nothing here was measured on a third platform.

---

## Upgrading and rolling back

Nothing in the persisted format changed: the journal is still format 2, the per-tab tier map is unchanged, and
no tweak id was renamed. Existing journal entries, `undo-all.ps1` and the recorded originals are unaffected.

A machine that applied pins under an earlier release and now runs the Balanced path will find those pins as
leftover rows rather than planned rows - they are still live, and each has its own Revert. That is the
intended behaviour of this release, not a change of address.

Full detail of every change is in the commit history; this file states what the release does.

**License:** GPL-3.0. See [LICENSE](../../LICENSE) and [COPYRIGHT](../../COPYRIGHT).