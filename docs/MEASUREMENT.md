# Measurement method

The numbers in the top-level README come from one controlled experiment. This file states how it was
run, what it measures, and what it cannot measure, so that the result can be judged rather than
believed.

---

## What is being measured

Windows wakes a thread and the thread runs later than requested. That delay is **scheduler wake
latency**, and it is the leg this experiment isolates.

It is not the same quantity as a DPC's *duration*. A driver routine that runs for 800 µs and a thread
that waits 800 µs for a processor can produce similar-looking numbers in different tools and have
nothing to do with each other. Conflating the two is the most common mistake in this area, so this
project keeps them apart in its own output too.

## Instruments

Built for this purpose and kept in `tools/`, so the measurement can be repeated rather than taken on
trust:

| File | Role |
| :--- | :--- |
| `WakeProbe.cs` | A high-resolution waitable timer with a 1 ms period, on a thread pinned to one logical processor. Each observed interval minus the nominal 1000 µs is lateness. |
| `SpinLoad.cs` | A controlled 100 %-duty-cycle user-mode load, pinned to an explicit CPU mask, so that load can be **held constant** while only its placement varies. |
| `sg-latency-probe.ps1` | Per-processor percentile table: median, P90, P99, P99.9, max. |
| `sg-ab-equal-load.ps1` | The A/B driver: rotates arm order between rounds to defeat drift, and verifies placement by per-processor utilisation. |

## What the environment cannot do

Established by attempting it, not assumed:

- **Kernel ETW produces no trace in this environment.** `xperf -on PROC_THREAD+DPC+INTERRUPT` and
  `wpr -start CPU` both failed to produce an ETL file. Therefore DPC and ISR **handler durations** are
  not measurable here. Only per-processor `% DPC Time` and `% Interrupt Time` aggregates are reachable.
- **LatencyMon has no CLI and no settings file**, so its headline "highest measured interrupt to process
  latency" cannot be read headlessly. This is why the experiment measures the mechanism directly rather
  than reading a vendor aggregate.
- **The experiment ran unelevated.** Anything needing administrator rights was deferred to the app,
  which runs elevated.

## The controlled experiment

Load held at **7 threads in every loaded arm**; only placement varies. Two rounds, with arm order
rotated per round. **2,400 wake-ups per cell.** Placement verified by per-processor utilisation:
in the IRQ-loaded arm, processor 20 read 100 % while processor 2 read 1.6 %; in the other arm those
figures reversed.

| Arm | Probe 20 | Probe 26 | Probe 16 (control, never loaded) |
| :--- | :--- | :--- | :--- |
| idle | median 8.9, p99 486.7 | median 8.6, p99 471 | median 8.7, p99 513.5 |
| **load on non-interrupt processors** | **median 8.2**, p99 319.4 | **median 8.8**, p99 347.6 | median 8.1, p99 314.2 |
| **load on interrupt processors** | **median 782.4**, p99 823.3 | **median 784.5**, p99 829.3 | **median 10.7**, p99 509.7 |

All figures in microseconds. Hardware: Ryzen 9 9950X3D, 16 cores / 32 logical processors, SMT on.

### What this establishes

1. **Placing ordinary load on the processors that service interrupts costs about 780 µs of median wake
   latency** - 8.9 to 782.4, roughly 90 times.
2. **The control processor does not move** (10.7 against 8.7 at idle), so the effect is specific to the
   processors that were loaded, not a system-wide slowdown. The control is what makes the result
   trustworthy rather than an artifact of a busy machine.
3. **Load on non-interrupt processors costs nothing measurable** - p99 was slightly better than idle,
   which is consistent with keeping clocks and power states up.

## Limitations, stated as properties of the instrument

- **Synthetic load is 100 % duty cycle.** Real background work is bursty, so the real-world magnitude is
  smaller than 780 µs. This experiment bounds the mechanism, not the user's experience.
- **Wake latency only.** Handler execution cannot be measured without kernel ETW, as above.
- **One machine, one afternoon.** n = 2,400 wake-ups per cell across two rounds. The mechanism is
  documented by Microsoft; the magnitude is not independently reproduced.
- **A discarded first design.** The first A/B varied **load as well as placement** - 24 threads against
  14 - which would have measured the difference in thread count, not the difference in placement. It was
  replaced with the equal-load design above, and the control processor was added for the same reason:
  to make it impossible to mistake a general slowdown for a locality effect.
- **SMT siblings are not isolated.** A separate probe found that loading a core's second thread did not
  degrade wake latency (p99 19.1 µs against 464.9 idle), which is why the plan pins devices to a core's
  first thread only and does not claim whole-core exclusivity. Handler-duration effects of sibling
  contention remain unmeasured.

## Corroboration in real conditions

The controlled experiment is the evidence. These counter readings are the real-world sanity check, taken
before and after clearing 70 processes off processors reserved for interrupts:

| Metric | Before | After |
| :--- | :--- | :--- |
| Processes on a restricted mask | 70 at `0xFFFC0000` | 4 |
| Processor 26 (audio) utilisation | 33.77 % | 6.13 % |
| Processor 20 (receive queues) DPC time | 2.39 % peak 12.49 % | 0.00 % |
| Processor 22 (receive queues) DPC time | 0.94 % peak 7.81 % | 0.00 % |

Real-conditions wake latency after that change was **noisy and is not reported as a before/after**:
instantaneous activity differed between runs. The controlled experiment is the rigorous number; these
counters show that the placement actually changed.

## Reproducing it

```powershell
tools\sg-latency-probe.ps1 -Cpus 16,20,22,26,27 -Samples 400 -Repeats 3
tools\sg-ab-equal-load.ps1 -Samples 400 -Repeats 3 -Rounds 2
```

Results land as CSV in `tools\ab-results\`. Run them with the interrupt pins as they are, then with
devices moved, and compare medians - not maxima, which are dominated by unrelated outliers.

## Why medians

Rule 7 of Hoefler and Belli's guidelines for reporting performance measurements: decide deliberately
whether a measure of central tendency is appropriate, because worst-case problems may need another
percentile. Wake latency is dominated by a long tail that moves with unrelated machine activity, so the
median is the stable statistic and p99 is reported beside it to show the tail. A single maximum is never
used as a headline figure, because one outlier is not a property of the plan.
