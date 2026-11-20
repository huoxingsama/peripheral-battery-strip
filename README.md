# 外设电量条

在 Windows 11 任务栏右下角查看无线耳机、鼠标、键盘和手柄的电量。安装程序会尝试把电量条放到**隐藏图标箭头 `^` 紧后方**，其他固定托盘图标和输入法保留各自的顺序。数字使用与 Windows 时钟接近的 Segoe UI 常规字重，并跟随任务栏的主题和缩放。将鼠标悬停在**设备图标**上可查看名称和电量。

程序后台刷新电量。连接新设备后，只要电量读取组件支持该设备及其连接方式，它就会自动出现。任务栏最多显示五台设备，本地 JSON 接口可以读取所有已识别的设备。

## 安装

需要 **Windows 11 x64**、.NET Framework 4.8 或更新版本。安装在当前 Windows 账户下，无需管理员权限。首次安装需要联网获取电量读取组件。

1. 从发布页下载最新的 `PeripheralBatteryStrip-*-win-x64.zip` 并解压。 [GitHub 发布页](https://github.com/huoxingsama/peripheral-battery-strip/releases/latest)
2. 双击 `安装.cmd`。安装期间可能打开 Windows 的任务栏设置。让安装程序完成可见性与位置检查即可。
3. 安装完成后，程序从 `%LOCALAPPDATA%\PeripheralBatteryStrip` 启动。将鼠标移到电量条上，右键可以立即刷新、切换「登录 Windows 时启动」或退出。

开机启动默认关闭。也可以在解压后的目录中运行以下命令，并在安装时启用登录启动。

```powershell
.\安装.cmd -StartWithWindows
```

要卸载，运行发布包中的 `卸载.cmd`。

Windows 负责显示折叠箭头。如果系统没有显示箭头，安装程序会尝试把电量条放在可见应用图标的最前面。若自动排列未完成，可运行 `.\安装.cmd -ManualPromotion`，按提示将临时的 1 到 10 号图标拖成一组。托盘槽位分散时，程序会在可见的自有槽位显示简化读数，避免遮住其他图标。 [Windows 任务栏排列说明](https://support.microsoft.com/zh-cn/windows/experience/personalization/customize-the-taskbar-in-windows)

## 设备兼容性

- **USB 无线接收器**依赖 BatteryHub 的设备支持。Sony INZONE H9 II 和 Razer DeathAdder V4 Pro 的接收器模式已读取到电量。
- **蓝牙设备**可以通过 Windows 设备属性读取。以 Sony ULT WEAR 为例，已确认它在 Windows 的免提设备节点上提供过电量。实际显示取决于耳机当前是否连接、驱动是否提供电量。
- 同一设备走蓝牙、USB 接收器或有线连接时，提供的电量信息可能不同。若连接后仍无电量，请先核对该型号和连接方式。 [BatteryHub 设备支持列表](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor#supported-devices)

## 本地命令行 API

`--json` 执行一次电量查询并输出 UTF-8 JSON，可以在图形界面未启动时使用。成功时输出包含 `schemaVersion` 和 `devices` 的对象。查询失败返回非零退出码，未读到某台设备的百分比时该设备的 `percent` 为 `null`。 `--refresh` 请求正在运行的电量条刷新，`--quit` 退出电量条。 [完整接口与字段说明](docs/API.md)

### PowerShell

```powershell
$exe = Join-Path $env:LOCALAPPDATA 'PeripheralBatteryStrip\BatteryHubStrip.exe'
$json = & $exe --json | Out-String
if ($LASTEXITCODE -ne 0) { throw '读取外设电量失败' }
($json | ConvertFrom-Json).devices |
    Where-Object { $_.connected -and $null -ne $_.percent } |
    Select-Object name, percent, charging
```

### Python

```python
import json
import os
import subprocess
from pathlib import Path

exe = Path(os.environ["LOCALAPPDATA"]) / "PeripheralBatteryStrip" / "BatteryHubStrip.exe"
result = subprocess.run([str(exe), "--json"], capture_output=True, encoding="utf-8", check=True)
for device in json.loads(result.stdout)["devices"]:
    if device["connected"] and device["percent"] is not None:
        print(f'{device["name"]}: {device["percent"]}%')
```

例如，接口可能返回如下数据。

```json
{
  "schemaVersion": 1,
  "devices": [
    {
      "name": "Sony INZONE H9 II",
      "kind": "headset",
      "percent": 70,
      "charging": false,
      "connected": true,
      "stale": false
    }
  ]
}
```

## 隐私与源码构建

本地 API 的输出字段包括设备名称、类型、电量和状态。设备地址、路径及内部标识限于本机的电量读取流程。安装脚本从上游 GitHub Release 下载固定版本的 BatteryHub，使用 SHA-256 校验下载包后再解压。程序的登录启动选项与托盘设置作用于当前 Windows 账户。

在仓库目录运行以下命令即可编译本项目的 C# 代码。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

安装和卸载源码见 [install.ps1](scripts/install.ps1) 和 [uninstall.ps1](scripts/uninstall.ps1)。如遇设备兼容性问题，可在议题中附上设备型号、连接方式和删去个人自定义名称后的 JSON 示例。 [提交反馈](https://github.com/huoxingsama/peripheral-battery-strip/issues)

## 许可证与上游依赖

本项目采用 GPL-3.0-only，许可全文见 [LICENSE](LICENSE)。仓库包含电量条、命令行接口和安装脚本的源码。安装时由上游正式发布包提供下列程序。

| 组件 | 用途 | 许可证和源码 |
| --- | --- | --- |
| BatteryHub v1.1.0（krisgfx9 及其贡献者） | 通过独立进程查询外设电量 | GPLv3。 [源码](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/tree/v1.1.0) · [官方发布包](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/releases/tag/v1.1.0) |
| HeadsetControl 4.3.0（Sapd 及其贡献者） | BatteryHub 便携包中的耳机读取组件 | GPL-3.0。 [对应源码和发布包](https://github.com/Sapd/HeadsetControl/releases/tag/4.3.0) |

已安装的 BatteryHub 目录中保留上游随包提供的许可证与第三方声明。
