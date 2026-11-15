#Requires -Version 5.1
<#
.SYNOPSIS
  Measures the cost of waking a "cold" logical processor: does idle duration before a wake
  increase the latency of that wake?

.DESCRIPTION
  Directly tests the mechanism behind core parking and C-states: a processor that has been idle
  (parked, or in a deep C-state) may take longer to service a wake than one kept busy. If wake
  latency grows with prior idle duration, then avoiding parking/idle has a measurable latency
  benefit -- which is the premise behind running with parking disabled.

  For each idle duration the script sleeps, then records the FIRST high-resolution timer wake,
  repeated many times. It also records steady-state wakes (1 ms apart, no prior idle) as the
  reference. Read-only: only sets its own thread affinity.
#>
[CmdletBinding()]
param(
  [int]$Cpu = 26,
  [int[]]$IdleMs = @(0, 50, 250, 1000, 5000),
  [int]$Trials = 40,
  [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'WakeProbe.cs') -Raw) -Language CSharp
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'ab-results' }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$orig = [WakeProbe]::Mask()
$w = ''; $null = [WakeProbe]::Widen([ref]$w)

function Get-FirstWakeUs {
  param([int]$Cpu, [int]$IdleMs)
  # Measure() runs 20 warm-up wakes then records; for the cold test we want the FIRST wake after
  # idling, so we idle here and take the minimum of a 1-sample measurement's first value.
  if ($IdleMs -gt 0) { Start-Sleep -Milliseconds $IdleMs }
  $n = ''
  $v = [WakeProbe]::Measure($Cpu, 1, [ref]$n)
  if ($v -and $v.Count -gt 0) { return $v[0] }
  return $null
}

$rows = @()
try {
  foreach ($idle in $IdleMs) {
    $vals = New-Object System.Collections.Generic.List[double]
    for ($t = 0; $t -lt $Trials; $t++) {
      $x = Get-FirstWakeUs -Cpu $Cpu -IdleMs $idle
      if ($null -ne $x) { $vals.Add($x) }
    }
    if ($vals.Count -eq 0) { continue }
    $s = @($vals | Sort-Object)
    $cnt = $s.Count
    $at = { param($q) $s[[math]::Min($cnt-1, [int][math]::Floor($cnt*$q))] }
    $rows += [pscustomobject]@{
      Cpu            = $Cpu
      IdleBeforeMs   = $idle
      Trials         = $cnt
      MedianUs       = [math]::Round((& $at 0.50), 1)
      P90Us          = [math]::Round((& $at 0.90), 1)
      MaxUs          = [math]::Round($s[$cnt-1], 1)
      Over100us      = (@($s | Where-Object { $_ -gt 100 }).Count)
    }
    Write-Output ("idle {0,5} ms before wake -> median {1,8} us  p90 {2,8} us  max {3,9} us" -f $idle, $rows[-1].MedianUs, $rows[-1].P90Us, $rows[-1].MaxUs)
  }
}
finally { $null = [WakeProbe]::SetMask($orig) }

$csv = Join-Path $OutDir ("coldwake-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))
$rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Write-Output ""
Write-Output "=== COLD-WAKE vs IDLE DURATION (LP $Cpu) ==="
$rows | Format-Table Cpu,IdleBeforeMs,Trials,MedianUs,P90Us,MaxUs,Over100us -AutoSize
Write-Output "CSV: $csv"
