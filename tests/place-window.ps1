<#
.SYNOPSIS
  Moves a test instance's game window to tile slot -Index (0 = left), so instances sit side by side.

.DESCRIPTION
  Unity has no command-line option for the window position. This waits for the process's main window, then puts it
  in column -Index of the primary screen's work area (wrapping to a new row when the columns run out). Unity moves
  its window again at unpredictable points while it starts up, so the slot is held until the game exits.
#>
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][int]$Index,
    [int]$TimeoutSeconds = 90
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SosWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$handle = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { exit 1 }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $p.MainWindowHandle; break }
    Start-Sleep -Milliseconds 250
}
if ($handle -eq [IntPtr]::Zero) { exit 1 }

$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$SWP_NOSIZE = 0x0001; $SWP_NOZORDER = 0x0004; $SWP_NOACTIVATE = 0x0010
# Unity moves and resizes its window (and can replace it) at several points during startup: keep the window in its
# slot until the game exits, moving it only when it drifted.
while ($true) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { exit 0 }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $p.MainWindowHandle }
    $r = New-Object SosWin+RECT
    if (-not [SosWin]::GetWindowRect($handle, [ref]$r)) { Start-Sleep -Seconds 1; continue }
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $cols = [Math]::Max(1, [Math]::Floor($area.Width / $w))
    $x = $area.Left + ($Index % $cols) * $w
    $y = $area.Top + [Math]::Floor($Index / $cols) * $h
    if ($r.Left -ne $x -or $r.Top -ne $y) {
        [void][SosWin]::SetWindowPos($handle, [IntPtr]::Zero, $x, $y, 0, 0, $SWP_NOSIZE -bor $SWP_NOZORDER -bor $SWP_NOACTIVATE)
    }
    Start-Sleep -Seconds 1
}
