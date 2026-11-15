#Requires -Version 5.1
<#
.SYNOPSIS
  Controlled A/B of the two candidate background-affinity masks against interrupt cores.

.DESCRIPTION
  Real background load is bursty, so comparing masks by observation alone proves little.
  This drives a controlled load onto each mask in turn and measures scheduler wake latency
  (sg-latency-probe's metric) on the interrupt logical processors.

  Experiment 1 (mechanism / Q1): does load on the SMT SIBLING of an interrupt core degrade
  wake latency on that core? Conditions: idle, sibling loaded, unrelated core loaded.

  Experiment 2 (the A/B): the broken mask 18-31 vs the proposed 0-15;17;19;...;31, each with
  one load thread per logical processor, compared with no load at all.

  Read-only with respect to the OS: it sets affinity on its own threads/process only, generates
  its own load in user mode, and stops that load before exiting. No admin, no reboot.
#>
[CmdletBinding()]
param(
  [int]$Samples = 400,
  [int]$Repeats = 3,
  [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'WakeProbe.cs') -Raw) -Language CSharp
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'SpinLoad.cs')  -Raw) -Language CSharp

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'ab-results' }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$originalMask = [WakeProbe]::Mask()
$widen = ''
$null = [WakeProbe]::Widen([ref]$widen)

function Get-Stats {
  param([int]$Cpu)
  $pool = New-Object System.Collections.Generic.List[double]
  $note = ''
  for ($r = 0; $r -lt $Repeats; $r++) {
    $n = ''
    foreach ($v in [WakeProbe]::Measure($Cpu, $Samples, [ref]$n)) { $pool.Add($v) }
    if ($n) { $note = $n }
  }
  $cnt = $pool.Count
  if ($cnt -eq 0) { return [pscustomobject]@{ MedianUs=$null; P99Us=$null; MaxUs=$null; Over100Pct=$null; N=0; Note=$note } }
  $s = @($pool | Sort-Object)
  $at = { param($q) $s[[math]::Min($cnt-1, [int][math]::Floor($cnt*$q))] }
  [pscustomobject]@{
    MedianUs   = [math]::Round((& $at 0.50), 1)
    P99Us      = [math]::Round((& $at 0.99), 1)
    MaxUs      = [math]::Round($s[$cnt-1], 1)
    Over100Pct = [math]::Round(((@($s | Where-Object { $_ -gt 100 }).Count) / $cnt) * 100, 1)
    N          = $cnt
    Note       = $note
  }
}

function Get-LpUtil {
  param([int]$Cpu)
  try {
    $inst = "0,$Cpu"
    $s = Get-Counter "\Processor Information($inst)\% Processor Time" -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
    return [math]::Round($s.CounterSamples[0].CookedValue, 1)
  } catch { return $null }
}

$results = New-Object System.Collections.Generic.List[object]
function Add-Row {
  param($Exp, $Arm, $Cpu, $Mask, $LoadThreads, $LoadVerif, $Stats)
  $results.Add([pscustomobject]@{
    Experiment   = $Exp
    Arm          = $Arm
    ProbeCpu     = $Cpu
    LoadMask     = $Mask
    LoadThreads  = $LoadThreads
    LoadedLpUtil = $LoadVerif
    MedianUs     = $Stats.MedianUs
    P99Us        = $Stats.P99Us
    MaxUs        = $Stats.MaxUs
    Over100Pct   = $Stats.Over100Pct
    N            = $Stats.N
    Note         = $Stats.Note
  })
}

try {
  # ---------- Experiment 1: SMT sibling interference ----------
  Write-Output "=== Experiment 1: SMT sibling interference (probe on LP 26, the audio IRQ core) ==="
  $probeCpu = 26
  foreach ($cond in @(
      @{ Name='idle';            Mask=[uint64]0;                Threads=0 },
      @{ Name='sibling_loaded';  Mask=[uint64](1 -shl 27);      Threads=1 },
      @{ Name='othercore_loaded';Mask=[uint64](1 -shl 25);      Threads=1 }
  )) {
    $loaded = 0
    if ($cond.Threads -gt 0) { $loaded = [SpinLoad]::Start($cond.Mask, $cond.Threads) }
    Start-Sleep -Milliseconds 500
    $util = $null
    if ($cond.Threads -gt 0) {
      $lp = if ($cond.Mask -eq ([uint64](1 -shl 27))) { 27 } else { 25 }
      $util = Get-LpUtil -Cpu $lp
    }
    $st = Get-Stats -Cpu $probeCpu
    [SpinLoad]::Stop()
    Start-Sleep -Milliseconds 400
    $maskTxt = if ($cond.Mask -eq 0) { 'none' } else { ('0x{0:X}' -f $cond.Mask) }
    Write-Output ("  {0,-16} loadThreads={1,-2} loadedLpUtil={2,5}%  median={3,7}us p99={4,8}us max={5,9}us" -f $cond.Name,$loaded,$util,$st.MedianUs,$st.P99Us,$st.MaxUs)
    Add-Row -Exp 'E1-sibling' -Arm $cond.Name -Cpu $probeCpu -Mask $maskTxt -LoadThreads $loaded -LoadVerif $util -Stats $st
  }

  # ---------- Experiment 2: the mask A/B ----------
  Write-Output ""
  Write-Output "=== Experiment 2: mask A/B (one load thread per LP) ==="
  # Broken mask: 18-31
  $maskBroken = [uint64]0
  foreach ($c in 18..31) { $maskBroken = $maskBroken -bor ([uint64]1 -shl $c) }
  # Proposed mask: 0-15 plus the odd (non-interrupt) LPs
  $maskProposed = [uint64]0
  foreach ($c in 0..15) { $maskProposed = $maskProposed -bor ([uint64]1 -shl $c) }
  foreach ($c in @(17,19,21,23,25,27,29,31)) { $maskProposed = $maskProposed -bor ([uint64]1 -shl $c) }

  $probeCpus = @(16,20,22,26)
  foreach ($arm in @(
      @{ Name='none';     Mask=[uint64]0;            Threads=0 },
      @{ Name='B_18-31';  Mask=$maskBroken;          Threads=1 },
      @{ Name='A_proposed';Mask=$maskProposed;       Threads=1 }
  )) {
    $loaded = 0
    if ($arm.Threads -gt 0) { $loaded = [SpinLoad]::Start($arm.Mask, $arm.Threads) }
    Start-Sleep -Milliseconds 700
    $verify = $null
    if ($arm.Threads -gt 0) { $verify = Get-LpUtil -Cpu 26 }
    $line = "  {0,-12} threads={1,-2} LP26util={2,5}%  " -f $arm.Name, $loaded, $verify
    foreach ($pc in $probeCpus) {
      $st = Get-Stats -Cpu $pc
      $line += ("LP{0}: med={1} p99={2} max={3}  " -f $pc, $st.MedianUs, $st.P99Us, $st.MaxUs)
      $maskTxt = if ($arm.Mask -eq 0) { 'none' } else { ('0x{0:X}' -f $arm.Mask) }
      Add-Row -Exp 'E2-masks' -Arm $arm.Name -Cpu $pc -Mask $maskTxt -LoadThreads $loaded -LoadVerif $verify -Stats $st
    }
    [SpinLoad]::Stop()
    Start-Sleep -Milliseconds 500
    Write-Output $line
  }
}
finally {
  [SpinLoad]::Stop()
  $null = [WakeProbe]::SetMask($originalMask)
}

$csv = Join-Path $OutDir ("ab-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))
$results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Write-Output ""
Write-Output "CSV: $csv"
Write-Output ""
Write-Output "=== SUMMARY (pooled, microseconds of lateness beyond nominal 1 ms) ==="
$results | Sort-Object Experiment, Arm, ProbeCpu | Format-Table Experiment,Arm,ProbeCpu,LoadedLpUtil,MedianUs,P99Us,MaxUs,Over100Pct -AutoSize
