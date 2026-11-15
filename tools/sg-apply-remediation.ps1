#Requires -Version 5.1
<#
.SYNOPSIS
  Applies the researched fix to this machine: removes the two harmful generated Process Lasso
  entries, snapshots and resets stale process affinities, and verifies before/after.

.DESCRIPTION
  1. Snapshots every readable process's affinity to JSON (full revert source).
  2. Backs up the active Process Lasso profile INI, timestamped, into
     C:\ProgramData\Stutter Guard\Backups\<stamp>\ .
  3. Removes the '*.*' catch-all entry from DefaultAffinitiesEx (it lands on the interrupt cores)
     and empties ThreadPriorityBoosts (it disables Windows' +6 input / +8 sound wake boosts).
     Game->CCD0 entries are left intact; they are correct.
  4. Resets stale non-default affinities on accessible user processes back to the system mask.
  5. Re-reads the INI to prove the edit took, and restores the backup if it did not.

  Every step is reversible: the JSON snapshot restores affinities, the INI backup restores rules.
#>
[CmdletBinding()]
param(
  [switch]$SkipProcessReset,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$sgData  = 'C:\ProgramData\Stutter Guard'
$backup  = Join-Path $sgData "Backups\$stamp"
$profDir = 'C:\ProgramData\ProcessLasso\config\Game  Mode'
$profIni = Join-Path $profDir 'prolasso.ini'
$outDir  = Join-Path $PSScriptRoot 'ab-results'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

Write-Output "=== REMEDIATION $stamp ==="

# ---------- 1. Snapshot process affinities ----------
$snapshot = @()
foreach ($p in Get-Process) {
  try {
    $a = [int64]$p.ProcessorAffinity
    if ($a -ne 0) { $snapshot += [pscustomobject]@{ Name=$p.Name; Id=$p.Id; Affinity=('0x{0:X}' -f $a) } }
  } catch { }
}
$snapFile = Join-Path $outDir "affinity-snapshot-$stamp.json"
$snapshot | ConvertTo-Json -Depth 3 | Set-Content -Path $snapFile -Encoding UTF8
Write-Output "1) affinity snapshot: $($snapshot.Count) processes -> $snapFile"

# The real system mask: every logical processor. NOT the current process mask, which is itself
# whatever stale pin was left behind (0xFFFC0000 on this box) -- using it would re-apply the bug.
$sysMask = ([int64]1 -shl [Environment]::ProcessorCount) - 1
Write-Output ("   system mask = 0x{0:X} ({1} logical processors)" -f $sysMask, [Environment]::ProcessorCount)

# ---------- 2. Back up the INI ----------
if (-not (Test-Path $backup)) { New-Item -ItemType Directory -Path $backup -Force | Out-Null }
if (Test-Path $profIni) {
  Copy-Item $profIni (Join-Path $backup 'prolasso.GameMode.ini') -Force
  Copy-Item (Join-Path $profDir '..\.profile') (Join-Path $backup 'dot-profile') -Force -ErrorAction SilentlyContinue
  Write-Output "2) backed up: $backup\prolasso.GameMode.ini"
} else { Write-Output "2) WARNING: profile ini not found at $profIni" }

# ---------- 3. Edit the INI ----------
function Read-IniText {
  param([string]$Path)
  $bytes = [System.IO.File]::ReadAllBytes($Path)
  if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { return [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length-2) }
  if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length-3) }
  return [System.Text.Encoding]::UTF8.GetString($bytes)
}

$iniStatus = 'skipped'
try {
if ((Test-Path $profIni) -and -not $DryRun) {
  $text  = Read-IniText -Path $profIni
  $lines = $text -split "`r?`n"
  $changed = @()

  for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^\s*DefaultAffinitiesEx\s*=') {
      # Drop only the "*.*" catch-all entry; keep every game->CCD0 entry.
      $eq = $line.IndexOf('=')
      $val = $line.Substring($eq + 1)
      $parts = $val -split ','
      $kept = New-Object System.Collections.Generic.List[string]
      for ($k = 0; $k -lt $parts.Count; $k++) {
        if ($parts[$k].Trim() -eq '*.*') {
          # entry form is match,group,mask  -> skip 3 tokens
          $k += 2
          $changed += 'removed *.* catch-all'
          continue
        }
        $kept.Add($parts[$k])
      }
      $newVal = ($kept -join ',')
      $lines[$i] = $line.Substring(0, $eq + 1) + $newVal
    }
    elseif ($line -match '^\s*ThreadPriorityBoosts\s*=') {
      $eq = $line.IndexOf('=')
      $lines[$i] = $line.Substring(0, $eq + 1)
      $changed += 'cleared ThreadPriorityBoosts'
    }
  }

  $newText = ($lines -join "`r`n")
  [System.IO.File]::WriteAllText($profIni, $newText, (New-Object System.Text.UTF8Encoding($false)))
  Write-Output "3) edited INI: $($changed -join '; ')"

  # verify
  $verify = Read-IniText -Path $profIni
  $stillCatchAll = ($verify -match '(?m)^\s*DefaultAffinitiesEx\s*=.*\*\s*\.\s*\*')
  $stillBoost    = ($verify -match '(?m)^\s*ThreadPriorityBoosts\s*=\s*\S')
  if ($stillCatchAll -or $stillBoost) {
    Copy-Item (Join-Path $backup 'prolasso.GameMode.ini') $profIni -Force
    Write-Output "3) VERIFY FAILED (catchAll=$stillCatchAll boost=$stillBoost) -> INI restored from backup"
  } else {
    Write-Output "3) verify OK: no catch-all, no threadPriorityBoosts entries"
    $iniStatus = 'edited'
  }
}
} catch {
  $iniStatus = 'NEEDS-ELEVATION'
  Write-Output "3) could not edit the profile INI: $($_.Exception.Message)"
  Write-Output "   -> expected on a stock install (the file is ReadAndExecute for Users)."
  Write-Output "   -> this edit belongs in Stutter Guard itself, which already runs elevated."
}

# ---------- 4. Reset stale affinities ----------
if (-not $SkipProcessReset -and -not $DryRun) {
  $irqLps = @(16,18,20,22,24,26,28,30)
  $irqMask = [int64]0
  foreach ($c in $irqLps) { $irqMask = $irqMask -bor ([int64]1 -shl $c) }
  $moved = 0; $failed = 0
  foreach ($p in Get-Process) {
    try {
      $a = [int64]$p.ProcessorAffinity
      if ($a -eq 0) { continue }
      if (($a -band $irqMask) -ne 0) {
        try { $p.ProcessorAffinity = [IntPtr]$sysMask; $moved++ }
        catch { $failed++ }
      }
    } catch { }
  }
  Write-Output "4) reset affinity on $moved processes overlapping interrupt cores ($failed refused)"
} else { Write-Output "4) process reset skipped" }

Write-Output ""
Write-Output "REVERT:"
Write-Output "  rules     : copy '$(Join-Path $backup 'prolasso.GameMode.ini')' back to '$profIni'"
Write-Output "  affinities: source '$snapFile' (Name/Id/Affinity); restarting each process also clears it"


