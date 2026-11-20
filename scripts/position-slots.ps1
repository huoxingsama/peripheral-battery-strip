[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int] $ProcessId,
    [ValidateRange(2, 10)]
    [int] $SlotCount = 10,
    [ValidateRange(2, 45)]
    [int] $TimeoutSeconds = 12,
    [switch] $VerifyOnly
)

# Keep -VerifyOnly for callers of the installer. Every invocation is read-only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('BatterySlotReadNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BatterySlotReadNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public int Size; public IntPtr Window; public uint Id; public Guid Guid; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string caption);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rect);

    public static bool GetSlot(IntPtr host, int number, out Rect rect)
    {
        var id = new IconIdentifier { Size = Marshal.SizeOf(typeof(IconIdentifier)),
            Window = host, Id = (uint)(0x4800 + number - 1) };
        return Shell_NotifyIconGetRect(ref id, out rect) == 0;
    }
}
'@
}

function New-PositionResult([string] $Status, [string] $Message, [int[]] $SlotOrder = @(), [bool] $Contiguous = $false) {
    [pscustomobject] @{ Status = $Status; Message = $Message; SlotOrder = $SlotOrder; Contiguous = $Contiguous }
}

function Test-MatchingBounds($NativeRect, $UiRect) {
    return [Math]::Abs($NativeRect.Left - $UiRect.Left) -le 2 -and
        [Math]::Abs($NativeRect.Right - $UiRect.Right) -le 2 -and
        [Math]::Abs($NativeRect.Top - $UiRect.Top) -le 2 -and
        [Math]::Abs($NativeRect.Bottom - $UiRect.Bottom) -le 2
}

function Read-TrayLayout {
    $tray = [BatterySlotReadNative]::FindWindow('Shell_TrayWnd', [NullString]::Value)
    $hostWindow = [BatterySlotReadNative]::FindWindow([NullString]::Value, 'Peripheral Battery Strip Background')
    [uint32] $owner = 0
    if ($hostWindow -eq [IntPtr]::Zero -or $tray -eq [IntPtr]::Zero -or
        [BatterySlotReadNative]::GetWindowThreadProcessId($hostWindow, [ref] $owner) -eq 0 -or $owner -ne $ProcessId) { return }

    $bar = New-Object BatterySlotReadNative+Rect
    if (![BatterySlotReadNative]::GetWindowRect($tray, [ref] $bar) -or
        $bar.Right - $bar.Left -le $bar.Bottom - $bar.Top) { return }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($tray)
    $items = @(
        foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $selector)) {
            $info = $element.Current
            $rect = $info.BoundingRectangle
            if ($info.IsOffscreen -or $rect.IsEmpty -or $rect.Width -lt 12 -or $rect.Height -lt 12 -or
                $rect.Left -lt $bar.Left -or $rect.Right -gt $bar.Right -or
                $rect.Top -lt $bar.Top -or $rect.Bottom -gt $bar.Bottom) { continue }
            [pscustomobject] @{ Id = $info.AutomationId; Name = [string] $info.Name; Rect = $rect; Slot = 0 }
        }
    )

    $apps = @($items | Where-Object Id -EQ 'NotifyItemIcon' | Sort-Object { $_.Rect.Left })
    if ($apps.Count -lt $SlotCount) { return }
    foreach ($slot in 1..$SlotCount) {
        $native = New-Object BatterySlotReadNative+Rect
        if (![BatterySlotReadNative]::GetSlot($hostWindow, $slot, [ref] $native)) { return }
        # Runtime labels name devices. The verified host, icon ID, and physical rectangle identify our slot.
        $matching = @($apps | Where-Object { $_.Slot -eq 0 -and (Test-MatchingBounds $native $_.Rect) })
        if ($matching.Count -ne 1) { return }
        $matching[0].Slot = $slot
    }

    $chevron = $items | Where-Object {
        $_.Id -eq 'SystemTrayIcon' -and $_.Name -match '显示隐藏的图标|Show hidden icons'
    } | Select-Object -First 1
    $mirrored = (([BatterySlotReadNative]::GetWindowLong($tray, -20) -band 0x00400000) -ne 0) -or
        ($chevron -and $chevron.Rect.Left -ge $apps[-1].Rect.Right - 2)
    [pscustomobject] @{ Apps = $apps; Chevron = $chevron; Mirrored = $mirrored }
}

function Test-Contiguous($OwnedApps) {
    for ($index = 1; $index -lt $OwnedApps.Count; $index++) {
        $previous = $OwnedApps[$index - 1].Rect
        $current = $OwnedApps[$index].Rect
        if ([Math]::Abs($current.Left - $previous.Right) -gt 2 -or
            [Math]::Abs($current.Top - $previous.Top) -gt 2 -or
            [Math]::Abs($current.Bottom - $previous.Bottom) -gt 2) { return $false }
    }
    return $true
}

$dpi = [BatterySlotReadNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
if ($dpi -eq [IntPtr]::Zero) {
    return New-PositionResult 'ManualRequired' '当前会话无法读取托盘位置，请手动调整。'
}
try {
    $uia = [System.Windows.Automation.AutomationElement]
    $selector = [System.Windows.Automation.OrCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($uia::AutomationIdProperty, 'NotifyItemIcon'),
        [System.Windows.Automation.PropertyCondition]::new($uia::AutomationIdProperty, 'SystemTrayIcon'))
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastLayout = ''
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try { $layout = Read-TrayLayout }
        catch [System.Windows.Automation.ElementNotAvailableException] { $layout = $null }
        catch [Runtime.InteropServices.COMException] { $layout = $null }
        if (!$layout) { Start-Sleep -Milliseconds 200; continue }

        # Explorer animates new tray icons. Check consecutive observations before deciding.
        $signature = ($layout.Apps | ForEach-Object { "$($_.Slot)@$($_.Rect.Left)" }) -join ','
        if ($signature -ne $lastLayout) {
            $lastLayout = $signature
            Start-Sleep -Milliseconds 220
            continue
        }
        $owned = @($layout.Apps | Where-Object Slot -GT 0)
        $order = @($owned | ForEach-Object Slot)
        $contiguous = Test-Contiguous $owned
        $leading = !$layout.Mirrored -and $layout.Apps[0].Slot -gt 0 -and
            (!$layout.Chevron -or [Math]::Abs($owned[0].Rect.Left - $layout.Chevron.Rect.Right) -le 2)
        if ($contiguous -and $leading) {
            return New-PositionResult 'Positioned' '外设电量槽位已连续排列在可见应用托盘图标的最前面。' $order $true
        }
        return New-PositionResult 'ManualRequired' '请将外设电量槽位并排拖到应用托盘图标的最前面。' $order $contiguous
    }
    return New-PositionResult 'ManualRequired' '未能从 Windows 确认全部电量槽位，请检查托盘显示设置。'
} finally {
    [void] [BatterySlotReadNative]::SetThreadDpiAwarenessContext($dpi)
}
