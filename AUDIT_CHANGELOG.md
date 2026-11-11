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

## Test growth

| Stage | Tests |
|---|---|
| Baseline | 215 |
| After Waves 1–2 | 276 |
| After Wave 4 | 316 |
| After Wave 5 | 371 |
| After Wave 6 | **393** |

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