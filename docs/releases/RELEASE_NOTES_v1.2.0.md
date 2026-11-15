# Stutter Guard 1.2.0

*Released 2026-11-15. GPL-3.0-or-later, with commercial licensing on request.*

---

## What's new

### Driver advisories cover every device on the machine

The advisor now compares each rule against the identifiers the Plug and Play manager itself
matches on: a device's **hardware ids**, its **compatible ids**, and its instance id. That is
the same test Windows performs when it chooses a driver, and it means a rule is recognised on
the hardware it actually describes.

Every present device node is scanned, so the catalogue's coverage now runs across **PCI, USB,
HDAUDIO, ACPI, ROOT and Software Component** devices - the last of which is where audio
processing components live. Advisories for USB audio interfaces, HD Audio codecs and vendor
root drivers surface alongside graphics cards and network adapters.

Rules that list several identifiers in one row are matched against every one of them.

### AMD driver versions on both forms

AMD advisories record the release name together with the WDDM build - `23.9.1
(31.0.21029.1006)` - while a system reports only the WDDM build. Both forms are compared, so a
listed AMD regression is detected on a real machine. The update-or-rollback advice compares
like with like, so an out-of-date driver is told to update, and a newer one is told where to
roll back to.

### Processes competing with interrupts are named

The Windows scheduler has no interrupt-load heuristic: it does not know a processor is
servicing a device, so ordinary work placed there competes with every interrupt on it. On the
development machine a ready thread on an interrupt processor waited a median of **782 µs**,
against **8.9 µs** on a processor reserved for the game.

The Diagnostics report now lists the processes whose affinity mask names a processor the
interrupt pins occupy, with the process name and the specific CPUs. A mask that covers every
processor is never reported, because it restricts nothing.

### DPC findings against Microsoft's published budgets

A "large DPC" is now stated against Microsoft's documented budget of **100 µs for one DPC
routine** and **25 µs for one ISR**, so a reader can see how far over budget a driver is
rather than whether it looks large. The one cause no register, mask or power setting can
reach - a System Management Interrupt, which firmware raises directly to the processor - is
named as the remaining candidate when DPC times survive a clean install.

### Receive queues and interrupt masks describe the same CPUs

The RSS keyword range, the device affinity mask and the text shown on screen are all derived
from one span, so a receive queue's DPC lands on the same processor as its interrupt. A core
count guard keeps the adapter's request within the processors available to it.

### Presets are a state, not a filter

Choosing a tier and pressing **Apply this preset** makes the machine match that tier: it
applies what the tier includes and puts back what it leaves out, so going from Everything
down to Safe really undoes the costly tweaks. The confirmation states both counts first, and
a restore point is taken before either step runs. **Apply selected** remains the narrower
control that only applies the ticked rows.

### Reproducible release builds

Two builds of one commit, from different directories, now produce the same SHA-256. You can
check this yourself before publishing, from a clean state:

```
build.cmd --verify-repro
```

That is what makes a published checksum meaningful: you can rebuild the file and compare it
against the one you downloaded.

### Defaults that follow the measured benefit

Interrupt moderation and Flow Control are enabled only where the evidence supports the cost,
and each row states its own price rather than `None.`. The tooltip on every preset button
describes what that button does.

---

## Fixes

| Area | Now |
| :--- | :--- |
| **Interrupt mask and RSS width** | Derived from one span, so queues and interrupts describe the same processors. |
| **Device core capacity** | A second core is granted only when the budget can actually spare a pair after the GPU, input and audio devices, so a plan never promises a core it cannot deliver. |
| **Processor check interval** | Emitted at Microsoft's maximum only when core parking is held off; otherwise the stock interval stands, so a parking decision is never deferred by five seconds. |
| **`PP_MclkDpmDisabled`** | States that the tick proves the registry value is set, and no more. |
| **Power architecture** | Each plan row states what it commits you to: parking off means placement is yours; parking on means Windows and the vendor driver keep placing the game. |
| **Driver version normalisation** | NVIDIA's marketing encoding is applied to display drivers only, so Intel Arc and AMD version strings are compared as the vendor reports them. |
| **Version matching** | Compares whole components, so a rule for `1.18` cannot match `.3118`. |
| **Advisory precedence** | The most severe advisory wins when a device matches several catalogue entries. |
| **Preset transition** | Lives in its own file with its own tests rather than alongside unrelated summary types. |

---

## Known limitations

| Limitation | Detail |
| :--- | :--- |
| **Measurement scope** | The 782 µs to 8.9 µs pair was measured on one machine under held synthetic load, with a control core. It is a wake-latency measurement, not an idle-desktop figure: LatencyMon on the same machine reports a highest interrupt-to-process of about 320 µs, which measures a different leg of the same path. |
| **DPC and ISR durations** | This app reports interrupt and DPC **rates** per processor. Handler **durations** need kernel ETW, which it does not µse, so it names the possibility rather than guessing at a duration. |
| **RSS placement** | NDIS spreads receive queues over a **contiguous** processor range. The app sizes the queue count to fill that span; it cannot make queues skip a processor. |
| **Verification is simulation-based** | The topology matrix, drift engine and write-ahead log are tested against synthetic topologies and a throwaway registry key, not against every shipping CPU. |
| **Restarts** | Interrupt affinity, MSI mode and GPU registry keys are read by drivers at device start. The app flags a restart, and the interrupt check reports whether the pins landed. |
| **Idle power** | Restricting cores to C1 raises idle package power by roughly 15-20 W on a desktop. This is stated on the Power screen rather than hidden. |

---

## Verification

- **1043 self-test assertions**, 0 failures.
- 28 synthetic CPU topologies, including dual-CCD 3D V-Cache, Intel hybrid, and interleaved
  P/E core numbering.
- Write-ahead-log durability and rollback, a drift-heal-revert proof against a throwaway
  registry key, and hostile-input fuzzing.
- `build.cmd --verify-repro` for the release binary.
