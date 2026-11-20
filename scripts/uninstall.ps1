[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$localData = [Environment]::GetFolderPath('LocalApplicationData')
$installPath = Join-Path $localData 'PeripheralBatteryStrip'
$installedStrip = Join-Path $installPath 'BatteryHubStrip.exe'
$knownFiles = @('BatteryHubStrip.exe', 'BatteryHub.exe', 'headsetcontrol.exe', 'LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'uninstall.ps1')
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'PeripheralBatteryStrip'

$setupMutex = [Threading.Mutex]::new($false, 'Local\PeripheralBatteryStripSetup')
$ownsSetup = $false
try {
    try { $ownsSetup = $setupMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsSetup = $true }
    if (!$ownsSetup) { throw '已有一个安装或卸载任务在运行。' }
    if (Test-Path -LiteralPath $installPath) {
        $folder = Get-Item -LiteralPath $installPath -Force
        if (!$folder.PSIsContainer -or ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw '安装路径不是普通本地目录，卸载已停止。'
        }
        foreach ($name in $knownFiles) {
            $target = Join-Path $installPath $name
            if (Test-Path -LiteralPath $target) {
                $file = Get-Item -LiteralPath $target -Force
                if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "安装文件不是普通文件，卸载已停止。$name"
                }
            }
        }

        $running = @(Get-Process BatteryHubStrip -ErrorAction SilentlyContinue | Where-Object {
            $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId -and $_.Path -ieq $installedStrip
        })
        if ($running) {
            Start-Process -FilePath $installedStrip -ArgumentList '--quit' -Wait
            foreach ($process in $running) {
                if (!$process.WaitForExit(10000)) { throw '外设电量栏仍在运行。请从托盘菜单退出后重试。' }
            }
        }

        foreach ($name in $knownFiles) {
            [IO.File]::Delete((Join-Path $installPath $name))
        }
        if (!@(Get-ChildItem -LiteralPath $installPath -Force | Select-Object -First 1)) {
            try { [IO.Directory]::Delete($installPath) }
            catch [IO.IOException] { Write-Host '安装目录已清空。' }
        }
    }

    $startup = Get-ItemPropertyValue -LiteralPath $runKey -Name $runValue -ErrorAction SilentlyContinue
    if ($startup -ieq ('"' + $installedStrip + '"')) {
        Remove-ItemProperty -LiteralPath $runKey -Name $runValue
    }
    Write-Host '外设电量栏已卸载。'
    exit 0
} catch {
    Write-Host ('卸载失败。' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if ($ownsSetup) { $setupMutex.ReleaseMutex() }
    $setupMutex.Dispose()
}
