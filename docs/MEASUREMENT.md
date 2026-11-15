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

## Idle baseline: 3 h 51 min, no load

A second condition, and a different one: no game, no benchmark, no synthetic load. Desktop idle, the
three displays at 240 / 120 / 60 Hz, all tweaks applied as shipped. LatencyMon 7.31, all processors.

| Reading | Value |
| :--- | ---: |
| Highest interrupt to process latency | 63.70 µs |
| Average interrupt to process latency | 3.12 µs |
| Highest interrupt to DPC latency | 29.60 µs |
| Highest DPC routine execution time | **239.49 µs**, `amdkmdag.sys` |
| Highest ISR routine execution time | 0.620 µs, `Wdf01000.sys` |
| DPCs of 250 µs or longer | **0 of 2,775,336** |
| Total ISR count | 297 |
| Total time in DPCs | 0.000661 % of all 32 processors |

### What it establishes

**Interrupt routing is working, and this is the first direct evidence of it.** Every one of the 297
interrupts recorded in nearly four hours was serviced on **CPU 18** - the first thread of the core the
plan gives the USB input controller. Processors 0-15, the V-Cache CCD that the plan keeps free of
devices, took **zero** interrupts, and so did 19 through 26. Fourteen of the 32 processors recorded no
interrupt and no DPC at all.

**Long GPU DPCs are isolated events here, not a background.** Nothing reached 250 µs in four hours, and
the single longest routine was `amdkmdag` at 239.49 µs. That matters for diagnosis rather than for
placement: a lone long call is a different problem from a steady stream of them, and moving a lone call
to another core would still cost the same time.

**The measurement instrument is the largest DPC consumer on the machine.** CPU 0 carried 2,724,862 of
the 2,775,336 DPCs - 98.2 % - and its 2.64 s of total DPC time accounts for the 0.000595 % attributed to
`rspLLL64.sys`, LatencyMon's own kernel library. That is the instrument, running at 0.5 ms timer
resolution, not the system under test.

**The GPU peak landed where the plan put it, and the per-driver figures say more than the CPU totals do.**
CPU 16 is the core assigned to the Radeon RX 9070 XT, and `amdkmdag` is the driver whose longest routine
ran there. From the Drivers tab: **7,914 DPCs, longest 239.49 µs, 200.20 ms in total** - an average of
**25.3 µs** per call, so the maximum sits about 9.5 times above the mean of its own distribution. Over
13,865 s that is 0.57 calls per second, which is what a static desktop presenting no new frames looks
like.

### Per-driver figures

Read from the Drivers tab, highest execution first. This is the only place in LatencyMon where a
per-driver duration exists.

| Driver | DPCs | Longest | Total | Average |
| :--- | ---: | ---: | ---: | ---: |
| `amdkmdag.sys` (Radeon) | 7,914 | **239.49 µs** | 200.20 ms | 25.3 µs |
| `ndis.sys` (network) | 349 | 133.73 µs | 1.61 ms | 4.6 µs |
| `ntoskrnl.exe` (kernel) | 19,531 | 84.60 µs | 56.05 ms | 2.9 µs |
| `tcpip.sys` | 212 | 80.05 µs | 2.42 ms | 11.4 µs |
| `Wdf01000.sys` (KMDF) | 297 | 30.19 µs | 1.51 ms | 5.1 µs |
| `rspLLL64.sys` (LatencyMon) | **2,723,956** | 4.53 µs | **2,638.73 ms** | 0.97 µs |

Two things fall out of it.

**A second routing proof, and this one is per driver.** `ndis.sys` recorded its longest DPCs on **CPU 20
(133.73 µs) and CPU 22 (68.20 µs)** - the two processors the plan assigns to the network adapter for
its receive queues. The adapter's DPCs are therefore landing on exactly the cores its interrupt mask
names, which is what the RSS-alignment work exists to achieve. The CPUs tab names the driver behind each
processor's worst DPC, and on the network cores that driver is the network stack.

**The longest DPC exceeded Microsoft's budget.** The app quotes 100 µs per DPC routine as the documented
ceiling, and 239.49 µs is 2.4 times it - once, in nearly four hours. That is not a storm and it is not
dismissable: it is one call that ran long, while the mean of 25.3 µs is what the driver spends the rest
of the time doing.

**The sibling of the GPU's core is not idle.** CPU 17 is the second thread of the same physical core as
CPU 16, and it carried 5,190 DPCs attributed to `ntoskrnl.exe`, peaking at 84.60 µs. The plan pins
devices to a core's first thread only, so nothing the plan controls is running there - but the kernel's
own DPCs are not under the plan's control, and they share that core's execution units with the GPU's
interrupt work. Claim the mechanism the plan actually controls; do not claim a core is quiet.
### What it does not establish

**This is not a repeat of the earlier 803.89 µs reading, and the two must not be compared as before and
after.** That run lasted 2:55 and recorded 13,691 hard pagefaults, about 78 per second; this one recorded
4,231 in 3 h 51 min, about 0.31 per second. Those are different machine conditions - most likely a warm
boot in the first case - so the honest statement is that an idle four-hour run peaked at 239.49 µs, not
that a previously seen spike has been fixed.

**The reported CPU speed is wrong in LatencyMon's own output.** It prints `430 MHz` for both WMI and the
registry; this machine reports `4300` in both. Whether its microsecond conversion uses that figure is not
verifiable from here, and it would scale every duration by ten if it did. The ISR reading argues that it
does not: 0.620 µs is a plausible dispatcher ISR, while a tenth of that, 62 ns, is too fast for a driver
routine carrying real work. The durations are therefore treated as approximately right, and the
discrepancy is recorded here rather than dismissed.

### The same machine under load

For completeness: the author reports gameplay is **equally clean**. No figures were supplied, so it is
recorded here as an observation and not as a measurement; it is not entered in the table above and must
not be quoted as one.

Taken together, the two observations are the point. An earlier session recorded a single
`amdkmdag` DPC of **803.89 µs** over a 2 min 55 s window. That window also logged **13,691 hard
pagefaults - about 78 per second** - against **0.31 per second** in the idle run above, a factor of about
250. A machine reading its own binaries from disk is a machine in startup churn, and a 2:55 sample sits
almost entirely inside it. The parsimonious reading is that the 803.89 µs was a **startup transient**, not
a property of the driver under load - and it is consistent with a power or mode transition rather than a
steady background of long calls.

**The condition neither observation covers is the minutes immediately after boot**, which is precisely where
that transient came from. A single run covering the first ten minutes from a cold start would settle it:
reproducing a DPC above 500 µs there would make the finding specific and explainable, and not reproducing
it would leave the original sample as an unexplained outlier.
### Which hypothesis this rules out

An earlier investigation ranked a compositor-cadence explanation (three refresh rates) against a
mode-set or power-transition explanation, and said the discriminator was whether a long GPU DPC is **one
of many** or **a lone event**. Over four idle hours there were zero DPCs above 250 µs. That is a lone
event, so a steady composition-cadence background is not supported for long spikes. It does not clear
that hypothesis for moderate ones: the run cannot see how many DPCs fell between 100 and 250 µs, because
LatencyMon's buckets begin at 250.

---
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
