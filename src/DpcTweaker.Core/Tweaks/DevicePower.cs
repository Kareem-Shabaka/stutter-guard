namespace DpcTweaker.Core.Tweaks;

using DpcTweaker.Core.Infra;

/// <summary>
/// "Allow the computer to turn off this device to save power", for every device:
/// WMI MSPower_DeviceEnable plus PnPCapabilities = 24 on physical network adapters.
/// Same logic as disable-device-powersave.ps1 (which ran on SHABAKA-B4 since Oct 12).
/// "Allow this device to wake the computer" is never touched.
/// </summary>
public static class DevicePower
{
    public static string BackupCsv => Path.Combine(Paths.DevicePowerDir, "backup.csv");

    private static string Header => $"$dir = {PowerShellBridge.Quote(Paths.DevicePowerDir)}\n$backup = {PowerShellBridge.Quote(BackupCsv)}\n";

    public static string ApplyScript => Header + """
        $ErrorActionPreference = 'SilentlyContinue'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $changed = 0; $already = 0; $failed = 0; $bk = @()
        $wmi = @(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable)
        foreach ($d in $wmi) {
            if ($d.Enable -eq $false) { $already++; continue }
            try {
                Set-CimInstance -InputObject $d -Property @{ Enable = $false } -ErrorAction Stop
                $changed++
                $bk += [pscustomobject]@{ Kind = 'wmi'; Id = $d.InstanceName; Old = 'True' }
            } catch { $failed++ }
        }
        $nicClass = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
        foreach ($nic in @(Get-NetAdapter -Physical)) {
            $key = Get-ChildItem $nicClass | Where-Object { (Get-ItemProperty $_.PSPath).NetCfgInstanceId -eq $nic.InterfaceGuid } | Select-Object -First 1
            if ($key) {
                $old = (Get-ItemProperty $key.PSPath).PnPCapabilities
                if ($old -ne 24) {
                    Set-ItemProperty -Path $key.PSPath -Name PnPCapabilities -Type DWord -Value 24
                    $bk += [pscustomobject]@{ Kind = 'nic'; Id = $key.PSPath; Old = $(if ($null -eq $old) { '' } else { [string]$old }) }
                    $changed++
                } else { $already++ }
            }
        }
        if ($bk.Count -gt 0) {
            if (Test-Path $backup) { $bk | Export-Csv -Path $backup -NoTypeInformation -Encoding UTF8 -Append }
            else { $bk | Export-Csv -Path $backup -NoTypeInformation -Encoding UTF8 }
        }
        [pscustomobject]@{ changed = $changed; already = $already; failed = $failed; total = $wmi.Count } | ConvertTo-Json -Compress
        """;

    public static string RestoreScript => Header + """
        $ErrorActionPreference = 'SilentlyContinue'
        $n = 0
        if (Test-Path $backup) {
            $rows = Import-Csv $backup
            $wmi = @(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable)
            foreach ($r in $rows) {
                if ($r.Kind -eq 'wmi') {
                    $obj = $wmi | Where-Object { $_.InstanceName -eq $r.Id } | Select-Object -First 1
                    if ($obj) { Set-CimInstance -InputObject $obj -Property @{ Enable = $true }; $n++ }
                } elseif ($r.Kind -eq 'nic') {
                    if ($r.Old -eq '') { Remove-ItemProperty -Path $r.Id -Name PnPCapabilities }
                    else { Set-ItemProperty -Path $r.Id -Name PnPCapabilities -Type DWord -Value ([int]$r.Old) }
                    $n++
                }
            }
            Remove-Item $backup -Force
        }
        [pscustomobject]@{ restored = $n } | ConvertTo-Json -Compress
        """;

    private const string QueryScript = """
        $w = @(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue)
        [pscustomobject]@{ total = $w.Count; allowed = @($w | Where-Object { $_.Enable -eq $true }).Count } | ConvertTo-Json -Compress
        """;

    public static (int Changed, int Already, int Failed, int Total) ApplyNow()
    {
        var r = PowerShellBridge.RunJson(ApplyScript, 180_000);
        var result = ((int)(r.Num("changed") ?? 0), (int)(r.Num("already") ?? 0), (int)(r.Num("failed") ?? 0), (int)(r.Num("total") ?? 0));
        Log.Info($"Device power saving: {result.Item1} unticked, {result.Item2} already off, {result.Item3} failed, {result.Item4} devices with the box");
        return result;
    }

    public static int RestoreFromBackup()
    {
        var r = PowerShellBridge.RunJson(RestoreScript, 180_000);
        int n = (int)(r.Num("restored") ?? 0);
        Log.Info($"Device power saving restored on {n} devices");
        return n;
    }

    private static readonly object CacheGate = new();
    private static (DateTime When, int Total, int Allowed)? _cache;

    /// <summary>How many devices still allow power-down. Cached for a minute: it costs a PowerShell call.</summary>
    public static (int Total, int Allowed)? Query(bool force = false)
    {
        lock (CacheGate)
        {
            if (!force && _cache is { } c && DateTime.Now - c.When < TimeSpan.FromSeconds(60)) return (c.Total, c.Allowed);
        }
        try
        {
            var r = PowerShellBridge.RunJson(QueryScript, 60_000);
            var v = (DateTime.Now, (int)(r.Num("total") ?? 0), (int)(r.Num("allowed") ?? 0));
            lock (CacheGate) _cache = v;
            return (v.Item2, v.Item3);
        }
        catch (Exception ex)
        {
            Log.Warn("Device power query failed: " + ex.Message);
            return null;
        }
    }

    public static void Invalidate()
    {
        lock (CacheGate) _cache = null;
    }
}
