# Security policy

## Reporting a vulnerability

Report privately through GitHub's [Security Advisories](https://github.com/Kareem-Shabaka/stutter-guard/security/advisories/new).
Please do not open a public issue for a vulnerability.

Include what you did, what happened, and what you expected. A proof of concept is welcome; a
proof of concept that only works with administrator rights on a machine the attacker already controls
is usually out of scope, because this app runs with those rights by design.

You can expect an acknowledgement within a week. If a report is valid, the fix ships in the next release
and the advisory names the reporter unless you ask otherwise.

## What this app is, in security terms

It is a local, elevated, single-process desktop application that makes machine-scope configuration
changes. That shapes what a vulnerability means here.

**It holds no secrets and no accounts.** There is no login, no licence server, and no user database.

**It runs with administrator rights.** It edits `HKLM`, device properties, power plans and, when you
tick the relevant row, scheduled tasks. Anything that let an unprivileged user redirect one of those
writes would be a genuine privilege-escalation bug and should be reported as one.

**It makes no network connection on its own.** Nothing is sent on launch, on a timer, or in the
background. The only outbound requests are ones a button starts:

| Action | Destination | Data |
| :--- | :--- | :--- |
| **Submit hardware report** | An address configured on this machine (empty by default) | The hardware report shown in the preview, verbatim |
| **Send feedback** | The same address | The text and attachments shown in the preview |
| **Download a test file** (during the interrupt check, only if ticked) | `speed.cloudflare.com` | None sent; the response is discarded unread |

No destination is compiled into the program. An address is asked for once, stored in the local journal, and
only ever used for a report you pressed a button to send; with none set, the buttons say so and the report is
saved to a file instead. This is deliberate: two webhook URLs used to be in `Reports/TelemetryClient.cs`, a
public-repository scanner found them on 2026-11-15, and the service deleted both permanently. A credential in
a repository is a leaked credential, and it also ships inside every binary built from that source.

Every one of those shows the exact payload before sending, and each is off unless you press it.

**It reads files it did not write.** The driver advisory inspects driver version strings from the PnP
manager, and the interrupt check reads performance counters. A malformed device name or driver string
must never cause more than a parse failure; report it if it does.

## What is out of scope

- Changes the app makes correctly, on a machine where the user pressed a button that said what it would
  do. Those are documented behaviour, and every one is revertible - see `undo-all.ps1`.
- Anything requiring an attacker who already has administrator rights.
- The default permissions of `%ProgramData%\Stutter Guard\`. The journal and backups live there so that
  `undo-all.ps1` works in Safe Mode without the app; treat that directory as security-relevant and say
  so if you think its permissions are wrong.

## Recovering a broken machine

This is a security-adjacent concern worth stating plainly: if a change leaves Windows unusable, boot
into Safe Mode and run `%ProgramData%\Stutter Guard\undo-all.ps1`. It restores the recorded originals
and needs neither the app nor this repository.
