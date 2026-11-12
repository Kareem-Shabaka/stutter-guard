# Stutter Guard — Audit & Remediation Changelog

Full record of the continuous audit run that took the codebase from **215 passing self-tests**
to **393**, with every build at **0 warnings / 0 errors**.

---

## Commits

| Commit | Scope |
|---|---|
| `48179a7` | `feat(core)`: transactional WAL journal, degraded latch, SMT-aware RSS, xHCI escalation |
| `4515f1a` | `fix(core)`: harden WAL, gate degraded journal, SMT-correct RSS & interrupt checks, journal System Protection |
| `c9d38aa` | `feat(timer)`: rebuild `DpcTimerRes` with verified exit codes; fix driver-version misdetection; audit unread subsystems |
| `4822fb9` | `feat(engine)`: drift detection across all hardware-bound tweaks with 1-click auto-heal |

Baseline before the audit: `b4f7e45`.

---

## Wave 1 — Transactional Write-Ahead Durability

| # | Bug | Fix | Where |
|---|---|---|---|
| F2.1 | Journal flushed **after** the mutation: a crash left the system changed with no record | Each tweak runs in a `JournalTransaction`; `RecordOriginal` writes `journal.json` **before** the change | `Journal.cs:201`, `Engine.cs:139` |
| F2.2 | A corrupt journal silently regenerated `undo-all.ps1` from the empty document, destroying the only out-of-app restore path | `IsDegraded` latch: `Save`, `ClearAll` and `WriteUndoScripts` all refuse | `Journal.cs:179`, `JournalRestorer.cs:244` |
| F2.3 | Journal and undo script could drift apart between save points | `SidecarWriter` rewriters `undo-all.ps1` on every flush | `Engine.cs:24` |
| F2.4 | A failed tweak (e.g. `schtasks` Access Denied) left phantom entries in the undo script | `RollbackTransaction` removes exactly what the transaction added | `Journal.cs:240` |
| F2.5 | `power.plan` was a pseudo-id no `ITweak` owned, so `plan-created` could never be reverted | Bound to `power.active`; `RebindTweak` migrates orphans on startup | `Power.cs:304`, `Engine.cs:62` |
| F2.6 | `sc.exe config` writes `Start` only — a *delayed* auto service came back plain auto | `SplitJournal` + `RestoreDelayedAutostart` restore both halves, in-app and in the generated script | `Cleanup.cs:147`, `JournalRestorer.cs:94` |
| F2.7 | Every PnP export name was truncated at 150 chars, cutting the `Affinity Policy` vs `MessageSignaledInterruptProperties` suffix | 8-char FNV-1a hash appended past the cap | `BackupSet.cs:105` |
| F2.8 | `New-Item -Path` (wildcard-expanding) in generated PowerShell | `-LiteralPath` | `JournalRestorer.cs:141` |
| F2.9 | `RegistryValueKind.None` could not round-trip | Recorded as raw hex | `RegistryOps.cs:21` |

---

## Wave 2 — Affinity, RSS, Timers

| # | Bug | Fix | Where |
|---|---|---|---|
| F1.4 | `*MaxRssProcessors` counted **logical** processors while the code treated it as cores: 2 queues over `[20,22]` landed on LPs 20+21, the two threads of **one** core | `RssPlan` computed from real topology | `AffinityPlanner.cs:401`, `Interrupts.cs:136` |
| F1.5 | An xHCI root hub carrying both the mouse and the default USB DAC got one core | Escalates to 2 cores **when cores are spare**, with a budget guard so no other device is pushed into sharing; plan notes say to move the DAC | `AffinityPlanner.cs:290` |
| F1.2 | 7800X3D/9800X3D reported as generic "Single CCD" | `IsSingleCcdVCache` (single L3 ≥ 64 MB) + name fallback | `CpuTopology.cs:54` |
| F3.2 | `timerres.c` discarded the granted resolution | `NTSTATUS` checked (exit 3), `actual > wanted + 100` (exit 4) | `timerres.c:99` |
| F3.3 | `<Arguments>` matched as raw XML text; any reformat read as "not applied" | Proper `XDocument` parse | `BootTasks.cs:145` |

---

## Wave 4 — Severity 1 & 2 blockers

| # | Bug | Fix | Where |
|---|---|---|---|
| #1 | A journal with `"Originals": null` deserialized *successfully* then threw `NullReferenceException` on first read — the app showed "The scan failed" and never started | Coalesce `Originals`/`Applied`/`State` after deserialize | `Journal.cs:136` |
| #2 | `Flush()` swallowed save failures, so a full disk meant mutating the system with no durable record | Throws inside a transaction; logs outside one | `Journal.cs:201` |
| #17 | ~140 rewrites of a 20 KB `undo-all.ps1` per apply | JSON WAL per entry, sidecar once per tweak | `Journal.cs:293`, `Engine.cs:165` |
| #3 | No single-instance guard: two copies each held their own journal snapshot and the last save dropped the other's before-states | `Local\` mutex + activate event; `--agent` bypasses it | `App.xaml.cs:63` |
| #4/#5 | `IsDegraded` was invisible and `Save` still overwrote the journal | Safety-page caution card naming the `.broken-*` file, **Start a fresh journal** button, `Save` refuses while degraded | `Pages.cs:623`, `Journal.cs:179` |
| #6 | The interrupt check summed only first threads, reporting "hardly any network interrupts" while RSS ran on the siblings | `CpusFor` expands to every thread of the assigned cores | `InterruptCheck.cs:38` |

---

## Wave 5 — Subsystem audit

| # | Bug | Fix | Where |
|---|---|---|---|
| #18 | Shipped `DpcTimerRes.exe` predated its own source (no C toolchain on the machine) | Rebuilt as a 6.6 KB managed twin via in-box `csc.exe`; verified exit codes 0/3/4 live; `timerres.c` kept as the native reference | `timerres.cs`, `Assets\DpcTimerRes.exe` |
| W5.1 | `NormalizeVersion` treated *any* version starting `30./31./32.` as NVIDIA — Intel Arc (`32.0.101.x`) became `151.86` and AMD (`32.0.21025.x`) was mangled too | Require NVIDIA's `14`/`15` third component; match driver files from the start so `stornvme.sys` is not "nv" | `DriverAuditor.cs:265` |
| W5.2 | `EndsWith`/`StartsWith` matched **inside** a version component (`31.0.15.3118` "ended in" `1.18`) | Whole-component suffix/prefix comparison | `DriverAuditor.cs:213` |
| W5.3 | The first matching catalog entry won, so a `Critical` advisory could be shadowed by a `High` duplicate of the same driver | Most-severe match wins | `DriverAuditor.cs:89` |
| W5.4 | `ScanReport` shifted a nullable `Address`, printing blanks | Explicit null check | `ScanReport.cs:71` |
| W5.5 | `CancellationTokenSource` created per run and never disposed | `BeginRun()` disposes the previous one | `InterruptsPanel.cs` |

---

## Wave 6 — Drift detection & 1-click auto-heal

`TweakBase.Detect()` already turned "applied + live state no longer matches" into `Drifted`, so
drift was only ever *reported* per row. Wave 6 makes it actionable engine-wide.

| Feature | Where |
|---|---|
| `FindDrifted` / `DriftedCount` / `DriftedTweaks` — covers AMD GPU keys, MSI mode, interrupt affinity, NIC power & RSS keywords and `GlobalTimerResolutionRequests` in one pass, because each tweak reads its own state back | `Engine.cs:224` |
| `Heal` / `HealDrifted` — re-applies inside journal transactions. A second apply **does not duplicate** the baseline: `RecordOriginal` keeps the first entry per (tweak, kind, target) | `Engine.cs:290` |
| Safety-page drift banner with **Re-apply drifted tweaks** | `Pages.cs:701`, `MainWindow.xaml` |
| `DriftedCount` surfaced on the shell status line | `MainViewModel.cs` |

---

## Wave 7 — Release

- `dotnet publish -c Release` → `publish\StutterGuard.exe` (65 MB, self-contained, all fixes baked in).
- Live `C:\ProgramData\Stutter Guard\journal.json` and `undo-all.ps1` verified **byte-identical**
  to `PreAuditBackup` (SHA-256 `C1AA45E5…` / `3FA4B048…`), 87 original states intact.
  Every test redirects `Paths.DataDir` to a temp folder and restores it in `finally`.

---

## Wave 8 — 12-Topology Simulation Matrix & DriverAuditor Stress Suite

Two real bugs found by the new tests, plus the harness itself. Test count 450 → 570.

### Bugs found and fixed

| # | Bug | Fix | Where |
|---|---|---|---|
| W8.1 | `NormalizeVersion` treated **any** `nv*` driver file as an NVIDIA *display* driver. `nvraid.sys` / `nvstor.sys` are storage drivers, and their `10.0.19041.1234` was mangled into a fabricated `904112.34` — which would then be compared against display-driver advisory versions. | The loose file-name fallback is gone. Translation now requires the third version component to be exactly `14` or `15`, which every real NVIDIA display driver uses and no storage driver does. | `Catalog\DriverAuditor.cs:269` |
| W8.2 | **22 of the 77 driver-catalog rules can never fire in a real scan.** The auditor is complete (all 77 fire when handed a matching device), but it is driven from `profile.Gpus` + `profile.Devices`, and `PciScanner` emits only `PCI\` nodes. The 22 rules for USB (9), SWC (6), ROOT (3), HDAUDIO (2) and ACPI (2) devices have nothing to match. | **Characterized, not yet fixed.** Locked in with an explicit assertion so it cannot regress silently and cannot be forgotten. Fixing it means teaching the detection pipeline to emit non-PCI device nodes. | `Detection\PciDevices.cs:222`, test `Wave 8 P1: every one of the 77...` |

### Part 1 — DriverAuditor stress suite (new tests)

- **Vendor collision matrix**: NVIDIA `32.0.15.6094 → 560.94`, `31.0.15.3118 → 531.18`, `32.0.14.7212 → 472.12`; Intel Arc `32.0.101.5186`, AMD `32.0.12019.1028` / `31.0.24033.1003`, `stornvme.sys`, `nvraid.sys`, `nvstor.sys` all provably *untranslated*.
- **Component-boundary traps**: a rule for `1.18` must not match `.3118`; `24.1` must not match `24.10` or `24.11`; `1.1` must not match `11.1`.
- **Hostile input fuzz**: null / empty / whitespace / non-numeric (`v1.0-beta`) / zero (`0.0.0.0`) / 400-char versions, OEM suffixes (`32.0.15.6094 (WHQL)`), full `\SystemRoot\...DriverStore\...` and `\\?\` paths, empty and null device lists — zero exceptions, and a DriverStore path still matches its base file name.
- **All 77 catalog rules exercised**: each entry's own hardware pattern and first bugged version are fed through `IsHardwareMatch`, `IsVersionBugged` and the full `Audit()` path. 77/77 matcher-reachable, 77/77 audit-reachable, 55 PCI + 22 non-PCI.

### Part 2 — 12-topology hardware simulation matrix

| # | Topology | Key asserted invariants |
|---|---|---|
| 1 | 9950X3D / 7950X3D — 16C/32T, 96 MB CCD0 + 32 MB CCD1 | CCD0 (LPs 0–15) gets **zero** interrupts; everything lands on 16–31; RSS too |
| 2 | 9900X3D / 7900X3D — 12C/24T asymmetric 6+6 | CCD0 (LPs 0–11) clean; 6 free cores vs 8 device slots → sharing on CCD1 only, GPU never shares |
| 3 | 9800X3D / 7800X3D — 8C/16T single-CCD V-Cache | `IsSingleCcdVCache`, `CpuKind == "Single CCD with 3D V-Cache"`, CPU 0 + top game cores protected |
| 4 | 9950X3D SMT **off** — 16C/16T | `SmtEnabled == false`; RSS 1 processor/core, contiguous 2-LP span, no SMT doubling; masks valid 8-byte LE |
| 5 | i9-14900K — 24C/32T hybrid | 2 threads on P-cores / 1 on E-cores handled by `RssFor` **and** `InterruptCheck.CpusFor`; Balanced parking preserved |
| 6 | Core Ultra 9 285K Arrow Lake — 24C/24T, no HT | hybrid + no SMT ranks cleanly; unequal L3 does not fake X3D |
| 7 | 4C/8T and 4C/4T extreme starvation, 6 devices | no exception, nothing on core 0, GPU never shares, graceful sharing |
| 8 | LP 63 boundary & >64-LP multi-group | `1UL << 63` → `00 00 00 00 00 00 00 80`; bit 31 unsigned-correct; 128-thread read-only |
| 9 | Dual GPU: Radeon iGPU + discrete RX 9070 XT | discrete takes slot 0; iGPU ranked below |
| 10 | Crowded USB/PCIe: 2 mouse hubs + shared hub + 2 NVMe + bridge | NVMe/bridges untouched, shared hub escalated to 2 cores, no device pushed into sharing |
| 11 | Laptop / battery | CurrentActive plan, Laptop preset, no DC forcing, desktop-only settings excluded, timer task off by default |
| 12 | Hostile device nodes | keyless device, line-based IRQ, bare NIC, code-10 device, 0-mask pin, empty queue options — no throws |

### Verification

```
dotnet build -c Release   →  0 Warning(s), 0 Error(s)
--selftest                →  570 passed, 0 failed
dotnet publish -c Release →  publish\StutterGuard.exe refreshed
```
---

## Wave 9 — Configurable hybrid P/E placement (the 12700K report)

A user on an **i7-12700K** reported that the 2-minute core benchmark told them to put their Intel
I210 NIC on E-cores while GPU and USB stayed on P-cores. Reproduced exactly on a synthetic Alder
Lake 8P+4E topology. The behaviour was working as designed — but the design silently overrode the
benchmark the user had just run, and never said why.

Preferred() hard-coded "GPU and Input want P-cores, everything else wants E-cores", so a measured
P-core that woke up 3 µs faster could never win for the NIC, and three P-cores sat idle while audio
and network were pushed onto the 4-core E-cluster across the ring.

### New option: HybridDevicePolicy (Interrupts screen, hybrid CPUs only)

| Mode | Behaviour |
|---|---|
| **EcoresForNetAudio** *(default, unchanged)* | GPU + mouse on P-cores; audio, network and secondary USB on E-cores. Maximum game isolation. |
| **PcoresWhenAvailable** | Keeps CPU 0/1 plus 4 P-cores for the game, then uses spare P-cores before spilling onto E-cores. |
| **BenchmarkDecides** | No P/E preference at all: pure measured ranking, E-cores included. |

On a 12700K this changes the NIC from *E-cores 17,18* to *spare P-cores* under the middle mode, and
under BenchmarkDecides it follows whichever cluster the benchmark actually measured as steadier.

### Explicit P/E reasoning on every row

HybridNote() appends to each assignment's reason on a hybrid CPU:

> Placed on E-core(s) (LP 17,18) to keep P-cores free for game threads — switch 'Hybrid CPU
> placement' in Interrupts options if you prefer P-cores or benchmark-only ranking.

or simply Placed on P-core(s) (LP 6). No hybrid owner has to guess again.

### Files

| Change | Where |
|---|---|
| HybridDevicePolicy enum + HybridGamePoolPcores | Core\Interrupts\AffinityPlanner.cs:? |
| PreferredPredicate — the P/E rule, now policy-driven | Core\Interrupts\AffinityPlanner.cs:? |
| HybridNote — per-device P/E explanation | Core\Interrupts\AffinityPlanner.cs:? |
| Persisted option (defaults to EcoresForNetAudio) | Core\Tweaks\Options.cs:? |
| Options-panel choice, hidden unless IsHybrid | UI\InterruptsPanel.cs:88, :? |
| Engine passes the option into the plan | Core\Engine.cs |

### A latent bug fixed while building it

The first cut stored the P-core id set in a [ThreadStatic] field. Two concurrent Plan() calls —
a UI refresh plus a background heal — would have clobbered each other's set and produced wrong
P/E notes. Replaced with an explicit parameter threaded through Assign().

### Tests: 12700K (8P+4E) and 12600K (6P+4E)

30 new assertions in Wave 9, covering all three policies on both CPUs, plus:
- EcoresForNetAudio: GPU/Input on P-cores, audio/NIC on E-cores, only CPU 0/1 + top-2 reserved.
- PcoresWhenAvailable: 12700K puts NIC **and** audio on spare P-cores; 12600K fits 3 devices on its 3 free P-cores and spills only the fourth.
- BenchmarkDecides: follows the measured order both ways — P-cores steadier → NIC on P; E-cores steadier → GPU takes an E-core on merit and the NIC spills to P once the 4-core E-cluster is consumed.
- Note text asserted for both the E-core and P-core wording.
- Option defaults and With() copy semantics.

### Verification

```
dotnet build -c Release   →  0 Warning(s), 0 Error(s)
--selftest                →  600 passed, 0 failed
dotnet publish -c Release →  publish\StutterGuard.exe refreshed
```
---

## Wave 10 — Three architectural hardware traps + 28-CPU simulation suite

### Trap A — false 3D V-Cache detection on Zen 5 Strix Point (fixed)

`VCacheGroup` only asked `big >= small * 3/2`. A **Ryzen AI 9 HX 370** has 4 Zen 5 cores with
**16 MB** L3 and 8 Zen 5c cores with **8 MB** — `16 >= 12`, so it was labelled a dual-CCD X3D part
and the app reserved its "V-Cache CCD" for the game.

Now a group only counts as V-Cache at **≥ 64 MB** (`CpuTopology.cs:58`), which every real X3D part
clears (96 MB single, 96/32 dual, 128 MB) and no non-X3D chip does. The chip is described honestly
instead: `HasAsymmetricL3` (`:66`) and a `CpuKind` that names both groups rather than claiming
"3D V-Cache" (`SystemProfile.cs:112`). Game threads still go to the larger-cache group — that
behaviour was already correct and is unchanged.

### Trap B — Intel LP-E island cores with no L3 (fixed)

Meteor Lake's 2 and Lunar Lake's 4 Low-Power Island E-cores sit on the SoC tile outside the ring bus
and report `L3Id == -1`. Nothing looked at that, so a NIC or mouse controller could be pinned there
and pay a cache-missing memory round trip on every interrupt.

`AffinityPlanner` now keeps L3-less cores out of the candidate pool whenever L3-backed cores exist
(`AffinityPlanner.cs:185`) and says so in the plan notes.

**A bug in my own first attempt**, caught by the new Lunar Lake fixture: the pre-existing
starvation fallback (`free.Count < 2`) rebuilt the free list from *all* cores, silently undoing the
exclusion on a 4-P-core Lunar Lake — every device landed on LP-E. The fallback now relaxes the
*reservation* first while keeping the L3 rule, and only drops the rule when no L3-backed core is
left at all (`AffinityPlanner.cs:193`).

### Trap C — non-contiguous P/E numbering (verified, no change needed)

Arrow Lake interleaves P and E cores in LP order (P 0–1, E 2–17, P 18–23) with no HT. The planner
already grouped by `CoreId` and ordered by first thread, so it was correct — but untested. It is now
covered by a fixture and three assertions: hybrid + `SmtEnabled == false`, P-cores identified
wherever they sit, and GPU/mouse still landing on P-cores.

### The 28-architecture matrix

Table-driven, each entry built from a synthetic `SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX` buffer and
run through `AffinityPlanner.Plan` with a 14-device inventory:

| Group | Architectures |
|---|---|
| AMD 3D V-Cache | 9950X3D/7950X3D, 9900X3D/7900X3D, 9800X3D/7800X3D/5800X3D, 7600X3D/5600X3D, 9950X3D SMT-off |
| AMD standard / APU | 9950X/7950X/5950X, 9900X/7900X/5900X, 9700X/7700X/5800X, 9600X/7600X/5600X, 3900X/3950X quad-CCX, Ryzen AI 9 HX 370 |
| Intel hybrid w/ HT | i9-14900K/13900K, i7-14700K, i7-13700K/i9-12900K, i7-12700K, i5-14600K/13600K, i5-12600K/13400F |
| Arrow / Meteor / Lunar | Ultra 9 285K, 285K interleaved, Ultra 7 265K, Ultra 5 245K, Ultra 7 155H (+2 LP-E), Ultra 7 258V (+4 LP-E) |
| Homogeneous & boundaries | i9-10900K/10700K, i3-12100F/3300X, i5-9400F/9600K, Athlon 3000G, Threadripper 7970X (LP 63), Threadripper 7980X (2 groups) |

Universal invariants asserted on every applicable topology:
- CPU 0 **and its SMT sibling** are never assigned to any device.
- Every `AssignmentSetOverride` mask is non-zero, serialises to 8 bytes little-endian, round-trips
  through `AsMask`, and references only LPs `< LogicalCount`.
- Every `RssFor` span satisfies `0 <= Base <= Max < LogicalCount`.
- `InterruptCheck.CpusFor` never leaves `LogicalCount`.
- No exception from any topology × device-role × `HybridDevicePolicy` combination.

### Verification

``````
dotnet build -c Release   →  0 Warning(s), 0 Error(s)
--selftest                →  828 passed, 0 failed
dotnet publish -c Release →  publish\StutterGuard.exe refreshed
``````
---

## Wave 11 - MPO tweak removed, leftover override offered for removal

`graphics.mpo-off` was the one tweak in the app that was a **diagnostic workaround rather than a tune**.
It helped a handful of badly broken systems and gave other people flashing black screens, broken HDR and
broken FreeSync. An app should not switch that on.

### What changed

| Change | Where |
| :--- | :--- |
| `graphics.mpo-off` deleted, module removed from the engine | `Tweaks\Graphics.cs` (deleted), `Engine.cs:80` |
| `MpoOverride` finds leftover overrides from any source | `Tweaks\MpoOverride.cs` |
| `MpoOverrides()` / `RemoveMpoOverrides()` on the engine | `Engine.cs` |
| Home card: "Remove the override" / "Keep it" | `Pages.cs`, `MainWindow.xaml` |

### How the "ask" works

- **Detected:** `DisableOverlays` (what 0.7 set) and `OverlayTestMode` (the older community method) on
  `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`, plus `OverlayTestMode` on each GPU class key.
  `OverlayTestMode = 0` is the stock "overlays allowed" value and is not reported.
- **Asked, not done:** the Home card names every value found and where it came from, and only acts when
  the user presses **Remove the override**. It confirms first.
- **Reversible:** removal is an ordinary journal transaction. `RecordOriginal` captures the value before
  it is deleted, so Revert on the GPU screen (and `undo-all.ps1`) restores it exactly.
- **Not nagging:** **Keep it** is remembered in journal state, keyed to the exact values found. Change the
  override and it asks again.

### Tests (31 new, 828 -> 859)

- The catalog no longer contains any MPO tweak, and no tweak writes `DisableOverlays`.
- Detection accepts `DisableOverlays = 1` and `OverlayTestMode = 5`, rejects `OverlayTestMode = 0`, reports
  nothing on a clean machine, and survives an unreadable key without throwing.
- Removal runs end to end against a throwaway `HKCU` key: value found through the real registry read,
  deleted, baseline recorded as `1`, second scan clean, then `RevertAll` puts `1` back.
- `Keep it` is remembered for those values, a changed value asks again, and an empty finding set is never
  dismissed.

### Live-data note

While this was being built, the live journal on the test machine went from 87 to 86 originals. That was
**not** the test suite: re-running `--selftest` leaves `journal.json` and `undo-all.ps1` byte-identical and
adds zero log lines. The log shows `Reverted graphics.mpo-off` at 02:12:49 with exactly one applied entry
removed, which is a single-row Revert from the running v1.0.0 app (that build still had the row). Its
recorded original was `Existed: false`, so the revert correctly deleted `DisableOverlays` and left the
registry at the Windows default. `PreAuditBackup\journal.good.json` is untouched and still holds all 87.

---

## Test growth

| Stage | Tests |
|---|---|
| Baseline | 215 |
| After Waves 1–2 | 276 |
| After Wave 4 | 316 |
| After Wave 5 | 371 |
| After Wave 6 | 393 |
| After DriftWatcher (cda74dc) | 450 |
| After Wave 8 | 570 |
| After Wave 9 (hybrid policy) | 600 |
| After Wave 10 (28-CPU matrix) | 828 |
| After Wave 11 (MPO removal) | **859** |

Every stage: `dotnet build -c Release` → **0 warnings, 0 errors**; `--selftest` → **0 failures**.

---

## Known limitations

1. **`timerres.c` vs the shipped binary.** No C/C++ compiler exists on this machine (no VS, no
   LLVM, no MSYS2/MinGW, no zig; `winget install zig.zig` could not reach a source). The shipped
   `DpcTimerRes.exe` is therefore a managed twin built with the in-box `csc.exe`, functionally
   identical (P/Invoke to `ntdll`), 6.6 KB instead of 40 KB, and it needs the .NET Framework 4.x
   that ships with Windows. `timerres.c` remains the reference native implementation.
2. **RSS placement is not guaranteed.** NDIS spreads queues over the *contiguous* range
   `[*RssBaseProcNumber, *RssMaxProcNumber]`, so with 2 queues the first two LPs are always the
   SMT siblings of one core. `ChooseQueues` sets the queue count to fill the span (4 queues → 2 per
   core), which is the most the registry keywords can express. Per-LP verification would need
   `Get-NetAdapterRss` data that this Realtek driver does not populate.
3. **#3 and #11/#12 have no automated coverage** — they live in the WPF layer the self-test
   project does not reference.
4. **Two `bad_drivers.json` groups share a `driverFile` + `hardwareIdPattern`** with overlapping
   bugged versions (Pascal `DEV_1B80`, Ampere `DEV_2204`). Harmless now that the most severe match
   wins, but the duplicate content should be merged.