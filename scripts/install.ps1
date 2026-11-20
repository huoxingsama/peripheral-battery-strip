[CmdletBinding()]
param(
    [string] $StripExecutable,
    [switch] $StartWithWindows,
    [switch] $StageOnly,
    [string] $InstallDirectory,
    [switch] $ManualPromotion
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$archiveUrl = 'https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/releases/download/v1.1.0/BatteryHub-1.1.0-portable.zip'
$archiveSha256 = '7A97A1FF0AEFA895386077093B23A714DF549A08FAD3B1B9C37CCF2100A27026'
$upstreamFiles = @('BatteryHub.exe', 'headsetcontrol.exe', 'LICENSE.txt', 'THIRD_PARTY_NOTICES.txt')
$installedFiles = @('BatteryHubStrip.exe', 'uninstall.ps1') + $upstreamFiles
$defaultDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PeripheralBatteryStrip'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'PeripheralBatteryStrip'

function Assert-PlainPath([string] $Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "安装目录或文件使用了重解析点，请选用普通本地目录。$Path"
        }
    }
}

function Save-VerifiedArchive([string] $Destination) {
    $protocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = $protocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $archiveUrl -UseBasicParsing -OutFile $Destination -TimeoutSec 90
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $archiveSha256) { return }
        throw '首次下载的 SHA-256 校验失败。'
    } catch {
        $curl = Get-Command 'curl.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (!$curl) { throw }
        & $curl.Source -fLsS --connect-timeout 15 --max-time 90 --retry 1 --output $Destination $archiveUrl
        if ($LASTEXITCODE -ne 0) { throw '无法从 GitHub 下载 BatteryHub 1.1.0。' }
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $archiveSha256) {
            throw 'BatteryHub 压缩包的 SHA-256 校验失败。'
        }
    } finally {
        [Net.ServicePointManager]::SecurityProtocol = $protocol
    }
}

function Get-CurrentStrip {
    Get-Process BatteryHubStrip -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId -and $_.Path
    }
}

function Stop-InstalledStrip {
    $running = @(Get-CurrentStrip | Where-Object Path -IEQ $installedStrip)
    if (!$running) { return }
    Start-Process -FilePath $installedStrip -ArgumentList '--quit' -Wait
    foreach ($process in $running) {
        if (!$process.WaitForExit(10000)) { throw '外设电量栏仍在运行。请从托盘菜单退出后重试。' }
    }
}

function Deploy-Files([string] $Stage, [string] $Backup) {
    [void] [IO.Directory]::CreateDirectory($installPath)
    $deployed = @()
    try {
        foreach ($name in $installedFiles) {
            $target = Join-Path $installPath $name
            $oldCopy = Join-Path $Backup $name
            Assert-PlainPath $target
            if ([IO.Directory]::Exists($target)) { throw "同名目录阻止安装。$target" }
            if ([IO.File]::Exists($target)) { [IO.File]::Copy($target, $oldCopy) }
            $deployed += $name
            [IO.File]::Copy((Join-Path $Stage $name), $target, $true)
        }
    } catch {
        $deployError = $_
        $restoreErrors = @()
        foreach ($name in $deployed) {
            $target = Join-Path $installPath $name
            $oldCopy = Join-Path $Backup $name
            try {
                if ([IO.File]::Exists($oldCopy)) { [IO.File]::Copy($oldCopy, $target, $true) }
                else { [IO.File]::Delete($target) }
            } catch { $restoreErrors += $name }
        }
        if ($restoreErrors) {
            $script:preserveBackup = $true
            throw "升级失败，部分旧文件无法恢复。备份保存在 $Backup。"
        }
        throw $deployError
    }
}

$setupMutex = [Threading.Mutex]::new($false, 'Local\PeripheralBatteryStripSetup')
$ownsSetup = $false
try {
    if (!$StageOnly) {
        try { $ownsSetup = $setupMutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $ownsSetup = $true }
        if (!$ownsSetup) { throw '已有一个安装或卸载任务在运行。' }
    }
    $build = [int] (Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber)
    $architecture = $env:PROCESSOR_ARCHITECTURE
    if ($env:PROCESSOR_ARCHITEW6432) { $architecture = $env:PROCESSOR_ARCHITEW6432 }
    if ($build -lt 22000 -or $architecture -ne 'AMD64') { throw '此版本需要 Windows 11 x64。' }
    if ($InstallDirectory -and !$StageOnly) { throw '自定义安装目录仅用于 -StageOnly 验证。' }
    if ($StageOnly -and !$InstallDirectory) { throw '使用 -StageOnly 时，请通过 -InstallDirectory 指定测试目录。' }
    if (!$InstallDirectory) { $InstallDirectory = $defaultDirectory }
    $fullPath = [IO.Path]::GetFullPath($InstallDirectory)
    if ($fullPath -eq [IO.Path]::GetPathRoot($fullPath)) { throw '请指定独立的应用目录。' }
    $installPath = $fullPath.TrimEnd('\')
    if ($StageOnly -and ($installPath -ieq $defaultDirectory -or
        $installPath.StartsWith($defaultDirectory + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw '请为 -StageOnly 选择独立测试目录。'
    }
    Assert-PlainPath $installPath
    if (!$StripExecutable) { $StripExecutable = Join-Path $PSScriptRoot 'BatteryHubStrip.exe' }
    if (!(Test-Path -LiteralPath $StripExecutable -PathType Leaf)) { throw '安装包缺少 BatteryHubStrip.exe。' }
    $stripSource = (Resolve-Path -LiteralPath $StripExecutable).ProviderPath
    $installedStrip = Join-Path $installPath 'BatteryHubStrip.exe'
    $uninstaller = Join-Path $PSScriptRoot 'uninstall.ps1'
    $promoter = Join-Path $PSScriptRoot 'promote-slots.ps1'
    $positioner = Join-Path $PSScriptRoot 'position-slots.ps1'
    if (!(Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw '安装包缺少 uninstall.ps1。' }
    if (!$StageOnly -and !(Test-Path -LiteralPath $promoter -PathType Leaf)) { throw '安装包缺少 promote-slots.ps1。' }
    if (!$StageOnly -and !(Test-Path -LiteralPath $positioner -PathType Leaf)) { throw '安装包缺少 position-slots.ps1。' }

    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('PeripheralBatteryStrip-' + [Guid]::NewGuid().ToString('N'))
    $stage = Join-Path $scratch 'stage'
    $backup = Join-Path $scratch 'backup'
    $archivePath = Join-Path $scratch 'batteryhub.zip'
    $preserveBackup = $false
    $restartOnFailure = $false
    [void] [IO.Directory]::CreateDirectory($stage)
    [void] [IO.Directory]::CreateDirectory($backup)
    try {
        Write-Host '正在下载并校验 BatteryHub 1.1.0 组件…'
        Save-VerifiedArchive $archivePath
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($name in $upstreamFiles) {
                $entry = $archive.GetEntry($name)
                if (!$entry) { throw "BatteryHub 压缩包缺少 $name。" }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $stage $name))
            }
        } finally { $archive.Dispose() }

        [IO.File]::Copy($stripSource, (Join-Path $stage 'BatteryHubStrip.exe'))
        [IO.File]::Copy($uninstaller, (Join-Path $stage 'uninstall.ps1'))
        if (!$StageOnly) {
            $other = @(Get-CurrentStrip | Where-Object Path -INE $installedStrip)
            if ($other) { throw '检测到从其他目录启动的外设电量栏。请退出该实例后重新运行安装。' }
            $restartOnFailure = [bool] @(Get-CurrentStrip | Where-Object Path -IEQ $installedStrip)
            Stop-InstalledStrip
        }
        Deploy-Files $stage $backup
    } finally {
        [IO.File]::Delete($archivePath)
        foreach ($name in $installedFiles) {
            [IO.File]::Delete((Join-Path $stage $name))
        }
        [IO.Directory]::Delete($stage)
        if (!$preserveBackup) {
            foreach ($name in $installedFiles) { [IO.File]::Delete((Join-Path $backup $name)) }
            [IO.Directory]::Delete($backup)
            [IO.Directory]::Delete($scratch)
        }
    }

    if ($StageOnly) {
        Write-Host "已在测试目录放置和校验文件。$installPath"
        exit 0
    }

    $prepareArgument = if ($ManualPromotion) { '--prepare-manual' } else { '--prepare' }
    $prepareProcess = Start-Process -FilePath $installedStrip -ArgumentList $prepareArgument -PassThru
    try {
        if ($prepareProcess.WaitForExit(500)) { throw '启动托盘位置准备程序失败。' }
        if ($ManualPromotion) {
            Start-Process 'ms-settings:taskbar'
            Write-Host '在 Windows 设置中展开「其他系统托盘图标」，打开「外设电量槽位 1」到「外设电量槽位 10」。'
            Write-Host '将任务栏上临时出现的 1 到 10 号图标拖成一组，并放在折叠箭头后面。'
            [void] (Read-Host '开启后按回车继续')
        }
        & $promoter -ExecutablePath $installedStrip -VerifyOnly:$ManualPromotion | Out-Null
        Write-Host '正在核对电量栏的位置…'
        $position = & $positioner -ProcessId $prepareProcess.Id -VerifyOnly
        if ($position.Status -ne 'Positioned') {
            Write-Warning $position.Message
            Write-Host '可稍后运行「.\安装.cmd -ManualPromotion」重新排好位置。'
        } else {
            Write-Host $position.Message
        }
    } catch {
        Write-Host '托盘位置尚未设好。可在终端运行「.\安装.cmd -ManualPromotion」按提示手动设置。'
        throw
    } finally {
        if (!$prepareProcess.HasExited) {
            Start-Process -FilePath $installedStrip -ArgumentList '--quit' -Wait
            if (!$prepareProcess.WaitForExit(10000)) { throw '托盘位置准备程序未退出，请退出后重试。' }
        }
    }

    $application = Start-Process -FilePath $installedStrip -PassThru
    if ($application.WaitForExit(1000)) { throw '电量栏未能启动。' }
    if ($StartWithWindows) {
        if (!(Test-Path -LiteralPath $runKey)) { [void] (New-Item -Path $runKey) }
        [void] (New-ItemProperty -LiteralPath $runKey -Name $runValue -Value ('"' + $installedStrip + '"') -PropertyType String -Force)
    }
    Write-Host '安装完成。可在任务栏右键打开电量栏菜单。'
    exit 0
} catch {
    $installationError = $_.Exception.Message
    if ($restartOnFailure -and !$preserveBackup -and (Test-Path -LiteralPath $installedStrip -PathType Leaf) -and
        !@(Get-CurrentStrip | Where-Object Path -IEQ $installedStrip)) {
        Start-Process -FilePath $installedStrip -ErrorAction SilentlyContinue
    }
    Write-Host ('安装失败。' + $installationError) -ForegroundColor Red
    exit 1
} finally {
    if ($ownsSetup) { $setupMutex.ReleaseMutex() }
    $setupMutex.Dispose()
}
