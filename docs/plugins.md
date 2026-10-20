# 本地插件

PowerHub 1.0.0 提供 API v1 的本地 DLL 插件机制，可扩展主题、提醒规则和设备电量读取。插件与主程序同进程运行，仅启用信任来源的插件。

PowerHub 默认离线，以普通用户权限运行，无进程注入或全局输入钩子。随包插件不联网；适配插件应只发送确认过的电量与连接状态查询，不进行固件、配对、灯光、DPI 或按键配置。宿主不会下载插件；唯一联网功能是由用户明确开启的应用版本检查，默认关闭。用户手动加入的第三方 DLL 没有权限沙箱，不能把“本地加载”等同于对其行为的安全保证。

## 内置插件

| 插件 ID | 名称 | 默认状态 | 行为 |
| --- | --- | --- | --- |
| `vendor-protocols` | 多品牌设备协议包 | 开启 | 命中厂商硬件标识后按需加载，补充 Logitech / Razer / Weisheng / Compx / AULA / DualSense / Steam 读取；开发版新增 MCHOSE、LANGTU、SteelSeries、Corsair、HyperX 协议族，具体范围见 [兼容性说明](compatibility.md) |
| `aurora-theme` | 极光主题 | 关闭 | 启用后加入设置里的主题列表；选中才应用，停用当前插件主题后跟随系统 |
| `full-charge` | 充满电提醒 | 关闭 | 本次运行已观察到未满电变为 100%，或满电充电结束时提醒；首次读取和重复扫描不提醒 |
| `quiet-hours` | 定时免打扰 | 关闭 | 在指定时段暂停低电提醒；支持跨午夜，起止相同表示全天 |

各插件开关和选项独立保存。仅发现清单不会立即加载未启用的 DLL。设备插件按清单的 VID 范围过滤后调用，读取器内部仍必须继续检查精确 PID、集合和报告格式。

免打扰时间使用本机时间，格式为 `HH:mm`，默认 `22:00` 至 `08:00`。免打扰时会同时暂停低电提醒和插件自定义通知。

## 安装与管理

1. 在侧栏打开“插件”，点击“插件目录”。目录是实际运行程序旁的 `plugins`。
2. 将插件清单、DLL 及其私有依赖放在同一个插件子目录内。
3. 点击“重新加载”，再开启需要的插件。
4. 选项输入后按 Enter 或离开输入框保存；刷新不会覆盖正在编辑的内容。

扫描清单的范围为 `plugins` 根目录及其下一层子目录，扩展名为 `.json`。同一目录可包含多个清单并共享一个 DLL。DLL 必须以普通文件放在清单同目录，不能使用绝对路径、向上跳转路径或文件链接。更新正在使用的文件时，先停用插件，必要时退出 PowerHub 后替换。

“已启用，按需加载”表示下次匹配时才创建实例；“已加载”表示已经运行。加载或运行出错时页面显示原因，停用后重新启用可重试。重新加载保留按插件 ID 保存的选项。

## 清单示例

```json
{
  "ApiVersion": 1,
  "Id": "my-reminder",
  "Name": "我的提醒规则",
  "Description": "一个本地提醒扩展示例",
  "Version": "1.0.0",
  "Assembly": "MyReminder.dll",
  "EntryType": "MyReminder.ReminderPlugin",
  "Kind": "utility",
  "Options": [
    { "Key": "label", "Label": "提示名称", "DefaultValue": "设备提示" }
  ]
}
```

`Kind` 支持 `utility`、`theme`、`device`。设备插件另需 `Vendors` 整数数组，例如 VID `0x1234` 对应 JSON 十进制 `4660`；这只是调用前过滤，不取代插件内部的型号验证。`ApiVersion` 当前必须为 `1`。插件 ID 应稳定且唯一；重命名 ID 会形成另一份偏好。

## 开发接口

以 [PluginContracts.cs](../Plugins/PluginContracts.cs) 为准，插件实现 `IPowerHubPlugin`，提供公共无参构造函数。目标框架与主程序一致：`net10.0-windows10.0.19041.0`，引用主项目的公共契约。现有示例在 [Extensions/Essentials](../Extensions/Essentials) 和 [Extensions/VendorProtocols](../Extensions/VendorProtocols)。

| 成员 | 用途 |
| --- | --- |
| `OnScan(PluginContext)` | 接收本次设备列表、时间和字符串选项，返回低电提醒抑制标记及可选通知 |
| `Theme` | 返回主题 ID、深浅色标记和资源颜色字典；颜色使用 `#RRGGBB` |
| `ReadHid(HidDeviceContext)` | 在明确匹配硬件后读取电量，返回设备状态、诊断及可选接收器子设备 |
| `Dispose()` | 释放插件持有的资源 |

主题资源沿用主程序的 `PageBackground`、`Surface`、`SurfaceAlt`、`Stroke`、`TextPrimary`、`TextSecondary`、`Accent`、`AccentSoft`、`AccentText`、`Warning`、`Danger`、`RingTrack`。多个主题插件同时启用时只使用宿主发现顺序中的首个有效主题，不组合多份调色板。

设备读取器应仅向已证实的厂商协议集合发送状态查询，限制等待时间，检查响应标识、命令、长度、校验和及取值范围。不要把无响应当成 0%，不要通过灯光、固件、配对或工作模式写入来探测电量；读取普通键鼠输入也不应成为厂商电量探测手段。

`scripts\Build.ps1` 编译核心与内置插件，`-Publish` 将清单及 DLL 放入发布目录的对应 `plugins/<id>`。宿主共享主程序契约程序集；插件自己的依赖放在插件目录，不要附带第二份主程序程序集来替换契约。

插件保留其自己的许可；主项目的 MIT 许可不会重新许可其他插件或其依赖。

实用插件的计算在后台执行；超过 500 ms 时本轮不采用结果，同一任务结束前不会叠加新任务。插件仍应主动限制工作量，及时返回。
