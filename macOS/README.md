# 电栖 · Perivolt for macOS

当前为 **1.1.0-alpha.2 开发预览**，尚未公开发行。Windows 正式版 [1.1.0](https://github.com/YangYingX/Perivolt/releases/tag/v1.1.0) 已发布；本目录是独立 Avalonia 桌面应用，不加载 Windows WPF 插件。

首版面向 macOS 15 及以上的 Apple Silicon 和 Intel 电脑，使用 .NET 10 与 Avalonia 11.3.21。最低系统目标参照 [.NET 10 当前支持列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)，实际兼容性仍需 Mac 实机确认。

## 已接通的功能

- 菜单栏常驻入口、已连接设备列表、手动和定时刷新；第一次运行显示主面板，关闭主面板后继续常驻，菜单栏初始化失败时关窗可退出。已接入 macOS 重新打开和系统退出事件，打开已最小化的面板时恢复窗口。
- 全局及逐设备低电阈值、重复提醒间隔、自定义名称和外观。充电、离线及未知电量不制造低电百分比；通知通过 macOS 本地通知命令发出，实际权限和显示行为待实机验证。
- 集中桌面卡片、设备固定选择、拖动位置、锁定、置顶、紧凑与半透明设置；七种主题、五种电量样式，沿用共享设置模型和已有多语言文案。Mac 新增说明当前以中文为主。
- 本地诊断查看和手动导出。更新检查默认关闭；开启后仅选择适合本机架构的 macOS 包，不选择 Windows EXE。当前正式发行页没有 Mac 安装包。

Windows 1.1 的自由卡片布局、桌面场景编辑、自动化规则执行、任务栏增强和 Windows 厂商私有协议尚未移植，界面不提供这些功能的开关。设置模型保留相关字段，不代表 Mac 已实现对应能力。

## 设备读取边界

USB HID 读取 `/usr/sbin/ioreg` 的系统属性；蓝牙读取 `/usr/sbin/system_profiler SPBluetoothDataType -json` 的当前已连接列表，并以稳定物理标识合并同一设备。IORegistry 中残留的蓝牙节点不能单独证明在线，读取失败或跳过蓝牙查询时不展示这些未确认节点。左右耳和充电盒保留各自标识和百分比，不混成一个读数。

数值来自 macOS 系统报告，可能包含驱动缓存。界面中的读取时间表示本次系统采样时间，不代表设备刚刷新电量。未提供电量、充电或连接证据时保持未知；接收器存在也不代表其无线外设在线。不主动配对、连接离线蓝牙或写入外设配置，尚未实现 BLE GATT 与 Windows 厂商私有电池查询。

## 构建和本地验证

需要 .NET 10 SDK。仓库内已有 SDK 时，Windows 可将以下 `dotnet` 替换为 `.tools/dotnet/dotnet.exe`。

```sh
dotnet build macOS/Perivolt.Mac.csproj
dotnet run --project macOS/Perivolt.Mac.csproj -- --self-check --data-dir artifacts/macOS-check
```

在 Mac 上运行真实应用或导出当前扫描：

```sh
dotnet run --project macOS/Perivolt.Mac.csproj -- --show
dotnet run --project macOS/Perivolt.Mac.csproj -- --scan --output artifacts/mac-scan.json
```

`--no-ble` 会跳过蓝牙系统查询。扫描输出可能含本机设备名称和唯一标识，仅供本地诊断，分享前须自行删除相关内容。

在 Windows 或 Mac 上检查样例界面：

```sh
dotnet run --project macOS/Perivolt.Mac.csproj -- --preview --show
dotnet run --project macOS/Perivolt.Mac.csproj -- --preview --screenshot artifacts/mac-preview.png --theme Ocean --page devices
```

`--page` 支持 `devices`、`settings`、`diagnostics`；`--language` 使用共享语言标识。`--screenshot` 搭配 `--widget-screenshot` 会截取样例桌面卡片。预览使用虚构设备数据，不探测硬件、不联网、不修改登录启动，也不保存设置；预览名称不构成对应型号的兼容性验证。

## 生成开发用 .app

使用 PowerShell 7：

```powershell
./macOS/scripts/Build.ps1 -Runtime osx-arm64
./macOS/scripts/Build.ps1 -Runtime osx-x64
# 在 Mac 上同时生成可拖入“应用程序”的 DMG：
./macOS/scripts/Build.ps1 -Runtime osx-arm64 -Dmg
```

产物位于 `artifacts/macOS-preview/` 对应架构目录，包含自带运行时的 `Perivolt.app`、ZIP 和 SHA256；Mac 上加 `-Dmg` 还会生成 DMG 与 SHA256。打开 DMG 后，把 `Perivolt.app` 拖到其中的 `Applications` 链接，推出磁盘，再从“应用程序”启动。CI 为两种芯片均生成 DMG，并验证挂载、复制安装、卸载镜像后运行应用自检。

在 Mac 构建时，先对嵌套原生代码签名，再以 ad-hoc 开发签名封装完整 `.app`，最后生成归档。ZIP 解压及 DMG 复制后的应用均执行严格签名验证；CI 还检查篡改资源会被拒绝。Windows 跨编译的 ZIP 不进行此 Mac 签名步骤。

应用包结构参照 [Avalonia macOS 部署说明](https://docs.avaloniaui.net/docs/deployment/macos)，DMG 封装参照 [Apple 分发说明](https://developer.apple.com/library/archive/documentation/Porting/Conceptual/PortingUnix/distributing/distibuting.html)。ad-hoc 只验证开发包的代码与资源完整性，不提供 Developer ID 身份或 Apple 公证；DMG 本身仍未签名。CI 将应用与镜像的 Gatekeeper 评估单独保存在 `distribution-assessment.json`，构建通过不表示 Gatekeeper 接受。正式分发仍需 Developer ID 签名、公证和 Finder 首次打开验收。

## 数据、权限和待验证项

设置和本地数据默认位于 `~/Library/Application Support/Perivolt/`，可用 `--data-dir` 指定。登录启动只管理本应用的 `~/Library/LaunchAgents/io.github.yangyingx.perivolt.plist`，需先将 `Perivolt.app` 放在最终安装位置。关闭登录启动不会退出当前应用。

默认不联网，不加载 Windows 插件，不添加全局键鼠钩子，不访问麦克风音频流。更新检查需要用户开启；本轮没有移植 Windows 的诊断自动上报入口。

当前环境为 Windows。构建、自检、合成系统输出解析及样例界面验证不能替代 Mac 实机测试。真实 USB/蓝牙电量对照、菜单栏、通知权限、登录启动、睡眠唤醒、混合 DPI/多屏拖动、`.app` 启动、签名与公证均需进一步验证。

双架构 CI、开发包下载方式及逐项实机验收步骤见 [macOS 适配准备](ADAPTATION.md)。

项目署名与个人非商业许可继续沿用；依赖许可见 [macOS 第三方声明](THIRD-PARTY-NOTICES.macOS.md)及 [LICENSES](LICENSES/)。
