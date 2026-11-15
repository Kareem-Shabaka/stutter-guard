# Stutter Guard 1.2.0

*Released 2026-11-15. GPL-3.0-or-later, with commercial licensing on request.*

1.2.0 is about the difference between a number and a claim. Several of the app's own
advisories could not be backed by anything it actually measured, and one of them was
confidently wrong.

---

## Fixed: the driver advisory catalogue could not match most of its own rules

The catalogue holds 77 rules for drivers known to cause latency. Four defects meant most
of them could never fire, and one fired on the wrong device.

| Defect | Measured effect |
| :--- | :--- |
| Rules were INF identifiers (`PCI\VEN_x&DEV_y`) compared with device **instance** ids, which cannot contain a backslash by definition. Only a start-with and a contains fallback kept anything alive. | Nothing matched exactly. Whole-tree reachability was **5 of 77**. |
| AMD rows store the release name and the WDDM build, `"23.9.1 (31.0.21029.1006)"`. A machine reports only `"31.0.21029.1006"`, and 7 version components never equal 4. | **0 of 33** listed AMD variants detected; an affected user was told *"Clean: not in the known regression catalog."* |
| Six rows list several ids separated by `;`. Only the first was ever compared. | **9 ids** unreachable. |
| The scan emitted only `PCI\` nodes. | **22 of 77** rules could not fire on any machine, including one for a node present on the author's own board. |
| The `Contains` fallback searched for a vendor number anywhere in the identifier. | The Realtek **audio**-component rule (`SWC\VEN_10EC&SID_0001`) fired on the Realtek **Ethernet** card (`PCI\VEN_10EC&DEV_8126`). |

Rules are now matched against the hardware ids and compatible ids Windows reports, as an
anchored glob, which is what INF matching is. Microsoft's own guidance is that device
identification strings "should not be parsed ... only for string comparisons", so the
fallbacks are gone rather than tuned.

`DetermineAction` had the same shape of fault: it compared the first number in the
recommended text (`23.12.1`) with the WDDM build the machine reports (`31.0.23013.1006`)
and concluded **"Roll back"** where "Update" is correct - the one instruction that makes
things worse. It now compares only forms with the same component count.

**On this machine:** 5 → 6 of 77 rules reach a real node, none lost. The ceiling is low
because one machine is one vendor's hardware.

---

## New: the app names processes pinned onto interrupt cores

The scheduler has no interrupt-load heuristic - it does not know a core is servicing a
device - so a process restricted to that core competes with every interrupt the plan
isolated. Measured on the author's machine: a ready thread there waited a median of
**782 µs** against **8.9 µs** on a reserved core.

The app now reports which processes carry a restricted mask naming an interrupt core.

Two things it deliberately does not do:

- **Report a full mask.** It intersects every interrupt processor trivially and restricts
  nothing. Counting intersections without checking for that reports the entire machine.
- **Move the processes.** A process's affinity is runtime state that disappears when it
  exits, so no journal entry could make the change reversible and `undo-all.ps1` could not
  put it back. Naming them needs no privilege and cannot mislead.

The interrupt set comes from the devices' **live pins**, not from the plan. The first
version used the plan and reported **104 false overlaps** on this machine, whose live pins
sit on the even processors while its Process Lasso catch-all sits on the odd ones. It is
also not expanded to SMT siblings: this project measured sibling occupancy and dropped
whole-core isolation on the result (p99 19 µs shared against 465 µs isolated). Live, it
now reports **0** here - the same state the earlier audit recorded.

Processes whose masks cannot be read without elevation are counted and disclosed (84
here) rather than folded into a clean zero.

---

## Fixed: defaults that contradicted the evidence

- **Interrupt moderation** was ticked on desktops and unticked on laptops, which is
  backwards: the measured cost is desktop interrupt load (about 88k/s off against about
  6k at Medium). Unticked everywhere, price stated.
- **Flow Control off** was community-graded, carried the cost `"None."`, and was ticked by
  default. Unticked, and the cost is described.
- **`PP_MclkDpmDisabled`** could only confirm its own registry value, so a card stuck at a
  low memory clock looked like a success. The row now says the tick proves the value is
  set and nothing more.

---

## Fixed: two settings that only worked as a pair

`PERFCHECK = 5000 ms` is Microsoft's documented maximum, and it governs "P-state **and
Core Parking** algorithms". It was emitted on paths that leave parking on, deferring a
parking decision by up to five seconds. The 5000 ms value is now emitted only when parking
is actually held off; otherwise the stock 15 ms stands. The row also states which
architecture it commits you to.

## Fixed: receive queues and the interrupt mask described different CPUs

RSS keywords described LPs 20-23 while the mask confined every message to `{20, 22}`, so
queues on 21 and 23 got their DPC on a different CPU from their ISR. The mask, the display
text and `DeviceCpus` now all follow one range.

---

## Also in this release

- The changelog was eight commits behind the shipped 1.1.0 binary. Waves 13 and 14 record
  all of it.
- `PresetTransition` moved out of `SelectionSummary.cs` into its own file; switching a
  preset down now really reverts what it drops.
- **1043 self-test assertions**, 0 failures.

## Known limitations

Unchanged and still true: DPC/ISR **durations** cannot be measured by this app on a machine
without kernel ETW, multi-monitor mixed refresh rates are the most common large-GPU-DPC
cause the app cannot fix, and SMIs are invisible to every lever a user has. The
`amdkmdag` DPC investigation remains open with hypotheses only.