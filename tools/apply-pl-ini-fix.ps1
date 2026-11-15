#Requires -Version 5.1
<#
.SYNOPSIS
  Removes the harmful Process Lasso catch-all rule (and re-enables dynamic thread priority
  boost) by editing the active profile's prolasso.ini. MUST run elevated.

.DESCRIPTION
  Why this needs admin: on a stock install, config\**\prolasso.ini is owned by
  BUILTIN\Administrators with BUILTIN\Users granted only ReadAndExecute. The
  containing directory grants Users "Write", but for a directory that means
  create-only (FILE_ADD_FILE) - it does not permit modifying, renaming or deleting an
  existing Administrators-owned file. Verified by attempting all three.

  What it fixes
  -------------
  Stutter Guard's LassoRules.cs generated a catch-all entry `*.*,0,18-31`. CPUs 18-31 are
  exactly the cores the interrupt planner had reserved for device ISRs, so every ordinary
  process was forced onto the interrupt cores. Measured cost of that placement: median wake
  latency 782 us vs 8.9 us idle (about 90x). It also emitted `ThreadPriorityBoosts=...;0`
  for every game, which disables Windows' documented +6 mouse/keyboard and +8 sound wake
  boosts.

  Two variants
  ------------
    -Variant Remove    drop the `*.*` entry entirely. Windows then places processes itself;
                       it may still put a thread on an interrupt core, because the dispatcher
                       has no interrupt-load heuristic. Simplest, least surprising.
    -Variant SafeMask  keep a catch-all but point it at the 24 logical processors that are
                       NOT interrupt cores: 0-15 plus 17,19,21,23,25,27,29,31 (0xAAAAFFFF).
                       Strictly better for the stated goal; background keeps 24 LPs.

  Safety
  ------
  Timestamped backup is written before anything changes. Encoding is preserved
  (Process Lasso writes UTF-16LE with BOM; it also reads UTF-8/BE/ANSI). Only the two
  affected lines are touched, and every other line is verified byte-identical afterwards.
  Revert by copying the backup back, or by re-adding the rule in the Process Lasso GUI.

.PARAMETER Variant
  SafeMask (default) or Remove. SafeMask is preferred: a full mask leaves Windows free to

.PARAMETER DryRun
  Report exactly what would change, write nothing.
#>
[CmdletBinding()]
param(
  [ValidateSet('Remove','SafeMask')]
  [string]$Variant = 'SafeMask',
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ---------- elevation ----------
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
             [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host "This script must run elevated (right-click > Run as administrator)." -ForegroundColor Red
  Write-Host "The target INI is Administrators-owned and ReadAndExecute for Users."
  exit 2
}

$cfg  = 'C:\ProgramData\ProcessLasso\config'
$prof = Join-Path $cfg '.profile'

# ---------- resolve the ACTIVE profile ----------
# .profile is UTF-16LE with no BOM (20 bytes for "Game  Mode"), .profilename is ANSI.
$pb = [IO.File]::ReadAllBytes($prof)
$profileDirName = (New-Object System.Text.UnicodeEncoding($false,$false)).GetString($pb)
$ini = Join-Path (Join-Path $cfg $profileDirName) 'prolasso.ini'
if (-not (Test-Path -LiteralPath $ini)) { throw "Active profile INI not found: $ini" }
Write-Host "Active profile : '$profileDirName'"
Write-Host "Target INI     : $ini"

# ---------- check nothing is holding it ----------
if (Get-Process -Name ProcessLasso,ProcessGovernor,srvstub -ErrorAction SilentlyContinue) {
  Write-Host "Process Lasso looks like it is running. The docs say to exit the GUI first," -ForegroundColor Yellow
  Write-Host "otherwise its next save can overwrite this edit." -ForegroundColor Yellow
}
$gov = Get-Service ProcessGovernor -ErrorAction SilentlyContinue
if ($gov) { Write-Host "ProcessGovernor service: $($gov.Status) / $($gov.StartType)" }

# ---------- read, preserving encoding ----------
$bytes = [IO.File]::ReadAllBytes($ini)
if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
  $enc = New-Object System.Text.UnicodeEncoding($false,$true); $text = $enc.GetString($bytes,2,$bytes.Length-2); $encName='UTF-16LE+BOM'
} elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
  $enc = New-Object System.Text.UTF8Encoding($true); $text = $enc.GetString($bytes,3,$bytes.Length-3); $encName='UTF-8+BOM'
} else {
  $enc = New-Object System.Text.UTF8Encoding($false); $text = [Text.Encoding]::UTF8.GetString($bytes); $encName='UTF-8/ANSI'
}
$origLines = $text -split "`r`n", -1
Write-Host "Encoding       : $encName ($($bytes.Length) bytes, $($origLines.Count) lines)"

$lines = $origLines.Clone()
$changeLog = New-Object System.Collections.Generic.List[string]

# ---------- affinity rule ----------
$ai = -1
for ($i=0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*DefaultAffinitiesEx\s*=') { $ai = $i; break } }
if ($ai -lt 0) {
  Write-Host "No DefaultAffinitiesEx key in this profile - nothing to do there." -ForegroundColor Yellow
} else {
  $eq  = $lines[$ai].IndexOf('=')
  $val = $lines[$ai].Substring($eq+1)
  # fields are comma separated; a literal comma in a match is escaped as '\,'. Respect that.
  $tok = New-Object System.Collections.Generic.List[string]
  $sb  = New-Object System.Text.StringBuilder
  for ($i=0; $i -lt $val.Length; $i++) {
    $ch = $val[$i]
    if ($ch -eq '\' -and $i+1 -lt $val.Length) { [void]$sb.Append($ch); [void]$sb.Append($val[$i+1]); $i++; continue }
    if ($ch -eq ',') { $tok.Add($sb.ToString()); [void]$sb.Clear(); continue }
    [void]$sb.Append($ch)
  }
  $tok.Add($sb.ToString())

  if ($tok.Count % 3 -ne 0) { throw "DefaultAffinitiesEx has $($tok.Count) tokens, not a multiple of 3 - refusing to edit a shape I do not understand." }

  $kept = New-Object System.Collections.Generic.List[string]
  $found = $false
  for ($k=0; $k -lt $tok.Count; $k+=3) {
    $m = $tok[$k].Trim()
    if ($m -eq '*.*') {
      $found = $true
      $oldAff = "match='$m' delay=$($tok[$k+1]) affinity=$($tok[$k+2])"
      if ($Variant -eq 'Remove') {
        $changeLog.Add("DefaultAffinitiesEx: removed $oldAff")
        continue
      } else {
        $kept.Add($tok[$k]); $kept.Add($tok[$k+1]); $kept.Add('0-15;17;19;21;23;25;27;29;31')
        $changeLog.Add("DefaultAffinitiesEx: retargeted $oldAff -> affinity=0-15;17;19;21;23;25;27;29;31")
        continue
      }
    }
    $kept.Add($tok[$k]); $kept.Add($tok[$k+1]); $kept.Add($tok[$k+2])
  }
  if (-not $found) {
    Write-Host "No '*.*' catch-all entry present - already clean." -ForegroundColor Green
  } elseif ($kept.Count % 3 -ne 0) {
    throw "Post-edit token count $($kept.Count) is not a multiple of 3."
  } else {
    $lines[$ai] = $lines[$ai].Substring(0,$eq+1) + ($kept -join ',')
  }

  # ---- second pass: the measuring instrument must not sit on the interrupt cores ----
  # The profile pins latmon.exe to 16-31, which is every CCD1 core including all eight
  # interrupt cores. Measuring latency with the instrument sharing those cores is worthless.
  # latmon is not a game: it belongs on the game CCD (0-15), which is interrupt-free.
  $aff2 = $lines[$ai].Substring($lines[$ai].IndexOf('=')+1)
  $t2 = New-Object System.Collections.Generic.List[string]
  $sb2 = New-Object System.Text.StringBuilder
  for ($i=0; $i -lt $aff2.Length; $i++) {
    $ch = $aff2[$i]
    if ($ch -eq '\' -and $i+1 -lt $aff2.Length) { [void]$sb2.Append($ch); [void]$sb2.Append($aff2[$i+1]); $i++; continue }
    if ($ch -eq ',') { $t2.Add($sb2.ToString()); [void]$sb2.Clear(); continue }
    [void]$sb2.Append($ch)
  }
  $t2.Add($sb2.ToString())

  $irqMask = [long]0
  foreach ($c in 16,18,20,22,24,26,28,30) { $irqMask = $irqMask -bor ([long]1 -shl $c) }

  $warn = New-Object System.Collections.Generic.List[string]
  $changedLatmon = $false
  for ($k=0; $k -lt $t2.Count; $k+=3) {
    if ($k+2 -ge $t2.Count) { break }
    $nm = $t2[$k].Trim()
    $sp = $t2[$k+2].Trim()

    # decode the affinity spec to a bitmask
    $m = [long]0
    if ($sp -eq 'all') { $m = [long]0xFFFFFFFF }
    else {
      foreach ($pt in ($sp -split ';')) {
        $p = $pt.Trim(); if (-not $p) { continue }
        if ($p -match '^(\d+)-(\d+)$') { for ($c=[int]$Matches[1]; $c -le [int]$Matches[2]; $c++) { $m = $m -bor ([long]1 -shl $c) } }
        elseif ($p -match '^\d+$') { $m = $m -bor ([long]1 -shl [int]$p) }
      }
    }
    $ov = $m -band $irqMask
    if ($ov -eq 0) { continue }

    if ($nm -ieq 'latmon.exe') {
      $t2[$k+2] = '0-15'
      $changedLatmon = $true
      $changeLog.Add("DefaultAffinitiesEx: latmon.exe affinity $sp -> 0-15 (measuring instrument was on the interrupt cores)")
    }
    elseif ($nm -eq '*.*') {
      # already handled above; nothing extra
    }
    else {
      $ovList = (0..31 | Where-Object { $ov -band ([long]1 -shl $_) }) -join ','
      $warn.Add("'$nm' is pinned to $sp, which includes interrupt CPUs ($ovList). Left unchanged - check it is intentional.")
    }
  }
  if ($changedLatmon) { $lines[$ai] = $lines[$ai].Substring(0,$lines[$ai].IndexOf('=')+1) + ($t2 -join ',') }
  if ($warn.Count -gt 0) { foreach ($w in $warn) { Write-Host ("  WARN: " + $w) -ForegroundColor Yellow } }
}

# ---------- priority boosts ----------
$ti = -1
for ($i=0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*ThreadPriorityBoosts\s*=') { $ti = $i; break } }
if ($ti -ge 0) {
  $eq  = $lines[$ti].IndexOf('=')
  $val = $lines[$ti].Substring($eq+1)
  if ($val.Trim().Length -gt 0) {
    $flipped = 0
    $parts = $val -split ','
    for ($k=0; $k -lt $parts.Count; $k++) {
      $f = $parts[$k] -split ';'
      if ($f.Count -ge 2 -and $f[1].Trim() -eq '0') { $f[1] = '1'; $parts[$k] = ($f -join ';'); $flipped++ }
    }
    if ($flipped -gt 0) {
      $lines[$ti] = $lines[$ti].Substring(0,$eq+1) + ($parts -join ',')
      $changeLog.Add("ThreadPriorityBoosts: re-enabled dynamic boost on $flipped entries (0 -> 1)")
    }
  } else { Write-Host "ThreadPriorityBoosts is already empty - nothing to do." -ForegroundColor Green }
}

# ---------- report / dry run ----------
Write-Host ""
if ($changeLog.Count -eq 0) { Write-Host "Nothing to change. The profile is already clean." -ForegroundColor Green; exit 0 }
Write-Host "Planned changes ($Variant):"
$changeLog | ForEach-Object { Write-Host "  - $_" }
if ($DryRun) { Write-Host ""; Write-Host "DryRun: nothing written." -ForegroundColor Cyan; exit 0 }

# ---------- backup ----------
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$bkRoot = Join-Path (Join-Path 'C:\ProgramData\Stutter Guard' 'Backups') $stamp
New-Item -ItemType Directory -Path $bkRoot -Force | Out-Null
$bk = Join-Path $bkRoot 'prolasso.GameMode.before-pl-fix.ini'
Copy-Item -LiteralPath $ini -Destination $bk -Force
Write-Host ""
Write-Host "Backup         : $bk"

# ---------- write ----------
$newText = $lines -join "`r`n"
[IO.File]::WriteAllText($ini, $newText, $enc)

# ---------- verify ----------
$vb = [IO.File]::ReadAllBytes($ini)
$vt = if ($encName -like 'UTF-16LE*') { $enc.GetString($vb,2,$vb.Length-2) } else { [Text.Encoding]::UTF8.GetString($vb) }
$vl = $vt -split "`r`n", -1
$ok = $true
if ($Variant -eq 'Remove' -and $vt -match '(?m)^\s*DefaultAffinitiesEx\s*=.*\*\.\*') { $ok = $false }
if ($Variant -eq 'SafeMask' -and $vt -match '(?m)^\s*DefaultAffinitiesEx\s*=.*\*\.\*,0,18-31') { $ok = $false }
if ($vt -match '(?m)^\s*ThreadPriorityBoosts\s*=.*;0(,|$)') { $ok = $false }
$unexpected = 0
for ($i=0; $i -lt $origLines.Count; $i++) { if ($origLines[$i] -cne $vl[$i]) { $unexpected++ } }
Write-Host ""
if ($ok -and $unexpected -le $changeLog.Count) {
  Write-Host "VERIFIED: rules fixed; only $unexpected line(s) changed (expected <= $($changeLog.Count))." -ForegroundColor Green
} else {
  Write-Host "VERIFICATION FAILED (rulesOk=$ok, changedLines=$unexpected). Restoring backup." -ForegroundColor Red
  Copy-Item -LiteralPath $bk -Destination $ini -Force
  exit 1
}

Write-Host ""
Write-Host "Interrupt cores the catch-all no longer touches: 16,18,20,22,24,26,28,30" -ForegroundColor Cyan
Write-Host "Revert: copy '$bk' back over '$ini', or re-add the rule in the Process Lasso GUI."
Write-Host ""
Write-Host "Process Lasso should stay enabled: it is what keeps background off the interrupt cores."
Write-Host "If it is not running yet, start it so it loads the corrected rules:"
Write-Host "  Start-Service ProcessGovernor"
