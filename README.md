# Stutter Guard

[![License: GPL-3.0 (Non-Commercial)](https://img.shields.io/badge/License-GPL--3.0%20(Non--Commercial)-blue.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0%20(LTS)-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-0078d4.svg)](https://microsoft.com/windows)
[![Version](https://img.shields.io/badge/Version-0.7.1-brightgreen.svg)](https://github.com/Kareem-Shabaka/stutter-guard/releases)
[![Sponsor](https://img.shields.io/badge/Sponsor-%E2%99%A5-ea4aaa.svg)](https://github.com/sponsors/Kareem-Shabaka)

> **"Why does a high-end gaming PC running at 200+ average FPS still hitch, micro-stutter, or drop audio?"**  
> Average FPS measures raw compute throughput when scenes are static. **1% Lows, frame pacing, and DPC kernel latency** dictate what happens during an aim flick, a rapid turn, or an intense multiplayer firefight. Stutter Guard is built from the ground up to eliminate micro-stutters and lock in real-time responsiveness.

Every optimization originates from verifiable latency profiling (e.g., AMD Ryzen 9 9950X3D + Radeon RX 9070 XT: worst LatencyMon interrupt-to-process drop from **733 µs → 11.6 µs**). Zero snake oil. Zero black-box registry scripts. Every tweak in the application documents **what it does**, **why it works**, **what it costs**, **failure modes**, and **the empirical evidence**.

---

## 🚀 The v0.7.1 Driver Latency Advisory Engine

### The Challenge: Infinite Hardware Combinations & Bugged Drivers
Modern gaming PCs represent millions of possible hardware combinations: CPUs, graphics cards, motherboards, network adapters, audio interfaces, and high-polling USB peripherals. Every single component runs its own vendor device driver.

Often, sudden micro-stuttering isn't caused by weak hardware—it is triggered by a **single bugged driver version** silently stalling the Windows kernel. For instance, a Wi-Fi card driver polling for nearby networks mid-game, or an Ethernet driver sleeping and waking mid-packet, can stall a CPU core for 3,000 to 8,000 microseconds. Traditional benchmarks like Cinebench or 3DMark Time Spy never expose this because they only measure raw compute power under static load.

### The Solution: Turning Internet-Wide Troubleshooting into an Instant Fix
Instead of requiring gamers to spend weeks scouring obscure hardware forums, Reddit troubleshooting posts, GitHub issue trackers, and technical LatencyMon logs to figure out why an audio stream popped or a frame dropped:

1. **Internet-Wide Data Aggregation:** Stutter Guard incorporates an automated research and verification pipeline that systematically harvested, parsed, and corroborated real-world driver bug reports, vendor hotfixes, and kernel DPC/ISR telemetry dumps from communities across the entire internet.
2. **The 77-Driver Regression Catalog:** Synthesized into an embedded, verified database spanning every major Device Manager class: GPUs (NVIDIA, AMD, Intel), Wi-Fi & Ethernet adapters, Audio DACs & OEM drivers, Storage controllers, and System power management agents.
3. **Instant Automated Diagnosis:** In under 5 seconds, Stutter Guard scans installed hardware, normalizes complex vendor version schemes (e.g., converting Windows 4-part NVIDIA strings `32.0.15.3118` → `531.18`), and provides an explicit, actionable prescription right on screen:
   - **Update** to a confirmed stable release (or newer).
   - **Roll back** to a verified stable baseline if running a bugged regression.
   - **Reconfigure or remove** problematic background polling utilities and audio processing bloatware.

---

## 🛠️ How Stutter Guard Works: The Complete Two-Step Solution

Stutter Guard operates on a clear two-step principle: **Fix the mechanical defects first, then apply the high-performance calibration.**

```
+-----------------------------------------------------------------------------------+
|                        STEP 1: THE MECHANICAL HEALTH CHECK                        |
|                                                                                   |
|  Audits hardware against the internet-harvested 77-driver regression catalog.     |
|  Identifies and resolves bugged kernel drivers before touching any system state.  |
|  --> Output: Clean, verified baseline drivers with zero DPC/ISR storms.           |
+-----------------------------------------------------------------------------------+
                                         │
                                         ▼
+-----------------------------------------------------------------------------------+
|                           STEP 2: THE FULL "ECU TUNE"                             |
|                                                                                   |
|  Calibrates Windows scheduling, interrupt routing, and link power states:         |
|  - Quarantines gaming cores (100% interrupt-free for the game render thread).     |
|  - Routes GPU, mouse, network, and audio IRQs to dedicated secondary cores.       |
|  - Strips latency-inducing PHY power gating (EEE, Green Ethernet, ASPM).          |
|  - Locks global timer resolution at native 0.5 ms.                                |
|  - Trims idle maintenance bursts without touching anti-cheat or critical services.|
|  --> Output: Flat frame-time graphs, elevated 1% lows, and locked input pacing.   |
+-----------------------------------------------------------------------------------+
```

### The Car ECU Tune Analogy

Imagine a high-performance turbocharged car running a restrictive factory economy ECU map:
- **The Dyno Pull (Cinebench / 3DMark Time Spy):** On a dyno at wide-open throttle, the car produces its rated peak horsepower. However, on the street, that same factory tune suffers from turbo boost lag on throttle tip-in and sputters at redline due to conservative emissions throttling.
- **Stock Windows:** Out of the box, Windows is tuned for fleet power conservation and office multi-tasking. It scatters interrupts across all cores indiscriminately. When a high-polling mouse moves or a network packet arrives, Windows dumps that interrupt directly onto the CPU core executing the game's render loop—stalling the frame from 5 ms to 35 ms.
- **The Stutter Guard Tune:** Applying a calibrated performance ECU tune eliminates boost lag and stabilizes redline execution, trading idle sleep states for immediate transient responsiveness. Stutter Guard applies that exact tuning to Windows: gaming cores remain strictly dedicated to the game, peripheral interrupts live on isolated secondary cores, and power-saving sleep transitions are eliminated.

---

## Table of Contents
- [The v0.7.1 Driver Latency Advisory Engine](#-the-v071-driver-latency-advisory-engine)
- [How Stutter Guard Works: The Two-Step Solution](#️-how-stutter-guard-works-the-complete-two-step-solution)
- [Key Modules & Capabilities](#key-modules--capabilities)
- [Comprehensive Step-by-Step Usage Guide](#comprehensive-step-by-step-usage-guide)
  - [Phase 1: Pre-Flight Baseline Measurement](#phase-1-pre-flight-baseline-measurement)
  - [Phase 2: Hardware Inspection, Driver Audit & Core Benchmark](#phase-2-hardware-inspection-driver-audit--core-benchmark)
  - [Phase 3: Hardware-Aware Interrupt Affinity & MSI Mode](#phase-3-hardware-aware-interrupt-affinity--msi-mode)
  - [Phase 4: Network Adapter PHY Optimization](#phase-4-network-adapter-phy-optimization)
  - [Phase 5: Power Plan & Native Timer Resolution](#phase-5-power-plan--native-timer-resolution)
  - [Phase 6: Safe Service & Scheduled Task Presets](#phase-6-safe-service--scheduled-task-presets)
  - [Phase 7: Restart & Live Verification](#phase-7-restart--live-verification)
- [Safety, Backups & Emergency Safe Mode Reversal](#safety-backups--emergency-safe-mode-reversal)
- [Hardware Diagnostics & Community Feedback (100% Optional)](#hardware-diagnostics--community-feedback-100-optional)
- [Supported Hardware & CPU Topologies](#supported-hardware--cpu-topologies)
- [Frequently Asked Questions (FAQ)](#frequently-asked-questions-faq)
- [Download & Running (Pre-Compiled Binary)](#download--running-pre-compiled-binary)
- [Building from Source](#building-from-source)
- [Project Architecture](#project-architecture)
- [License & Non-Commercial Terms](#license--non-commercial-terms)
- [Support & Sponsoring Development](#support--sponsoring-development)
- [Acknowledgments & Research](#acknowledgments--research)

---

## Key Modules & Capabilities

| Module | Core Functionality |
| :--- | :--- |
| **Driver Advisory Engine** *(New in v0.7.1)* | Real-time audit against 77 catalogued driver regressions harvested from internet community reports. Automatically normalizes vendor versions (NVIDIA display drivers, Realtek, Intel) and prescribes concrete remediation: **Update**, **Roll back**, or **Reconfigure/Remove**. |
| **Home (System Scan)** | Read-only hardware and OS topology inspection: Windows build and UBR, CPU core grouping (CCDs / 3D V-Cache / Intel P & E cores / SMT mapping), chassis form-factor detection, GPU drivers and driver files, Virtualization-Based Security (VBS), system timer resolution, active WHEA hardware errors, driver advisories summary, and integrated progressive **Core Benchmark**. Scan reports export cleanly to text. |
| **Interrupts & Affinity** | Hardware-aware IRQ affinity engine. Automatically preserves gaming cores completely interrupt-free (CPU 0/1; the 3D V-Cache CCD on AMD X3D; top-ranked CCDs or P-cores on Intel). Ranks secondary cores with an integrated progressive Core Benchmark (timer wake-up latency, speed, interrupt load, SMT stolen time) and assigns devices strategically: GPU, primary mouse USB controller, default audio, wired/wireless NIC RSS queues (on 2 adjacent cores), and remaining USB controllers. Supports safe MSI mode conversions. |
| **Network** | Eliminates latency-inducing PHY power-saving modes on physical wired network adapters directly from driver properties: Energy Efficient Ethernet (EEE / 802.3az), Green Ethernet, Gigabit Lite, Power Saving Mode, Realtek ASPM/CLKREQ, Selective Suspend, Flow Control, and Interrupt Moderation. Automatically restarts the NIC without requiring a full Windows reboot. |
| **Power Plan** | Applies a customized "Stutter Guard - Ultimate" power plan (light C-states, EPP 0, minimum processor state 100%, 5000 ms PPM check intervals, disabled USB/PCIe/NVMe/AHCI power gating), or a tuned Balanced profile for heterogeneous architectures that require core parking. |
| **GPU** | Driver-level AMD registry tuning (power gating, deep sleep, idle power optimizations, and memory clock DPM controls). Inspects the installed driver binary and automatically filters out keys not referenced by the specific installed driver version. Includes an optional toggle for Multi-Plane Overlay (MPO). Dynamic UI displays GPU tuning options when compatible hardware is present. |
| **Boot Tasks** | Configures lightweight persistence via Windows Task Scheduler: automated device power-saving cleanup on system boot and a 6 KB 0.5 ms timer resolution agent running at logon. Recognizes and preserves existing user tasks. |
| **Services** | Granular control over 439 catalogued Windows services with full descriptions and impact warnings. Features tested presets: *Windows Default*, *Gaming Safe*, *Gaming Aggressive*, *Laptop*, and *Workstation / VM Host*. Presets only ever lower startup types; critical OS services, anti-cheat engines (Ricochet, Vanguard, EAC, BattlEye), and Xbox Game Pass services remain strictly protected. |
| **Tasks** | Audits 241 catalogued Windows scheduled tasks, detailing triggers, actions, and idle maintenance bursts (the common cause of latency spikes ~10 minutes into a session). Disables tasks cleanly without deletion. |
| **Recommendations** | Actionable recommendations: live Driver Hazard Advisories with measured latency impact and vendor fix links, Process Lasso core separation guidelines with an automatic "Save Starter Rules" exporter tailored to detected CPU topology, WinUtil configurations, GPU cleaner workflows, LatencyMon audit methodologies, and a read-only BIOS checklist. |
| **Safety & Reversibility** | Automatic system restore points prior to batch application, complete per-tweak registry `.reg` and `.pow` file backups, continuous JSON journaling (`C:\ProgramData\Stutter Guard\journal.json`), per-tweak Revert controls, and a standalone `undo-all.ps1` script executable even in Windows Safe Mode. |

---

## Comprehensive Step-by-Step Usage Guide

Follow this workflow in order. Optimization requires objective measurement.

### Phase 1: Pre-Flight Baseline Measurement

Before applying any modifications, establish an objective, repeatable baseline:

1. Download and install [LatencyMon](https://www.resplendence.com/latencymon).
2. Close background monitoring utilities that poll hardware registers (HWiNFO, MSI Afterburner / RivaTuner, Process Explorer, RGB software).
3. Start LatencyMon and leave the system completely idle (untouched without user input) for **10 to 15 minutes**.
4. Note three numbers:
   - **Highest measured interrupt-to-process latency** (in µs).
   - **Highest reported DPC routine execution time** and its driver file (e.g., `nvlddmkm.sys`, `amdkmdag.sys`, `ndis.sys`, `Wdf01000.sys`).
   - The busiest CPU under the **CPUs** tab.
5. In Stutter Guard, navigate to the **Home** tab and click **Save scan report** to preserve a baseline record of hardware topology, driver advisories, and default driver registry states.

---

### Phase 2: Hardware Inspection, Driver Audit & Core Benchmark

1. On the **Home** tab, inspect the detected hardware profile:
   - **CPU Topology:** Stutter Guard automatically detects CCD boundaries, 3D V-Cache groups, Intel P-cores vs. E-cores, and SMT sibling threads.
   - **Driver Advisories:** Review the driver audit summary. If any installed driver matches known DPC regressions in the catalog, navigate to the **Recommendations** screen to view the recommended action (**Update**, **Roll back**, or **Reconfigure**) before applying system tweaks.
   - **VBS & Timer:** Verifies whether Virtualization-Based Security (HVCI) is running and displays the active global timer resolution.
2. In the **Core Benchmark** card, click **Run core benchmark**:
   - Executes a progressive 2-minute latency and speed assessment across every physical CPU core.
   - Measures timer wake-up jitter, clock execution speed, and background interrupt stolen time across both SMT sibling threads.
   - Ranks physical cores objectively from best to worst for device interrupt handling.

---

### Phase 3: Hardware-Aware Interrupt Affinity & MSI Mode

Navigate to the **Interrupts** screen:

1. **Review the Generated Plan:**
   - **Game Cores Reserved:** CPU 0/1 are always kept clean. On dual-CCD AMD 3D V-Cache chips (7900X3D, 7950X3D, 9900X3D, 9950X3D), the entire 16-thread V-Cache CCD0 is kept 100% interrupt-free. On Intel hybrid CPUs, P-cores are prioritized for games and high-priority devices, while network queues route to E-cores.
   - **Strategic Device Mapping:**
     - **Primary GPU:** Assigned to the top-ranked secondary core.
     - **Mouse USB Controller:** Dedicated, unshared physical core.
     - **Default Audio:** Pinned away from gaming cores.
     - **Network Adapter:** Configured with 2 adjacent physical cores to support consecutive Receive Side Scaling (RSS) queues.
   - **Never-Pinned Protection:** Platform devices, PCIe root bridges, AMD PSP, and NVMe drives (which manage their own multi-queue MSI-X steering) are left strictly untouched.
2. **MSI Mode:**
   - Review MSI Mode status. Devices still running on legacy line-based IRQs (e.g., GPU audio, USB controllers) are flagged for safe MSI conversion.
3. Select the desired device rows and click **Apply selected**.

---

### Phase 4: Network Adapter PHY Optimization

Navigate to the **Network** screen:

1. Stutter Guard reads every physical wired network adapter directly from its driver property registry keys.
2. Review the recommended power-saving toggles:
   - **Energy Efficient Ethernet (EEE / 802.3az):** Disables link sleep transitions.
   - **Green Ethernet & Gigabit Lite:** Forces full-power PHY signaling.
   - **Realtek ASPM / CLKREQ:** Disables bus-level PCIe power gating on Realtek GbE/2.5GbE/5GbE NICs.
   - **Flow Control & Interrupt Moderation:** Configured for maximum throughput responsiveness on desktop systems.
3. Click **Apply selected**. Stutter Guard restarts the adapter driver locally (2 to 4 seconds offline) without requiring a full Windows reboot.

---

### Phase 5: Power Plan & Native Timer Resolution

Navigate to the **Power** and **Boot tasks** screens:

1. **Power Plan:**
   - **Desktop / Fixed Core Tuning:** Select **Stutter Guard - Ultimate** (light C-states capped at C1, Energy Performance Preference `0`, minimum processor state `100%`, 5000 ms PPM check intervals, and PCIe/USB link state power management disabled).
   - **Laptops / Parking-Dependent CPUs:** Select **Balanced (Devices Only)** to preserve core parking and mobile battery profiles while stripping peripheral sleep delays.
2. **Boot Tasks:**
   - **Device Power Saving Cleanup (SYSTEM Boot Task):** Automatically unchecks *"Allow the computer to turn off this device to save power"* across all USB hubs and controllers on system startup.
   - **0.5 ms Timer Agent (Logon Task):** Deploys the 6 KB `DpcTimerRes.exe` helper to keep the Windows system timer locked at 0.5 ms resolution. It verifies the resolution Windows actually granted and exits with a distinct code (3 refused, 4 granted too coarse) instead of holding a request the kernel ignored. Ships as a managed build so it can be rebuilt with the in-box `csc.exe`; `timerres.c` remains the reference native implementation for anyone with MinGW-w64.
3. Click **Apply selected**.

---

### Phase 6: Safe Service & Scheduled Task Presets

Navigate to the **Services** and **Tasks** screens:

1. **Services:**
   - Select the preset that matches the target machine:
     - **Gaming Safe (Default):** Shuts down telemetry, diagnostic tracking, error reporting, and background indexing loops. Windows core services, anti-cheat drivers (Ricochet, Vanguard, EAC, BattlEye), and Xbox Game Pass dependencies remain strictly protected.
     - **Gaming Aggressive:** Disables print spooling (if no printer exists), Bluetooth (if no BT devices exist), and secondary search indexing.
     - **Laptop / Workstation:** Keeps virtualization (SVM/VT-x) and mobile sensors enabled.
2. **Tasks:**
   - Disables idle maintenance triggers that wake up ~10 minutes into a session (the common cause of sudden mid-game frame spikes). Tasks are disabled cleanly—never deleted.
3. Click **Apply selected**.

---

### Phase 7: Restart & Live Verification

1. Restart Windows so kernel-level IRQ affinities and power plan parameters take effect.
2. Launch Stutter Guard and navigate to **Interrupts**:
   - Click **Check interrupts**: Runs a 30-second silent audio stream and network burst, querying Windows kernel performance counters to verify device interrupts and DPCs are hitting their assigned CPU cores.
3. Launch **LatencyMon** and run a 10 to 15-minute hands-off idle run:
   - Compare measured interrupt-to-process latency, highest DPC routine duration, and per-CPU load against the Phase 1 baseline.

---

## Safety, Backups & Emergency Safe Mode Reversal

Stutter Guard is built from the ground up on the principle of **absolute reversibility**:

```
C:\ProgramData\Stutter Guard\
├── journal.json           # Real-time append-only transaction journal of every change made
├── Backups\               # Timestamped sessions containing original .reg and .pow exports
│   └── 20261025-143022\
│       ├── InterruptAffinity.reg
│       ├── NetworkSettings.reg
│       └── OriginalPowerPlan.pow
└── undo-all.ps1           # Standalone PowerShell script to revert every tweak without the app
```

### How to Revert:
- **In-App Per-Tweak Revert:** Every row in Stutter Guard has its own **Revert** button that restores the original pre-tweak value recorded in the journal.
- **In-App Global Revert:** On the **Safety** screen, click **Revert everything** to restore all original registry values, delete the custom power plan, and remove scheduled tasks in one operation.
- **Emergency Safe Mode Reversal (No App Required):** If a third-party device driver misbehaves after an affinity change:
  1. Boot Windows into **Safe Mode** or open a **Windows Recovery Command Prompt**.
  2. Navigate to `C:\ProgramData\Stutter Guard\`.
  3. Right-click `undo-all.ps1` and select **Run with PowerShell** (or run `powershell -ExecutionPolicy Bypass -File "C:\ProgramData\Stutter Guard\undo-all.ps1"`).
  4. Restart the PC. All original Windows defaults are restored immediately.

---

## Hardware Diagnostics & Community Feedback (100% Optional)

Unlike commercial tools or dubious "debloaters", Stutter Guard has **zero background network calls**. It never phones home automatically.

On the **Home** tab, two manual buttons allow submitting diagnostics directly to developer channels on Discord:
- **Submit hardware report:** Uploads the hardware scan report (CPU topology, GPU drivers, timer resolution) and benchmark logs. Helps expand hardware affinity heuristics for rare or newly released chipsets.
- **Send feedback:** Prompts for direct user feedback, suggestions, or bug description, and sends it along with the diagnostic log.

Both actions require explicit confirmation and display the exact payload before transmission.

---

## Supported Hardware & CPU Topologies

Stutter Guard includes an exhaustive hardware matrix verified by automated integration tests:

| Architecture | Example Processors | Optimization Strategy |
| :--- | :--- | :--- |
| **AMD Dual-CCD 3D V-Cache** | Ryzen 9 9950X3D, 9900X3D, 7950X3D, 7900X3D | Isolates entire V-Cache CCD0 (CPUs 0–15) exclusively for games. Routes GPU, mouse USB, audio, and NIC interrupts to frequency CCD1 (CPUs 16–31). |
| **AMD Single-CCD** | Ryzen 7 9800X3D, 7800X3D, 5800X3D, 5700X, 5600X | Reserves CPU 0/1 and top-ranked physical cores for games; assigns peripheral IRQs across remaining cores. |
| **AMD APUs & Laptops** | Ryzen 7 5800H, 6800H, 7840HS, 8845HS, Strix Point | Enforces laptop battery safety; respects flat-rank core spreads (<5% clock delta) and preserves hand-tuned SMT thread assignments. Disables unused iGPU interrupts on Optimus/dGPU configs. |
| **Intel Hybrid (P + E)** | Core i9-14900K, 13900K, 12900K, i7-14700K | Preserves CPU 0/1 and top P-cores for games. Pins GPU and mouse USB to remaining P-cores; routes NIC RSS and peripheral USB to consecutive E-cores. |
| **Intel Arrow Lake** | Core Ultra 9 285K, Core Ultra 7 265K | Handles interleaved P/E layouts without SMT; guards unequal L3 cache blocks from false V-Cache triggers. |
| **Intel Legacy / Quad-Core** | Core i7-7700HQ, i7-6700K, i5-4690K | Fallback logic reserves only CPU 0/1 so secondary candidate cores remain available. Primary discrete GPU and USB mouse receive dedicated cores; screen-driving iGPU shares with audio and Wi-Fi. |

---

## Frequently Asked Questions (FAQ)

#### Will Stutter Guard trigger multiplayer anti-cheat bans?
**No.** Stutter Guard does not inject code, hook system APIs, modify game memory, or replace system binaries. It only modifies official, documented Windows registry parameters and driver properties provided by Microsoft and hardware vendors. Anti-cheat services (Riot Vanguard, Call of Duty Ricochet, Easy Anti-Cheat, BattlEye) remain permanently locked and untouched.

#### Can these tweaks damage hardware or cause overheating?
**No.** Stutter Guard does not overclock, undervolt, alter CPU/GPU core multipliers, or modify fan profiles. Setting light C-states (C1 only) increases idle power consumption by approximately 15–20 W on desktop systems, which is explicitly noted in the UI.

#### Does Stutter Guard work on Windows 10 as well as Windows 11?
**Yes.** Stutter Guard natively supports Windows 10 (from build 2004 / 20H2 up to 22H2) and Windows 11 (21H2 through 24H2 and insider builds). Topology detection automatically adapts to OS scheduler differences.

---

## Download & Running (Pre-Compiled Binary)

To run the tool without compiling from source:

1. Download **`StutterGuard.exe`** from the **[Stutter Guard Releases](https://github.com/Kareem-Shabaka/stutter-guard/releases)** page.
2. Right-click `StutterGuard.exe` and select **Run as administrator**.

`StutterGuard.exe` is a self-contained, single-file executable for Windows 10 and 11 (x64). No installer or external .NET runtimes required.

---

## Building from Source

### Prerequisites
- Windows 10 or 11 (x64)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`winget install Microsoft.DotNet.SDK.10`)
- Optional (for recompiling the embedded C timer tool): MinGW-w64 (`x86_64-w64-mingw32-gcc`)

### One-Click Build
Clone the repository and run the root build script:

```cmd
git clone https://github.com/Kareem-Shabaka/stutter-guard.git
cd stutter-guard
build.cmd
```

`build.cmd` executes the entire 215 unit test suite, restores dependencies, and publishes a self-contained single-file executable to:
```
publish\StutterGuard.exe
```

### Running Self-Tests Independently
```cmd
dotnet run --project tests/DpcTweaker.SelfTest -c Release
```

---

## Project Architecture

```
stutter-guard/
├── .github/
│   ├── FUNDING.yml                  # GitHub Sponsors profile configuration
│   └── workflows/
│       └── build-and-release.yml    # CI/CD automated build, self-test, and release pipeline
├── src/
│   ├── DpcTimerRes/                 # Standalone 0.5 ms timer resolution agent (timerres.cs / .c)
│   │   ├── timerres.c
│   │   └── DpcTimerRes.exe
│   ├── DpcTweaker.Core/             # Hardware detection (SetupAPI/cfgmgr32), affinity planner,
│   │   ├── Catalog/                 #   embedded bad_drivers.json, services.json, tasks.json catalogs,
│   │   ├── Detection/               #   CPU topology parsers, journal engine, and backup sets
│   │   ├── Infra/
│   │   ├── Interrupts/
│   │   ├── Model/
│   │   ├── Native/
│   │   ├── Reports/
│   │   └── Tweaks/
│   ├── DpcTweaker.UI/               # Pure MVVM view models (INotifyPropertyChanged, RelayCommand)
│   └── DpcTweaker.App/              # WPF desktop application (Fluent Dark/Light UI, boot agent)
├── tests/
│   └── DpcTweaker.SelfTest/         # Zero-dependency test matrix covering all CPU architectures (215 tests)
├── tools/                           # Automated bad driver catalog generation & verification toolchain
├── build.cmd                        # Automated build & publish script
├── LICENSE                          # GPLv3 with Non-Commercial Rider
└── README.md
```

---

## License & Non-Commercial Terms

Copyright (C) 2026 Kareem Shabaka. All Rights Reserved.

Stutter Guard is licensed under the **GNU General Public License v3 (GPLv3)** subject to an express **Non-Commercial / No-Monetization Addendum (Rider)**:

- **Free for Personal & Community Use:** Users are 100% free to run, inspect, compile, modify, and distribute this software for personal, academic, educational, and gaming purposes.
- **Copyleft Reciprocity:** Any modified versions or forks distributed must share their complete source code under the exact same license terms.
- **Strictly No Monetization:** Selling, charging fees for copies, paywalling, or commercially monetizing the software, its compiled binaries, or any derivative works or forks (including listing on commercial app stores such as the Microsoft Store or Steam) is strictly prohibited without prior written authorization from the copyright holder.

See the complete legal text in [LICENSE](LICENSE).

---

## Support & Sponsoring Development

Stutter Guard is developed independently and distributed **100% free of charge**. No paywalls, no stripped feature tiers, no advertisements, and no forced background telemetry.

If Stutter Guard eliminated micro-stutters, fixed frame pacing, or smoothed out audio latency, back the project on GitHub Sponsors:

👉 **[Sponsor Kareem Shabaka on GitHub](https://github.com/sponsors/Kareem-Shabaka)**

---

## Acknowledgments & Research

- **Resplendence Software:** Creators of [LatencyMon](https://www.resplendence.com/latencymon), the gold standard in Windows DPC latency analysis.
- **valleyofdoom:** Research and methodology references from PC-Tuning and AutoGpuAffinity.
- **Microsoft Hardware Dev Center:** Windows Driver Kit (WDK) documentation on Interrupt Affinity Policy, MSI/MSI-X architectures, and Processor Power Management (PPM).
