namespace DpcTweaker.Core.Infra;

public static class RestorePoint
{
    /// <summary>
    /// Creates a System Restore point. Windows allows one per 24 hours by default, so the
    /// frequency limit is lowered for this one call and put back afterwards.
    /// </summary>
    public static (bool Ok, string Message) Create(string description, bool turnOnProtectionIfOff)
    {
        string script = $$"""
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $old = (Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Type DWord -Value 0 -ErrorAction SilentlyContinue
            $ok = $false; $msg = ''
            try {
                if ({{(turnOnProtectionIfOff ? "$true" : "$false")}}) { Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue }
                Checkpoint-Computer -Description {{PowerShellBridge.Quote(description)}} -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
                $ok = $true; $msg = 'Restore point created.'
            } catch {
                $msg = $_.Exception.Message
            }
            if ($null -eq $old) { Remove-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Type DWord -Value $old }
            [pscustomobject]@{ ok = $ok; message = $msg } | ConvertTo-Json -Compress
            """;
        try
        {
            var r = PowerShellBridge.RunJson(script, 300_000);
            bool ok = r.Bool("ok") ?? false;
            string msg = r.Str("message") ?? "";
            if (!ok && msg.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                msg = "System Protection is off for " + Environment.GetEnvironmentVariable("SystemDrive") + ". " + msg;
            Log.Info("Restore point: " + (ok ? "created" : "failed: " + msg));
            return (ok, msg);
        }
        catch (Exception ex)
        {
            Log.Error("Restore point failed: " + ex.Message);
            return (false, ex.Message);
        }
    }
}
