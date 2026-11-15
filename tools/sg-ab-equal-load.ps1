#Requires -Version 5.1
<#
.SYNOPSIS
  Equal-load A/B: does placing background work on interrupt cores cost wake latency?

.DESCRIPTION
  The first attempt at this A/B put 24 load threads on the proposed mask and 14 on the broken
  one, so it compared unequal load. This version holds the thread count constant and varies
  ONLY which logical processors they occupy:

    Arm onIRQ  : 7 threads, one on each interrupt LP of the broken mask (18,20,22,24,26,28,30)
    Arm offIRQ : 7 threads on 7 non-interrupt LPs (0-6)
    Arm idle   : 0 threads

  Probe LPs: 20 and 26 (interrupt cores that Arm onIRQ loads and Arm offIRQ leaves alone), and
  16 (never loaded in either arm, as a control that should NOT move).

  Two independent repetitions are pooled, so a single unlucky run cannot carry the conclusion.
#>
[CmdletBinding()]
param(
  [int]$Samples = 400,
  [int]$Repeats = 3,
  [int]$Rounds = 2,
  [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'WakeProbe.cs') -Raw) -Language CSharp
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'SpinLoad.cs')  -Raw) -Language CSharp

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'ab-results' }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$originalMask = [WakeProbe]::Mask()
$null = [WakeProbe]::Widen([ref]$null) 2>$null
$null = [WakeProbe]::Widen([ref]([string]$w)) 2>$null

$maskOnIRQ  = [uint64]0
foreach ($c in @(18,20,22,24,26,28,30)) { $maskOnIRQ  = $maskOnIRQ  -bor ([uint64]1 -shl $c) }
$maskOffIRQ = [uint64]0
foreach ($c in @(0,1,2,3,4,5,6))        { $maskOffIRQ = $maskOffIRQ -bor ([uint64]1 -shl $c) }

function New-Pool { return ,(New-Object 'System.Collections.Generic.List[double]') }

function Get-LpUtil {
  param([int]$Cpu)
  try {
    $s = Get-Counter "\Processor Information(0,$Cpu)\% Processor Time" -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
    return [math]::Round($s.CounterSamples[0].CookedValue, 1)
  } catch { return $null }
}

$probeCpus = @(20, 26, 16)
$pools = @{}
foreach ($arm in @('onIRQ','offIRQ','idle')) {
  foreach ($pc in $probeCpus) {
    $pools["$arm|$pc"] = New-Object System.Collections.Generic.List[double]
  }
}
$verif = @{}

try {
  for ($round = 1; $round -le $Rounds; $round++) {
    # Rotate arm order per round so drift cannot favour one arm.
    $order = if ($round % 2 -eq 1) { @('idle','onIRQ','offIRQ') } else { @('offIRQ','onIRQ','idle') }
    foreach ($arm in $order) {
      $mask = switch ($arm) { 'onIRQ' { $maskOnIRQ } 'offIRQ' { $maskOffIRQ } default { [uint64]0 } }
      $nt = 0
      if ($mask -ne 0) { $nt = [SpinLoad]::Start($mask, 1) }
      Start-Sleep -Milliseconds 700

      # Prove the load actually landed where we asked.
      if ($mask -ne 0) {
        $u20 = Get-LpUtil -Cpu 20
        $u2  = Get-LpUtil -Cpu 2
        $verif[$arm] = "LP20=$u20% LP2=$u2%"
      } else { $verif[$arm] = 'no load' }

      Write-Output ("round {0} arm {1,-7} threads={2}  {3}" -f $round, $arm, $nt, $verif[$arm])
      foreach ($pc in $probeCpus) {
        for ($r = 0; $r -lt $Repeats; $r++) {
          $n = ''
          foreach ($v in [WakeProbe]::Measure($pc, $Samples, [ref]$n)) { $pools["$arm|$pc"].Add($v) }
        }
      }
      [SpinLoad]::Stop()
      Start-Sleep -Milliseconds 500
    }
  }
}
finally {
  [SpinLoad]::Stop()
  $null = [WakeProbe]::SetMask($originalMask)
}

$rows = @()
foreach ($arm in @('idle','offIRQ','onIRQ')) {
  foreach ($pc in $probeCpus) {
    $pool = $pools["$arm|$pc"]
    $cnt = $pool.Count
    if ($cnt -eq 0) { continue }
    $s = @($pool | Sort-Object)
    $at = { param($q) $s[[math]::Min($cnt-1, [int][math]::Floor($cnt*$q))] }
    $rows += [pscustomobject]@{
      Arm        = $arm
      ProbeCpu   = $pc
      N          = $cnt
      MedianUs   = [math]::Round((& $at 0.50), 1)
      P90Us      = [math]::Round((& $at 0.90), 1)
      P99Us      = [math]::Round((& $at 0.99), 1)
      MaxUs      = [math]::Round($s[$cnt-1], 1)
      Over100Pct = [math]::Round(((@($s | Where-Object { $_ -gt 100 }).Count) / $cnt) * 100, 1)
      Load       = $verif[$arm]
    }
  }
}

$csv = Join-Path $OutDir ("ab-equalload-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))
$rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Write-Output ""
Write-Output "=== EQUAL-LOAD A/B (7 load threads in both loaded arms; microseconds of lateness) ==="
$rows | Format-Table Arm,ProbeCpu,Load,N,MedianUs,P90Us,P99Us,MaxUs,Over100Pct -AutoSize
Write-Output "CSV: $csv"
