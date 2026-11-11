namespace DpcTweaker.Core.Infra;

/// <summary>Outcome of a restore-point attempt.</summary>
public sealed record RestorePointResult(bool Ok, string Message, bool TurnedProtectionOn);

public static class RestorePoint
{
    private const string TweakId = "safety.restorepoint";

    /// <summary>
    /// Creates a System Restore point. Windows allows one per 24 hours by default, so the frequency
    /// limit is lowered for this one call and put back afterwards.
    /// <para>
    /// Turning System Protection on is a persistent system change, so it is journaled like any other:
    /// the "protection was off" state is written to the journal BEFORE the change is made, and
    /// undo-all.ps1 can turn protection back off. If no journal is supplied, protection is never
    /// turned on (only the already-reversible frequency value is touched).
    /// </para>
    /// </summary>
    public static RestorePointResult Create(string description, bool turnOnProtectionIfOff, Journal? journal = null)
    {
        string drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

        // 1. Try as-is. This changes nothing: the frequency tweak is restored inside the script.
        var first = Attempt(description, turnOn: false);
        if (first.Ok || !turnOnProtectionIfOff) return first;
        if (!first.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase) &&
            !first.Message.Contains("0x80070426", StringComparison.OrdinalIgnoreCase) &&
            !first.Message.Contains("0x8004230C", StringComparison.OrdinalIgnoreCase))
        {
            // Not a "protection is off" failure, so there is nothing to switch on.
            return first;
        }
        if (journal is null)
        {
            return first with
            {
                Message = first.Message + " (System Protection is off. Turning it on is a permanent change, " +
                          "so the app only does that from a screen that can record it in the journal.)",
            };
        }

        // 2. Record the before-state, then turn protection on. Write-ahead, like every other tweak.
        var txn = journal.BeginTransaction(TweakId);
        try
        {
            journal.RecordOriginal(TweakId, "restorepoint", drive, existed: false, null, "1");
        }
        catch (Exception ex)
        {
            journal.RollbackTransaction(txn);
            journal.CommitTransaction(txn);
            Log.Error("Restore point: couldn't record the System Protection change: " + ex.Message);
            return first with { Message = first.Message + " System Protection was left as it was: its state couldn't be journaled." };
        }

        var second = Attempt(description, turnOn: true);
        if (second.Ok)
        {
            journal.MarkApplied(TweakId);
            journal.CommitTransaction(txn);
            journal.Flush();
            Log.Info("System Protection was off on " + drive + "; turned it on and journaled it.");
            return second with { TurnedProtectionOn = true };
        }

        // Protection wasn't successfully turned on: drop the phantom entry.
        journal.RollbackTransaction(txn);
        journal.CommitTransaction(txn);
        journal.Flush();
        return second;
    }

    /// <summary>Puts System Protection back off for a drive the app turned it on for.</summary>
    public static bool DisableProtection(string drive)
    {
        try
        {
            var r = PowerShellBridge.RunJson($$"""
                try { Disable-ComputerRestore -Drive {{PowerShellBridge.Quote(drive + "\\")}} -ErrorAction Stop; '{"ok":true}' }
                catch { [pscustomobject]@{ ok = $false; message = $_.Exception.Message } | ConvertTo-Json -Compress }
                """, 120_000);
            bool ok = r.Bool("ok") ?? false;
            if (ok) Log.Info("System Protection turned back off on " + drive);
            else Log.Warn("Couldn't turn System Protection off on " + drive + ": " + (r.Str("message") ?? ""));
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("DisableProtection failed: " + ex.Message);
            return false;
        }
    }

    private static RestorePointResult Attempt(string description, bool turnOn)
    {
        string script = $$"""
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $old = (Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Type DWord -Value 0 -ErrorAction SilentlyContinue
            $ok = $false; $msg = ''
            try {
                if ({{(turnOn ? "$true" : "$false")}}) { Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue }
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
            return new RestorePointResult(ok, msg, false);
        }
        catch (Exception ex)
        {
            Log.Error("Restore point failed: " + ex.Message);
            return new RestorePointResult(false, ex.Message, false);
        }
    }
}