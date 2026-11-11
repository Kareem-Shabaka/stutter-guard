# Stutter Guard v1.0.0

**Hardware-aware latency tuning for Windows 10 / 11 (x64) that you can actually undo.**

Average FPS measures throughput on a static scene. **1% lows, frame pacing and DPC/ISR latency**
decide what happens during an aim flick or a firefight. Stutter Guard targets those, using documented
Windows APIs only - no kernel patching, no binary modification, nothing that a game's anti-cheat has
to care about.

---

## Executive summary

| Capability | What it does |
| :--- | :--- |
| **Interrupt affinity** | Pins each busy device's interrupts to its own core and keeps the cores your game renders on completely interrupt-free: CPU 0/1, the 3D V-Cache CCD on AMD X3D, the top-ranked CCD or the P-cores on Intel. |
| **Core benchmark** | A ~2-minute, read-only run that measures wake-up jitter, workload speed and existing interrupt load per physical core, then ranks the cores that take devices. |
| **NIC PHY sleep stripping** | Turns off Energy-Efficient Ethernet (802.3az), Green Ethernet, Gigabit Lite, Realtek ASPM/CLKREQ and the other vendor link power-saving keywords, then restarts the adapter. |
| **0.5 ms timer agent** | A 6.6 KB logon helper that holds the system timer at 0.5 ms and *verifies the resolution Windows actually granted* instead of assuming it. |
| **Service & task presets** | **475** Windows services and **271** scheduled tasks with plain-language impact text. Presets only ever lower start types; critical OS services and anti-cheat engines are locked and can never be changed. |
| **AMD GPU tuning** | Driver registry keys for power gating, deep sleep, ULPS and display idle behaviour, filtered against the keys your installed driver binary actually reads. |
| **Drift Watcher & auto-heal** | Detects when a driver or Windows update reset applied tweaks, tells you which subsystem was hit, and puts them back in one click. |
| **100% reversibility** | Every change is recorded *before* it is made, and an `undo-all.ps1` that works in Safe Mode is kept in lockstep with the journal. |

---

## What's new & hardened in v1.0.0

### Write-ahead-log journaling
The journal is now a real WAL. Each tweak's before-state is flushed to `journal.json` **before** the
system is touched, and `undo-all.ps1` is regenerated in the same step, so a crash mid-apply can never
leave a machine changed with no record of its previous state. Writing the journal and the undo script
is atomic (`.tmp` + rename), a failed write inside a transaction **aborts the tweak**, and a journal
that fails to parse latches into a protection mode that refuses to overwrite either file.

### Drift Watcher & 1-click auto-heal
A driver install (DDU, Adrenalin, a Windows cumulative update) wipes `Interrupt Management`,
`MessageSignaledInterruptProperties` and the GPU class keys - silently putting every interrupt back on
CPU 0. v1.0.0 fingerprints every device's driver identity when tweaks are written, re-scans on startup,
and when drift is found shows:

> **Driver Update Detected - N applied tweaks were reset to stock**

with the affected subsystems (`Interrupts / MSI (4), GPU driver keys (2), Network adapter (1)`) and a
**Re-Apply Now** button. Re-applying preserves the original factory baseline, so Revert still restores
the pre-Stutter-Guard state. Attribution is evidence-based: the app only names a driver update when a
driver version actually changed.

### SMT-aware NDIS RSS alignment
`*MaxRssProcessors` counts *logical* processors, not cores. The old code wrote "2 processors starting
at 20", which covers LPs 20 and 21 - the two threads of **one** core. RSS spans are now computed from
real topology and the queue count is sized to fill the span, so receive queues land on distinct
physical cores.

### Configurable hybrid P/E placement
On Intel hybrid CPUs, non-game devices default to E-cores so every spare P-core stays free for the
game - but that used to override the benchmark silently. There are now three policies, selectable on
the Interrupts screen: **E-cores for audio/network** (default), **P-cores when there is room**, and
**let the benchmark decide**. Every hybrid row also states which kind of core it landed on and why.

### Shared xHCI root-hub escalation
A root hub raises one interrupt for every port on it, so a mouse and a USB DAC on the same controller
already share an ISR. That controller now gets a second core when cores are spare - with a budget guard
that never pushes another device into sharing - and the plan tells you the real fix is moving the DAC
to a different controller.

### 28-architecture verification matrix
Every planner decision is asserted against 28 synthetic topologies: AMD 3D V-Cache (single and dual
CCD, asymmetric 6+6, SMT off), standard multi-CCD and quad-CCX parts, Strix Point, Intel 12th-14th gen
hybrids, Arrow / Meteor / Lunar Lake, homogeneous quad-cores, 2-core VMs, and 64-LP / 128-TP
Threadrippers. Three architectural traps were found and fixed this way:

- **Strix Point false X3D** - 16 MB + 8 MB satisfied the old ratio test and was mislabelled 3D V-Cache. V-Cache now requires >= 64 MB.
- **Intel LP-E island cores** - Meteor/Lunar Lake E-cores sit outside the ring bus with no L3. They are now excluded from device placement whenever L3-backed cores exist.
- **Interleaved P/E numbering** - verified to work with Arrow Lake's P0-P1 / E2-E17 / P18-P23 layout.

### DriverAuditor multi-vendor fixes
- Intel Arc `32.0.101.x` and AMD `32.0.x` were being mangled into NVIDIA-style version numbers. Translation now requires NVIDIA's `14`/`15` third component.
- `nvraid.sys` / `nvstor.sys` (storage drivers that start with "nv") no longer get display-driver version translation.
- Version matching compares whole components, so a rule for `1.18` can no longer match `.3118`.
- When a device matches several catalog entries, the most severe advisory wins instead of the first one in file order.

---

## Verification & safety guarantees

### Test suite
``````
dotnet build -c Release   ->  0 Warning(s), 0 Error(s)
--selftest                ->  828 passed, 0 failed
``````

Coverage includes: WAL durability and rollback, degraded-journal protection, drift detection and
auto-heal (against a real throwaway registry key), SMT-aware RSS, the 28-topology matrix, all 77
driver-catalog rules, hostile-input fuzzing and the timer agent's exit codes.

### Reversibility
- Every change is recorded in `C:\ProgramData\Stutter Guard\journal.json` **before** it happens.
- Original registry keys are exported to `C:\ProgramData\Stutter Guard\Backups\<timestamp>\` as `.reg`, power plans as `.pow`.
- `C:\ProgramData\Stutter Guard\undo-all.ps1` restores everything **without the app**, and works in Safe Mode.

**Emergency recovery, if Windows will not start normally:**
1. Boot into **Safe Mode** (hold Shift while clicking Restart, or `msconfig` -> Boot -> Safe boot).
2. Open PowerShell **as administrator**.
3. Run: `& "C:\ProgramData\Stutter Guard\undo-all.ps1"`
4. Restart normally.

If the app itself is unusable, the same script is the recovery path - it is plain PowerShell and needs
nothing installed.

### Release binary

| File | Size | SHA-256 |
| :--- | ---: | :--- |
| `StutterGuard.exe` | 65,427,869 bytes | `77FFCDA8EABF8E01FE3D9C95591E78CB341C0D0D9D57ADDF4DBF736F87321236` |
| `DpcTimerRes.exe` | 6,656 bytes | `44781A8F7DD72065201C7600C24D660562E07FA770A41321B1CDF1D28A3FBCEE` |

The published `StutterGuard.exe` is self-contained, requires the UAC **administrator** manifest it
ships with, and reports `ProductVersion 1.0.0` / `FileVersion 1.0.0.0`.

Verify with:
``````powershell
Get-FileHash .\StutterGuard.exe -Algorithm SHA256
``````

---

## Requirements

- Windows 10 or Windows 11, x64.
- Administrator rights (the app requests elevation on launch).
- .NET 10 runtime is **not** required - the release build is self-contained.

## Honest limitations

- **RSS placement is bounded by what NDIS keywords can express.** NDIS spreads queues over a *contiguous* processor range, so the app sizes the queue count to fill that span. It cannot make queues skip a processor.
- **22 of the 77 driver-catalog rules target non-PCI devices** (USB, HDAUDIO, ACPI, ROOT, SWC nodes). The auditor handles them correctly when given such a device, but the device scanner currently only enumerates PCI devices, so those rules will not surface in a real scan yet.
- **The telemetry endpoints are embedded in the binary.** Hardware reports and feedback go to the developer's channels; the webhook URLs are obfuscated but not secret, so they can be extracted from the binary. Submitting is always explicit and opt-in.
- **The timer agent ships as a managed build** (6.6 KB) so it can be rebuilt with the in-box `csc.exe`. `timerres.c` is kept as the reference native implementation for anyone with MinGW-w64.

---

## Licence

GPL-3.0 with a Non-Commercial Rider. See `LICENSE`.
