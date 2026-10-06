# Exercises the tray icon's real right-click menu.
#
# A tray click cannot be scripted, but the click itself is only a window message:
# post WinForms' tray callback (WM_USER + 1024) with WM_RBUTTONUP in lParam to the
# NotifyIcon's message window and the app handles it exactly as a user would.
#
#   -Shot            photograph the open menu (CAPTUREBLT, so the layered window is
#                    included; the app's own capture deliberately excludes it)
#   -Row <name>      click a row for real, with SetCursorPos + mouse_event:
#                    Region | FullScreen | Exit | Outside
#
# Clicking a row is the only way to catch the class of bug where the menu dismisses
# itself on mouse-down and the row's Click never fires, so -Row is the check to run
# after touching TrayMenuWindow's dismissal logic.
#
# Usage: powershell -File scripts\probe-tray-menu.ps1 -Shot [-Out menu.png]
#        powershell -File scripts\probe-tray-menu.ps1 -Row Exit
param(
    [switch]$Shot,
    [ValidateSet('Region', 'FullScreen', 'Exit', 'Outside')][string]$Row,
    [string]$Out = (Join-Path $env:TEMP 'tangerine-tray-menu.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace TrayProbe -Name Native -MemberDefinition @'
public struct POINT { public int X; public int Y; }
public struct RECT { public int Left, Top, Right, Bottom; }
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll", SetLastError=true)]
public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
[DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
[DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
[DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
[DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
[DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObj);
[DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
'@

if (-not $Shot -and -not $Row) { throw "nothing to do: pass -Shot and/or -Row <name>" }

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'screenshot\bin\Release\net10.0-windows\screenshot.exe'
if (-not (Test-Path $exe)) { throw "build it first: dotnet build -c Release ($exe)" }
$log = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'Screenshots\screenshot-trace.log'

# Row centres in DIPs below the surface's top edge: 6 panel margin + 1 row margin +
# half of the 32-high row = 23, +34 per row, with the separator before the last row.
$rowCentre = @{ Region = 23.0; FullScreen = 57.0; Exit = 99.0 }

$saved = New-Object TrayProbe.Native+POINT
[TrayProbe.Native]::GetCursorPos([ref]$saved) | Out-Null

$before = (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 2000
$new = Get-Content $log | Select-Object -Skip $before

# ── Open the menu through the tray icon's own message ───────
$handle = [Convert]::ToInt64((($new | Select-String 'NotifyIcon.Visible=True, window=0x' | Select-Object -Last 1) -replace '.*window=0x', '').Trim(), 16)
$rectLine = ($new | Select-String 'shell holds it at' | Select-Object -Last 1).Line
$icon = [regex]::Match($rectLine, '\((-?\d+),(-?\d+)\) (\d+)x(\d+)')
$iconRight = [int]$icon.Groups[1].Value + [int]$icon.Groups[3].Value
$iconTop = [int]$icon.Groups[2].Value

$wmTrayMessage = 0x0400 + 1024   # WM_USER + 1024: WinForms' tray callback
[TrayProbe.Native]::PostMessage([IntPtr]$handle, $wmTrayMessage, [IntPtr]1, [IntPtr]0x0205) | Out-Null
Start-Sleep -Milliseconds 1000

# This process is DPI-unaware, so GetWindowRect and SetCursorPos speak the same
# virtualised pixel space — the two stay consistent with no scaling maths.
$script:menuRect = $null
$enum = [TrayProbe.Native+EnumProc]{
    param($h, $l)
    $owner = 0
    [TrayProbe.Native]::GetWindowThreadProcessId($h, [ref]$owner) | Out-Null
    if ($owner -eq $proc.Id -and [TrayProbe.Native]::IsWindowVisible($h)) {
        $r = New-Object TrayProbe.Native+RECT
        [TrayProbe.Native]::GetWindowRect($h, [ref]$r) | Out-Null
        if (($r.Right - $r.Left) -gt 50 -and ($r.Bottom - $r.Top) -gt 50) { $script:menuRect = $r }
    }
    return $true
}
[TrayProbe.Native]::EnumWindows($enum, [IntPtr]::Zero) | Out-Null
if ($null -eq $script:menuRect) { throw "the menu never opened — check the trace" }
$menu = $script:menuRect
Write-Output ("menu window {0},{1} {2}x{3}" -f $menu.Left, $menu.Top, ($menu.Right - $menu.Left), ($menu.Bottom - $menu.Top))

function Invoke-Click([int]$x, [int]$y) {
    [TrayProbe.Native]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 250
    [TrayProbe.Native]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [TrayProbe.Native]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1000
}

# ── Photograph it ──────────────────────────────────────────
if ($Shot) {
    # The window rect already includes the 18 DIP shadow margin; photograph a little
    # wider so the shadow itself is visible.
    $w = $menu.Right - $menu.Left + 160
    $h = $menu.Bottom - $menu.Top + 160
    $x = [Math]::Max(0, $menu.Left - 80)
    $y = [Math]::Max(0, $menu.Top - 80)
    $screenDc = [TrayProbe.Native]::GetDC([IntPtr]::Zero)
    $memDc = [TrayProbe.Native]::CreateCompatibleDC($screenDc)
    $hbm = [TrayProbe.Native]::CreateCompatibleBitmap($screenDc, $w, $h)
    $old = [TrayProbe.Native]::SelectObject($memDc, $hbm)
    # CAPTUREBLT (0x40000000) is what pulls layered windows into a capture.
    [TrayProbe.Native]::BitBlt($memDc, 0, 0, $w, $h, $screenDc, $x, $y, 0x40000000 -bor 0x00CC0020) | Out-Null
    $bmp = [System.Drawing.Image]::FromHbitmap($hbm)
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [TrayProbe.Native]::SelectObject($memDc, $old) | Out-Null
    [TrayProbe.Native]::DeleteObject($hbm) | Out-Null
    [TrayProbe.Native]::DeleteDC($memDc) | Out-Null
    [TrayProbe.Native]::ReleaseDC([IntPtr]::Zero, $screenDc) | Out-Null
    Write-Output "captured $Out"
}

# ── Click ──────────────────────────────────────────────────
if ($Row -eq 'Outside') {
    # Empty taskbar space well to the left of the tray: a click away, not on an icon.
    $x = [int]($iconRight - 600)
    $y = [int]($iconTop + 40)
    Invoke-Click $x $y
    Write-Output "clicked away at $x,$y"
}
elseif ($Row) {
    $x = [int]($menu.Left + 138)
    $y = [int]($menu.Top + 18 + $rowCentre[$Row])
    Invoke-Click $x $y
    Write-Output "clicked the $Row row at $x,$y"
}

Start-Sleep -Milliseconds 800
Write-Output "--- trace ---"
Get-Content $log | Select-Object -Skip $before |
    Select-String 'tray menu:|capture:|preview: creating' |
    ForEach-Object { $_.Line.Trim() }
Write-Output ("app still running: {0}" -f (-not $proc.HasExited))

if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
[TrayProbe.Native]::SetCursorPos($saved.X, $saved.Y) | Out-Null
