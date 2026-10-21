# 本地插件

电栖（Perivolt）提供本地 DLL 插件机制，可扩展主题、提醒规则和设备电量读取。1.0.8-preview.2 兼容 API 1/2，另提供 API 3 任务栏布局扩展；正式 1.0.7 只支持 API 1。插件与主程序同进程运行，仅启用信任来源的插件。官方插件源码、可选插件及独立构建入口统一放在 [Extensions 插件目录](../Extensions/README.md)。

电栖默认离线，以普通用户权限运行，无进程注入或全局输入钩子。随包插件不联网；适配插件应只发送确认过的电量与连接状态查询，不进行固件、配对、灯光、DPI 或按键配置。宿主不会自动下载插件；主程序在用户操作或开启相应选项后，支持版本检查、更新下载及脱敏诊断上报，自动检查和自动上报默认关闭。用户手动加入的第三方 DLL 没有权限沙箱，不能把“本地加载”等同于对其行为的安全保证。

## 内置插件

| 插件 ID | 名称 | 默认状态 | 行为 |
| --- | --- | --- | --- |
| `vendor-protocols` | 多品牌设备协议包 | 开启 | 命中厂商硬件标识后按需加载，补充 Logitech / Razer / Weisheng / Compx / AULA / DualSense / Steam 读取；开发版新增 MCHOSE、LANGTU、SteelSeries、Corsair、HyperX 协议族，具体范围见 [兼容性说明](compatibility.md) |
| `aurora-theme` | 极光主题 | 关闭 | 启用后加入设置里的主题列表；选中才应用，停用当前插件主题后跟随系统 |
| `full-charge` | 充满电提醒 | 关闭 | 本次运行已观察到未满电变为 100%，或满电充电结束时提醒；首次读取和重复扫描不提醒 |
| `quiet-hours` | 定时免打扰 | 关闭 | 在指定时段暂停低电提醒；支持跨午夜，起止相同表示全天 |

各插件开关和选项独立保存。仅发现清单不会立即加载未启用的 DLL。设备插件按清单的 VID 范围过滤后调用，读取器内部仍必须继续检查精确 PID、集合和报告格式。

任务栏增强及电量趋势作为独立可选插件开发，不加入上述随包插件，也不进入主程序安装包或便携包。任务栏增强 1.1.0 需要 1.0.8-preview.2 的 API 3，电量趋势 1.0.0 继续使用 API 2，尚未公开发布；源码和开发边界见 [Optional](../Extensions/Optional/README.md)。

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

API 1 的 `Kind` 支持 `utility`、`theme`、`device`。设备插件另需 `Vendors` 整数数组，例如 VID `0x1234` 对应 JSON 十进制 `4660`；这只是调用前过滤，不取代插件内部的型号验证。这三类插件的 `ApiVersion` 必须为 `1`。插件 ID 应稳定且唯一；重命名 ID 会形成另一份偏好。

交互插件使用 `"ApiVersion": 2`、`"Kind": "interactive"`，ID 仅使用 ASCII 字母、数字与短横线，首位为字母或数字。API 2 不改变已有 API 1 清单和契约；旧宿主会将新清单标为不兼容并保持关闭。

## 开发接口

以 [PluginContracts.cs](../Plugins/PluginContracts.cs) 为准，插件实现 `IPowerHubPlugin`，提供公共无参构造函数。目标框架与主程序一致：`net10.0-windows10.0.19041.0`，引用主项目的公共契约。现有示例在 [Extensions/Essentials](../Extensions/Essentials) 和 [Extensions/VendorProtocols](../Extensions/VendorProtocols)。

| 成员 | 用途 |
| --- | --- |
| `OnScan(PluginContext)` | 接收本次设备列表、时间和字符串选项，返回低电提醒抑制标记及可选通知 |
| `Theme` | 返回主题 ID、深浅色标记和资源颜色字典；颜色使用 `#RRGGBB` |
| `Themes` | 多主题列表；默认兼容旧的 `Theme`。可为每套填写 `Name`、`Group`、`Description` 和原创装饰 `Motif` |
| `ReadHid(HidDeviceContext)` | 在明确匹配硬件后读取电量，返回设备状态、诊断及可选接收器子设备 |
| `Dispose()` | 释放插件持有的资源 |

### API 2 交互插件

实现 `IInteractivePowerHubPlugin`，由宿主在 UI 线程调用：

| 成员 | 用途 |
| --- | --- |
| `Initialize(PluginUiContext)` | 首次启用时接收独立数据目录与任务栏设备选择回调；未启用不加载、不创建数据目录 |
| `Update(PluginUiSnapshot)` | 接收已有扫描结果、扫描时间和当前插件选项；不重复扫描硬件 |
| `CreateTaskbarPopup(string?)` | 返回尚未挂载的 WPF 元素；宿主负责悬停位置与关闭，无内容则返回 `null` |
| `HandleTaskbarWheel(string?, int)` | 处理电量条上的滚轮，返回是否消费输入 |
| `Open()` | 用户点击插件页“打开插件”时打开插件自己的窗口 |
| `FlushAsync(CancellationToken)` | 异步保存当前待写数据；退出前所有插件共用最多 1 秒的等待预算 |
| `Dispose()` | 在 UI 线程关闭窗口、解除订阅并释放资源；不得同步等待后台磁盘任务 |

数据目录位于用户设置目录的 `plugins/<插件ID>/`，与程序安装目录分开。宿主过滤为用户设置的设备名称后传入快照；插件应自行限制文件大小、保留期限与后台任务数量。停用、重载或出错时宿主关闭旧任务栏弹层，插件仍须清理自己的窗口。正常退出先停止新扫描，再尝试保存；保存超时后继续退出，强制结束进程或保存失败时不能保证最近批次落盘。

所有同步交互回调应及时返回，长任务只能异步执行；同进程插件没有强制中断与权限沙箱。API 1 实用插件的后台 500 ms 结果等待规则不适用于 UI 回调。

### API 3 任务栏布局

清单设置 `"ApiVersion": 3`、`"Kind": "interactive"`，仍实现 `IInteractivePowerHubPlugin`。API 3 只用于需要下列新增能力的插件；原 API 2 清单、构造函数和回调保持兼容。

- `PluginUiSnapshot.TaskbarEnabled` 与 `TaskbarDeviceId` 反映全局任务栏开关和设备选择。全局关闭时插件须停止布局跟踪并关闭副屏窗口。
- `PluginUiContext.CreateTaskbarSurface(target)` 在 UI 线程创建宿主绘制的电量条，返回 `IPluginTaskbarSurface`。插件复用快照调用 `UpdateDevices()`，隐藏时 `Suspend()`，不再需要时 `Dispose()`。宿主在插件退休、出错或退出时兜底释放其创建的窗口。
- `IPluginTaskbarTarget.ReadLayout()` 返回目标 HWND、物理像素边界、显示器边界、客户区原点、边缘及窗口 DPI；不可用、隐藏、部分滑出或目标身份失效时返回 `null`。此方法既会在 UI 线程调用，也会在后台 MTA 布局采样前后调用，须线程安全，不访问 WPF。
- `LayoutChanged` 在 UI 线程通知位置、显示或隐藏变化；`InitialOffset` 与拖动结束时的 `SaveOffset()` 管理各屏位置，不逐帧写文件。宿主拒绝布局采样期间变化的结果，并在布局失效时收起窗口和弹层。
- `PluginUiContext.RefreshTaskbar()` 请求主屏重新检查布局。API 2 上下文不提供新增工厂和刷新回调；旧宿主将 API 3 清单视为不兼容。

任务栏增强只在插件及全局任务栏开关都启用时监听 Explorer 的窗口显示、隐藏和位置事件，使用进程限定的 WinEvent 通知和两秒拓扑恢复检查。监听不是全局输入钩子，不改变系统任务栏设置；Windows 没有创建副屏任务栏时不生成悬浮替代。原生事件约束见 [Microsoft SetWinEventHook 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)。

主题资源沿用主程序的 `PageBackground`、`Surface`、`SurfaceAlt`、`Stroke`、`TextPrimary`、`TextSecondary`、`Accent`、`AccentSoft`、`AccentText`、`Warning`、`Danger`、`RingTrack`。启用插件只添加候选项，只有所选主题的调色板会应用。多主题存储为 `plugin:<插件ID>/<主题ID>`；单主题 ID 与插件 ID 相同的旧插件仍使用 `plugin:<插件ID>`。停用正在使用的插件后跟随系统。单插件最多 500 套，主题 ID 使用 ASCII 字母、数字、短横线或下划线，完整存储键不超过 87 字符。

### 群星映色

从 1.0.3 起，群星映色为独立可选插件，不随主程序安装包或便携包分发。已发布的 1.0.2 包含配色插件，不含立绘。将独立插件包解压到程序旁的 `plugins`，使清单位于 `plugins/character-themes/plugin.json`，在“插件”开启主题包，然后到“设置 → 通用 → 插件主题”选择。插件 1.1.1 包含终末地 19、明日方舟 74、绝区零 26、少女前线2：追放 16 套，共 135 个独立选项；每个角色与形态配置独立色种、深浅色方向与原创配色标题。陈的三形态、阿米娅的三职业及其他异格或独立身份可分别选用，搜索原角色名也能找到对应形态；背景与透明度设置按主题 ID 各自保存。泥岩采用无头盔精二形象，仍使用原 `arknights-mudrock` 选项，不增加独立形态条目。筛选和搜索只在本机执行，列表采用回收式虚拟化，调色板仅在插件首次启用时生成并缓存，停用后释放。

角色名及游戏名用于标识非官方灵感主题。没有打包游戏立绘、Logo、语音或字体，主项目许可不声称拥有相关角色和商标。角色名单参考与配色说明见 [主题包说明](../Extensions/CharacterThemes/README.md)。

本轮新生成的 135 张背景单独保存在 `Perivolt-CharacterBackgrounds-1.1.1-LocalOnly/artwork`，在“选择立绘文件夹”中指定该目录。旧 `Perivolt-CharacterArtwork-LocalOnly` 是原设身份参考包。新版背景本地包不含插件 DLL，背景不会上传公开仓库或随插件发行 ZIP 分发。

设备读取器应仅向已证实的厂商协议集合发送状态查询，限制等待时间，检查响应标识、命令、长度、校验和及取值范围。不要把无响应当成 0%，不要通过灯光、固件、配对或工作模式写入来探测电量；读取普通键鼠输入也不应成为厂商电量探测手段。

`scripts\Build.ps1` 的调试与发布构建均只编译核心及明确列出的随包插件，不再递归收录新增项目。发布输出会移除非随包插件残留，安装器按四个插件 ID 收录。可选插件使用独立命令，例如：

```powershell
.\scripts\Build-Plugin.ps1 -Project .\Extensions\CharacterThemes\CharacterThemes.csproj
```

独立 ZIP 和同名 `.sha256` 输出到 `artifacts/plugins/`；打包不安装、不上传，也不复制到主程序的默认插件目录。ZIP 中的 `<插件ID>/` 包含 `plugin.json`、插件 DLL、插件说明（若有）及许可和署名声明，解压到运行程序旁的 `plugins` 后再手动启用。群星映色的 `Themes.txt` 已嵌入 DLL，无需额外复制，也不打包本地立绘。已发布的旧版三文件 ZIP 保持不变，新构建包附上声明文件。宿主共享主程序契约程序集；不要附带第二份主程序程序集来替换契约。有额外私有依赖的插件需在分发前确认其依赖和许可，当前独立脚本仅收录清单指定的 DLL。

插件保留其自己的许可；主项目的个人非商业许可不会重新许可其他插件或其依赖，历史 MIT 授权继续有效。未来分发适用新许可的官方插件修改时，附上根目录的 `LICENSE`、`NOTICE.md`、`LICENSES/MIT-legacy.txt` 和适用第三方声明，并保留开发者“阳影X (YangYingX)”及项目来源。

实用插件的计算在后台执行；超过 500 ms 时本轮不采用结果，同一任务结束前不会叠加新任务。插件仍应主动限制工作量，及时返回。
