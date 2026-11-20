[CmdletBinding()]
param(
    [string] $ExecutablePath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PeripheralBatteryStrip\BatteryHubStrip.exe'),
    [ValidateRange(5, 60)]
    [int] $TimeoutSeconds = 20,
    [switch] $VerifyOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$registrations = 'HKCU:\Control Panel\NotifyIconSettings'
$exe = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
$slotCount = 10

function Get-SlotRegistrations {
    if (!(Test-Path -LiteralPath $registrations)) { return }
    Get-ChildItem -LiteralPath $registrations | ForEach-Object {
        $icon = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
        if ($icon.ExecutablePath -ieq $exe -and $icon.InitialTooltip -cmatch '^外设电量位置 (10|[1-9])$') {
            [pscustomobject] @{ Number = [int] $Matches[1]; Id = $_.PSChildName; Promoted = ($icon.IsPromoted -eq 1) }
        }
    }
}

function Get-SettingsSection {
    $element = [System.Windows.Automation.AutomationElement]
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($element::ClassNameProperty, 'NamedContainerAutomationPeer'),
        [System.Windows.Automation.OrCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new($element::NameProperty, '其他系统托盘图标'),
            [System.Windows.Automation.PropertyCondition]::new($element::NameProperty, 'Other system tray icons')))

    foreach ($process in Get-Process ApplicationFrameHost, SystemSettings -ErrorAction SilentlyContinue) {
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { continue }
        $window = $element::FromHandle($process.MainWindowHandle)
        $section = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $section) { return $section }
    }
}

function Find-ElementById($Root, [string] $Id) {
    $element = [System.Windows.Automation.AutomationElement]
    $condition = [System.Windows.Automation.PropertyCondition]::new($element::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ForResult([scriptblock] $Read, [string] $Missing) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $result = & $Read
            if ($result) { return $result }
        } catch [System.Windows.Automation.ElementNotAvailableException] {
            # Settings can replace its accessibility tree while loading another pane.
        }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw $Missing
}

$slots = @(Wait-ForResult {
    $matchesByNumber = @(Get-SlotRegistrations | Group-Object Number)
    if ($matchesByNumber.Count -eq $slotCount) {
        if ($matchesByNumber | Where-Object Count -NE 1) {
            throw 'Windows 为同一位置登记了多个条目。请关闭电量栏后重新准备托盘位置。'
        }
        $matchesByNumber.Group | Sort-Object Number
    }
} 'Windows 尚未登记全部 10 个外设电量位置。请确认电量栏已进入准备模式。')

if (!$VerifyOnly -and ($slots | Where-Object { !$_.Promoted })) {
    Start-Process 'ms-settings:taskbar'
    Wait-ForResult {
        $section = Get-SettingsSection
        if ($null -eq $section) { return }
        $header = Find-ElementById $section 'EntityItemButton'
        if ($null -eq $header) { return }
        $expand = $null
        if (!$header.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref] $expand)) { return }
        if ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $expand.Expand()
        }
        return $true
    } '无法展开 Windows 设置中的「其他系统托盘图标」。' | Out-Null

    foreach ($slot in $slots) {
        if ($slot.Promoted) { continue }
        $automationId = 'SystemSettings_DesktopTaskbar_SystemTrayAppListItem{0}_ToggleSwitch' -f $slot.Id
        Wait-ForResult {
            $section = Get-SettingsSection
            if ($null -eq $section) { return }
            $control = Find-ElementById $section $automationId
            if ($null -eq $control) { return }
            $toggle = $null
            if (!$control.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref] $toggle)) { return }
            if ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $toggle.Toggle() }
            return $true
        } "Windows 设置未显示外设电量位置 $($slot.Number) 的开关。" | Out-Null

        $key = Join-Path $registrations $slot.Id
        Wait-ForResult {
            $registration = Get-ItemProperty -LiteralPath $key -Name IsPromoted -ErrorAction SilentlyContinue
            if ($registration.IsPromoted -eq 1) { return $true }
        } "Windows 未确认外设电量位置 $($slot.Number) 已开启。" | Out-Null
    }
}

$verified = @(Get-SlotRegistrations | Where-Object Promoted | Sort-Object Number)
if ($verified.Count -ne $slotCount -or @($verified.Number | Select-Object -Unique).Count -ne $slotCount) {
    throw "托盘设置只开启了 $($verified.Count) 个外设电量位置，需要开启全部 $slotCount 个。"
}
$verified | Select-Object Number, Id, Promoted
