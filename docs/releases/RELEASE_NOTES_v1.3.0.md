# Stutter Guard v1.3.0 - Release Notes

Released from `main` at the commit this tag names. Version stamped `1.3.0` in
`Directory.Build.props`; the `FileVersion` of the binary is `1.3.0.0`.

---

## The two themes

**The app tells you what it will do before it does it.** A tier is now a per-tab decision with a single
control that names the tab it edits; a tick says "this row is part of the tier"; findings open with an
action instead of homework.

**The release you download is the release that was built.** The build job publishes a checksum generated
from the exact bytes it uploaded, and a clean pair of publishes of one commit is byte-identical on a
fixed toolchain.

---

## Interface

| Change | Detail |
| :--- | :--- |
| **One tier control, in the shell** | Every module page used to render its own tier picker. The copies had already drifted - one promised to match "this PC", the other "this tab". There is one control now; it follows the selected tab, and its sentence names the tab being edited. |
| **The navigation rail shows every tab's tier** | Reading down the rail answers "what is each tab set to" without opening any of them. |
| **Interrupts opens with its list** | Three cards of reference sat above the checkbox list while the Apply button stayed pinned on screen. Rows come first; the benchmark, the plan and the post-restart check follow. |
| **Cores are named as cores** | `core 8 (CPU 16)`, not `CPU 16`. A core can carry two logical processors, and Windows numbers the processors. The page had been listing logical processors under the word "cores" in the same sentence that explained SMT. |
| **Background is one page** | Startup tasks, services and scheduled tasks as three sections of one page, with a scrolling list and a toolbar that stays put. |
| **Home answers first** | Verdict, then actions, then the reference tables - collapsed behind one line. |
| **Findings inform** | A triage finding opens with something the reader can do, states no magnitude it cannot source, and does not tell an elevated user to elevate. |

## State and continuity

| Change | Detail |
| :--- | :--- |
| **Versioned persisted files** | `journal.json`, the benchmark result and the driver fingerprint each carry a format version. `Journal.CurrentVersion` is now **2**; enums are stored by name. Migration happens in memory on load, and every change so far has been additive. |
| **A corrupt journal still cannot be overwritten** | The degraded latch remains: `Save`, `ClearAll` and `WriteUndoScripts` all refuse while the journal cannot be read. |
| **Drift detection is narrower and more specific** | Only drivers that can actually drift are fingerprinted; unreadable processes are named rather than counted. |

## Release integrity

| Change | Detail |
| :--- | :--- |
| **The release carries its own checksum** | `StutterGuard.exe.sha256` is generated on the build machine from the file it uploads, and attached beside it. |
| **Reproducible builds, measured** | Two clean publishes of one commit into two different directories: `65,447,000` bytes, SHA-256 `851FA3465EA11D59B141C3FEF8266E9352BE30FEA8477EC80B7D6B4F403D8880`, identical both times (.NET SDK 10.0.402, win-x64). `build.cmd --verify-repro` repeats it. |

A build on a different SDK patch level or output path can differ. Verify a download against the
`.sha256` published with that release, not against a hash you produced yourself.

---

## Verifying a download

```powershell
Get-FileHash .\StutterGuard.exe -Algorithm SHA256
```

Compare against the hash printed on the release page. The release is the authority for its own bytes.

---

## What this release does not fix

- **DPC duration is still not measured by the app.** It reports interrupt *rates* and, where a driver is
  named, a documented cause. The length of an individual DPC needs kernel tracing, which the app does not
  do. A very long outlier - the kind that shows up as a single 800 us DPC in LatencyMon - will not be
  explained by anything here.
- **SMIs.** They are named as a remaining cause precisely because no register, mask or power setting can
  reach them.
- **CPU Sets for games.** Placement is still hard affinity. CPU Sets are documented and planned, but they
  are not in this release.

---

## Upgrading and rolling back

The journal format is now version 2 and the app migrates older files on load. Nothing in the migration
rewrites the values recorded before the upgrade, so `undo-all.ps1` and the recorded originals are
unaffected.

Full detail of every change is in the commit history; this file states what the release does.

**License:** GPL-3.0. See [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).
