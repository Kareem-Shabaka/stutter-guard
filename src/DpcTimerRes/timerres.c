/*
  DpcTimerRes.exe - part of Stutter Guard.
  Copyright (C) 2026 Kareem Shabaka. Licensed under GPL-3.0 with Non-Commercial Rider.

  Holds the Windows system timer at a fixed resolution for as long as it runs.
  Started at logon by the scheduled task "\Stutter Guard\Timer resolution".

    DpcTimerRes.exe            -> 0.5 ms (5000 x 100 ns)
    DpcTimerRes.exe 5070       -> 0.507 ms
    DpcTimerRes.exe --query    -> prints min / max / current resolution and exits

  Exit codes: 0 held the requested resolution
              2 ntdll!NtSetTimerResolution not found
              3 NtSetTimerResolution refused the request
              4 granted, but coarser than requested + 10 us (GlobalTimerResolutionRequests off?)

  Needs GlobalTimerResolutionRequests = 1 (Windows 11 / Server 2022+) for the request
  to apply system-wide; otherwise Windows 10 2004+ applies it to this process only.

  It also opts out of Windows 11's "ignore timer requests from hidden windows"
  behaviour (PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION), because this
  process never has a visible window.

  Build (cloud sandbox or any MinGW-w64):
    x86_64-w64-mingw32-gcc -O2 -s -mwindows -municode -o DpcTimerRes.exe timerres.c
*/
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>
#include <stdlib.h>

typedef LONG (NTAPI *NtSetTimerResolution_t)(ULONG DesiredResolution, BOOLEAN SetResolution, PULONG CurrentResolution);
typedef LONG (NTAPI *NtQueryTimerResolution_t)(PULONG MinimumResolution, PULONG MaximumResolution, PULONG CurrentResolution);
typedef BOOL (WINAPI *SetProcessInformation_t)(HANDLE, int, LPVOID, DWORD);

#define DPC_PROCESS_POWER_THROTTLING   4      /* PROCESS_INFORMATION_CLASS ProcessPowerThrottling */
#define DPC_THROTTLE_CURRENT_VERSION   1
#define DPC_THROTTLE_EXECUTION_SPEED   0x1
#define DPC_THROTTLE_IGNORE_TIMER_RES  0x4

typedef struct {
    ULONG Version;
    ULONG ControlMask;
    ULONG StateMask;
} DPC_POWER_THROTTLING_STATE;

static void query_and_show(NtQueryTimerResolution_t q)
{
    ULONG mn = 0, mx = 0, cur = 0;
    wchar_t text[256];
    if (q && q(&mn, &mx, &cur) == 0) {
        swprintf(text, 256, L"Minimum: %.4f ms\nMaximum: %.4f ms\nCurrent: %.4f ms",
                 mn / 10000.0, mx / 10000.0, cur / 10000.0);
    } else {
        swprintf(text, 256, L"Could not read the timer resolution.");
    }
    MessageBoxW(NULL, text, L"Stutter Guard - timer", MB_OK | MB_ICONINFORMATION);
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, PWSTR cmdLine, int nShow)
{
    (void)hInst; (void)hPrev; (void)nShow;

    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    NtSetTimerResolution_t setRes = (NtSetTimerResolution_t)(void *)GetProcAddress(ntdll, "NtSetTimerResolution");
    NtQueryTimerResolution_t queryRes = (NtQueryTimerResolution_t)(void *)GetProcAddress(ntdll, "NtQueryTimerResolution");
    if (!setRes) return 2;

    ULONG wanted = 5000; /* 100-ns units: 5000 = 0.5 ms */
    if (cmdLine && *cmdLine) {
        while (*cmdLine == L' ' || *cmdLine == L'"') cmdLine++;
        if (wcsncmp(cmdLine, L"--query", 7) == 0) { query_and_show(queryRes); return 0; }
        long v = wcstol(cmdLine, NULL, 10);
        if (v >= 4000 && v <= 156250) wanted = (ULONG)v;
    }

    /* One instance per session is enough. */
    HANDLE mutex = CreateMutexW(NULL, TRUE, L"Local\\StutterGuardTimerRes");
    if (mutex && GetLastError() == ERROR_ALREADY_EXISTS) return 0;

    /* Keep the request honoured although this process never shows a window. */
    SetProcessInformation_t spi = (SetProcessInformation_t)(void *)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "SetProcessInformation");
    if (spi) {
        DPC_POWER_THROTTLING_STATE st;
        st.Version = DPC_THROTTLE_CURRENT_VERSION;
        st.ControlMask = DPC_THROTTLE_IGNORE_TIMER_RES | DPC_THROTTLE_EXECUTION_SPEED;
        st.StateMask = 0; /* 0 = do not throttle, do not ignore our timer request */
        spi(GetCurrentProcess(), DPC_PROCESS_POWER_THROTTLING, &st, sizeof(st));
    }

    /* The granted resolution is the only proof the request took effect. Read it back and fail
       with a distinct code instead of sitting forever on a request Windows quietly ignored. */
    ULONG actual = 0;
    LONG status = setRes(wanted, TRUE, &actual);
    if (status != 0) return 3;                 /* NTSTATUS failure: the request was refused */

    /* Windows rounds to its own quantum and may grant something coarser. 100 x 100 ns = 10 us
       of slack. A zero means the kernel didn't report it; don't invent a failure from that. */
    if (actual != 0 && actual > wanted + 100) return 4;

    for (;;) Sleep(INFINITE);
    return 0;
}
