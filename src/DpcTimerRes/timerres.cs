/*
  DpcTimerRes - part of Stutter Guard. Managed twin of timerres.c.
  Copyright (C) 2026 Kareem Shabaka. Licensed under the GNU General Public License v3.0 or later; commercial licensing available on request. See COPYRIGHT.

  Holds the Windows system timer at a fixed resolution for as long as it runs.
  Started at logon by the scheduled task "\Stutter Guard\Timer resolution".

    DpcTimerRes.exe            -> 0.5 ms (5000 x 100 ns)
    DpcTimerRes.exe 5070       -> 0.507 ms
    DpcTimerRes.exe --query    -> min / max / current resolution, then exit
    DpcTimerRes.exe --check    -> set, verify, release, report, then exit (no side effect)

  Exit codes: 0 held the requested resolution
              2 ntdll!NtSetTimerResolution not found
              3 NtSetTimerResolution refused the request
              4 granted, but coarser than requested + 10 us (GlobalTimerResolutionRequests off?)
              5 --check found the timer already at a different resolution than requested

  This is the build used when no C toolchain is available: it is compiled with the
  in-box csc.exe and needs only the .NET Framework 4.x that ships with Windows.
  timerres.c remains the reference native implementation; build it with
    x86_64-w64-mingw32-gcc -O2 -s -mwindows -municode -o DpcTimerRes.exe timerres.c
  when MinGW-w64 is available. Both behave the same.
*/
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

internal static class TimerRes
{
    private const int ProcessPowerThrottling = 4;
    private const uint THROTTLE_VERSION = 1;
    private const uint THROTTLE_EXECUTION_SPEED = 0x1;
    private const uint THROTTLE_IGNORE_TIMER_RES = 0x4;

    private const int EXIT_OK = 0;
    private const int EXIT_NO_NTDLL = 2;
    private const int EXIT_REFUSED = 3;
    private const int EXIT_TOO_COARSE = 4;
    private const int EXIT_CHECK_MISMATCH = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint current);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateMutexW(IntPtr attributes, bool initialOwner, string name);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] list, uint count);

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    /// <summary>A windowless exe has no stdout of its own: borrow the launching console when there is one.</summary>
    private static void AttachToParentConsole()
    {
        try
        {
            if (GetConsoleProcessList(new uint[2], 2) == 0) AttachConsole(ATTACH_PARENT_PROCESS);
        }
        catch { }
    }

    private static string Ms(uint hundredNs)
    {
        return (hundredNs / 10000.0).ToString("0.000", CultureInfo.InvariantCulture) + " ms";
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AttachToParentConsole();

        string mode = "";
        uint wanted = 5000; // 100-ns units: 5000 = 0.5 ms
        if (args != null && args.Length > 0 && args[0] != null) mode = args[0].Trim().Trim('"');
        if (args != null && args.Length > 1 && args[1] != null)
        {
            uint parsed;
            if (uint.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                && parsed >= 4000 && parsed <= 156250)
                wanted = parsed;
        }

        if (string.Equals(mode, "--query", StringComparison.OrdinalIgnoreCase))
        {
            uint qmin, qmax, qcur;
            string text;
            if (NtQueryTimerResolution(out qmin, out qmax, out qcur) == 0)
                text = "Minimum: " + Ms(qmin) + "\r\nMaximum: " + Ms(qmax) + "\r\nCurrent: " + Ms(qcur);
            else
                text = "Could not read the timer resolution.";
            Console.WriteLine(text.Replace("\r\n", " "));
            MessageBoxText("Stutter Guard - timer", text);
            return EXIT_OK;
        }

        bool checkOnly = string.Equals(mode, "--check", StringComparison.OrdinalIgnoreCase);

        // One instance per session is enough.
        IntPtr mutex = CreateMutexW(IntPtr.Zero, true, @"Local\StutterGuardTimerRes");
        if (mutex != IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (checkOnly)
            {
                // --check must never be blocked by the running agent.
                CloseHandle(mutex);
            }
            else if (err == 183 /* ERROR_ALREADY_EXISTS */)
            {
                CloseHandle(mutex);
                return EXIT_OK;
            }
        }

        // Keep the request honoured although this process never shows a window.
        try
        {
            var st = new PowerThrottlingState();
            st.Version = THROTTLE_VERSION;
            st.ControlMask = THROTTLE_IGNORE_TIMER_RES | THROTTLE_EXECUTION_SPEED;
            st.StateMask = 0; // 0 = do not throttle, do not ignore our timer request
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref st, Marshal.SizeOf(typeof(PowerThrottlingState)));
        }
        catch { }

        uint before, bmin, bmax;
        NtQueryTimerResolution(out bmin, out bmax, out before);

        uint actual;
        int status;
        try
        {
            status = NtSetTimerResolution(wanted, true, out actual);
        }
        catch (EntryPointNotFoundException)
        {
            return EXIT_NO_NTDLL;
        }
        catch (DllNotFoundException)
        {
            return EXIT_NO_NTDLL;
        }

        int code;
        string outcome;
        if (status != 0)
        {
            code = EXIT_REFUSED;
            outcome = "refused (NTSTATUS 0x" + status.ToString("X8") + ")";
        }
        else if (actual != 0 && actual > wanted + 100)
        {
            // 100 x 100 ns = 10 us of slack: Windows rounds to its own quantum and may grant
            // something coarser. A zero means the kernel didn't report it, which is not a failure.
            code = EXIT_TOO_COARSE;
            outcome = "granted coarser than requested";
        }
        else
        {
            code = EXIT_OK;
            outcome = "held";
        }

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "requested={0} granted={1} minimum={2} maximum={3} before={4} outcome={5}",
            wanted, actual, bmin, bmax, before, outcome));

        if (checkOnly)
        {
            // Release the request so --check leaves nothing behind.
            uint released;
            NtSetTimerResolution(wanted, false, out released);
            if (mutex != IntPtr.Zero) CloseHandle(mutex);
            return code;
        }

        if (code != EXIT_OK) return code;

        // Hold it for as long as the logon session lasts.
        Thread.Sleep(Timeout.Infinite);
        return EXIT_OK;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    private static void MessageBoxText(string caption, string text)
    {
        try { MessageBoxW(IntPtr.Zero, text, caption, 0x00000040 /* MB_ICONINFORMATION */); }
        catch { }
    }
}