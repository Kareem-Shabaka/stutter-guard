#Requires -Version 5.1
<#
.SYNOPSIS
  Measures scheduler wake latency per logical processor (microseconds beyond a nominal 1 ms).

.DESCRIPTION
  Method: a high-resolution waitable timer with a 1 ms period, on a thread pinned to one LP.
  Each real wake interval is compared with 1000 us; the surplus is "lateness" -- the cost of a
  ready thread waiting for a CPU. Per the research notes this isolates the wake-to-run leg of
  interrupt-to-process latency, which is the dominant term and the only leg that affinity and
  priority settings control.

  Needs no administrator rights and no kernel ETW. Changes nothing on the system: it sets its
  own thread affinity, widens its own process mask if an external tool pinned it (and restores
  that mask afterwards), then exits.

.PARAMETER Cpus
  Logical processors to probe. Default: the interrupt cores plus two controls.

.PARAMETER Samples
  Wake-ups per repeat (each takes ~1 ms).

.PARAMETER Repeats
  Independent repeats per CPU, pooled.

.PARAMETER AsJson
  Emit JSON instead of a table.
#>
[CmdletBinding()]
param(
  [int[]]$Cpus = @(16,20,22,26,27),
  [int]$Samples = 400,
  [int]$Repeats = 3,
  [string]$Label = '',
  [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'WakeProbe.cs'
if (-not (Test-Path $src)) { throw "WakeProbe.cs not found next to this script: $src" }
Add-Type -TypeDefinition (Get-Content $src -Raw) -Language CSharp

# Remember the mask we inherit, so a pin applied by another tool can be put back.
$originalMask = [WakeProbe]::Mask()
$widenNote = ''
$null = [WakeProbe]::Widen([ref]$widenNote)

$rows = @()
try {
  foreach ($cpu in $Cpus) {
    $pool = New-Object System.Collections.Generic.List[double]
    $note = ''
    for ($r = 0; $r -lt $Repeats; $r++) {
      $n = ''
      $vals = [WakeProbe]::Measure($cpu, $Samples, [ref]$n)
      if ($n) { $note = $n }
      foreach ($v in $vals) { $pool.Add($v) }
    }
    $cnt = $pool.Count
    if ($cnt -eq 0) {
      $rows += [pscustomobject]@{ Cpu=$cpu; N=0; MedianUs=$null; P90Us=$null; P99Us=$null; P999Us=$null; MaxUs=$null; Over100Pct=$null; Note=$note }
      continue
    }
    $s = @($pool | Sort-Object)
    $at = { param($q) $s[[math]::Min($cnt-1, [int][math]::Floor($cnt*$q))] }
    $rows += [pscustomobject]@{
      Cpu        = $cpu
      N          = $cnt
      MedianUs   = [math]::Round((& $at 0.50), 1)
      P90Us      = [math]::Round((& $at 0.90), 1)
      P99Us      = [math]::Round((& $at 0.99), 1)
      P999Us     = [math]::Round((& $at 0.999), 1)
      MaxUs      = [math]::Round($s[$cnt-1], 1)
      Over100Pct = [math]::Round(((@($s | Where-Object { $_ -gt 100 }).Count) / $cnt) * 100, 1)
      Note       = $note
    }
  }
}
finally {
  # Put back whatever mask we were handed.
  $null = [WakeProbe]::SetMask($originalMask)
}

if ($AsJson) {
  [pscustomobject]@{
    Label       = $Label
    When        = (Get-Date).ToString('o')
    OriginalMask= ('0x{0:X}' -f $originalMask)
    WidenNote   = $widenNote
    Rows        = $rows
  } | ConvertTo-Json -Depth 6
}
else {
  Write-Output ""
  Write-Output ("[{0}] wake latency, nominal 1 ms, {1} wake-ups per LP (inherited mask 0x{2:X}, {3})" -f $Label, ($Samples*$Repeats), $originalMask, $widenNote)
  $rows | Format-Table -AutoSize
}
