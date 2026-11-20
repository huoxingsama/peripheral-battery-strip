# 电量读取接口

`BatteryHubStrip.exe --json` 会发起一次本地扫描，读取设备状态并将一个 UTF-8 JSON 对象写入标准输出。命令可以独立运行。它需要安装目录中的 BatteryHub 读取组件。

## JSON 格式

下例中的设备名和电量仅用于展示格式。

```json
{
  "schemaVersion": 1,
  "devices": [
    {
      "name": "无线耳机",
      "kind": "headset",
      "percent": 68,
      "charging": false,
      "connected": true,
      "stale": false
    },
    {
      "name": "无线鼠标",
      "kind": "mouse",
      "percent": null,
      "charging": false,
      "connected": false,
      "stale": false
    }
  ]
}
```

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `schemaVersion` | 整数 | 当前为 `1`。读取方应忽略未知字段。 |
| `devices` | 数组 | BatteryHub 这次扫描识别到的设备。扫描成功但没有可识别设备时为 `[]`。 |
| `name` | 字符串 | 设备报告的名称。名称可重复或随系统设置变化。 |
| `kind` | 字符串 | `headset`、`mouse`、`keyboard`、`gamepad`、`phone`、`microphone` 或 `other`。 |
| `percent` | 整数或 `null` | 范围为 `0` 到 `100`。`null` 表示本次没有百分比。 |
| `charging` | 布尔值 | 设备报告正在充电时为 `true`。 |
| `connected` | 布尔值 | 读取组件报告设备已连接时为 `true`。接收器在场但设备无响应时可能为 `false`。 |
| `stale` | 布尔值 | 读取组件在本次扫描中标记设备休眠且提供了电量时为 `true`。 |

接口不输出设备地址或序列号。任务栏为便于查看可以短暂保留并淡化上次读数。命令行扫描不会读取任务栏的这个缓存。

## 命令与退出码

| 命令 | 行为 |
| --- | --- |
| `BatteryHubStrip.exe --json` | 读取一次。成功时退出码为 `0`，读取组件缺失或扫描失败时为 `1`。有错误时标准错误输出中文提示。 |
| `BatteryHubStrip.exe --refresh` | 向运行中的电量条发送刷新请求。送达返回 `0`，程序未运行或请求未送达返回 `1`。命令不会等待后台扫描结束。 |
| `BatteryHubStrip.exe --quit` | 请求关闭电量条。未运行时也返回 `0`。 |
| `BatteryHubStrip.exe --help` | 查看中文帮助。 |
| `BatteryHubStrip.exe --version` | 输出程序版本。 |

未知参数返回 `64`。建议定时调用的脚本间隔至少 30 秒。

## Python 示例

```python
import json
import os
import subprocess
from pathlib import Path

exe = Path(os.environ["LOCALAPPDATA"]) / "PeripheralBatteryStrip" / "BatteryHubStrip.exe"
result = subprocess.run(
    [str(exe), "--json"],
    check=True,
    capture_output=True,
    encoding="utf-8",
    timeout=35,
)
devices = json.loads(result.stdout)["devices"]
for device in devices:
    if device["percent"] is not None:
        print(f'{device["name"]}  {device["percent"]}%')
```
