<#
.SYNOPSIS
  Starts one test instance of the game on a hidden desktop, so it can't get in the way of the user's work.

.DESCRIPTION
  Used by tests/launch.sh (background mode, the default). The game's arguments come from the SOS_GAME_ARGS
  environment variable (PowerShell would read arguments starting with "-" as its own parameters). The process id is
  written to -PidFile as soon as the game has started; the script then stays until the game exits.

  The game runs on its own Win32 desktop (CreateDesktop "SosOnlineTests" in the user's window station, never switched
  to), like a virtual desktop nobody looks at:
  - its windows never show on the user's desktop or taskbar;
  - it can't take the foreground there: Unity activates its window on start, which on the user's desktop took the
    focus from whatever the user was doing (moving the window off-screen and handing the focus back afterwards
    didn't always work: Windows refuses SetForegroundWindow from a background process);
  - keyboard and mouse input only ever go to the desktop on screen, so the user's typing can't reach it (pads are
    read regardless of desktop: the mod's --sosonline-background ignores them).
  Unity renders as usual there (screenshots work). Windows 11's background throttling (EcoQoS, timer resolution) is
  turned off for the process, so it runs at full speed with its 60 fps cap.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$PidFile
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SosBg {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] public struct THROTTLE { public uint Version, ControlMask, StateMask; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref THROTTLE info, int size);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

    public const string Desktop = "SosOnlineTests";
    static IntPtr _desktop, _process;

    /// Starts the game on the hidden desktop (created, or opened when another instance already made it). Returns the pid.
    public static int Start(string exe, string args, string dir) {
        _desktop = CreateDesktopW(Desktop, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /* GENERIC_ALL */, IntPtr.Zero);
        if (_desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        si.lpDesktop = @"WinSta0\" + Desktop;
        var cmd = new System.Text.StringBuilder("\"" + exe + "\" " + args);
        PROCESS_INFORMATION pi;
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out pi))
            throw new System.ComponentModel.Win32Exception();
        // PROCESS_POWER_THROTTLING_EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, both off.
        var t = new THROTTLE { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        SetProcessInformation(pi.hProcess, 4 /* ProcessPowerThrottling */, ref t, Marshal.SizeOf(t));
        CloseHandle(pi.hThread);
        _process = pi.hProcess;
        return pi.dwProcessId;
    }

    /// Waits for the game to exit; the desktop goes away with its last process and handle.
    public static void Wait() {
        WaitForSingleObject(_process, 0xFFFFFFFF);
        CloseHandle(_process);
        CloseDesktop(_desktop);
    }
}
"@

$gamePid = [SosBg]::Start($Exe, $env:SOS_GAME_ARGS, (Split-Path -Parent $Exe))
Set-Content -Path $PidFile -Value $gamePid -Encoding ascii
[SosBg]::Wait()
