# 外设快捷中枢

开发者：**阳影X（YangYingX）**。独立可选插件 `peripheral-hub`，体验版 **1.0.0-preview.2**，需要电栖 **1.0.8 或以上 / 交互插件 API 2**。不进入默认插件、主程序安装包或便携包。

preview.2 修正音频输出设备集合的 COM 接口标识，解决 preview.1 的输出列表为空及 `E_NOINTERFACE (0x80004002)` 错误。实机验证要求输出列表无错误、包含当前默认输出且能显示真实设备名称；返回空列表或错误快照不再视为枚举通过。

## 使用

将独立 ZIP 解压到软件“插件”页打开的目录，保留 `peripheral-hub/` 子目录；点击“重新加载”，启用“外设快捷中枢”。可从插件页“打开”，或使用默认快捷键 **Ctrl+Alt+P**。面板已获得焦点时，再按一次快捷键收起；**Esc** 关闭面板（输出选择列表展开时，先收起列表）。

在插件选项中修改 `hotkey`，例如 `Ctrl+Shift+Space`，按 Enter 或离开输入框保存，立即生效。支持 Ctrl、Alt、Shift 与一个按键；不注册 Windows 组合键或 F12。留空关闭快捷键；组合被占用或无效时在面板底部提示，仍可从插件页打开。若其他程序释放了占用的快捷键，重新启用本插件即可重试。

- **外设电量**：使用主程序已有的设备快照，保留名称、百分比或原生档位，标明充电、连接和采样状态。未知电量显示 `—`，离线时不把旧数值当作当前电量；旧读数和 Windows 系统报告单独注明。不会额外轮询外设或补造读数。
- **声音输出**：选择 Windows 默认输出设备，调整默认多媒体输出音量、切换输出静音。选择输出会同步系统、多媒体和通信三个默认角色；固定使用特定设备的应用仍受其自身设置控制。
- **麦克风**：显示并控制 Windows **默认通信麦克风**的端点静音。不录音、不访问麦克风音频流；不代表各软件自己的静音按钮或物理麦克风开关。
- **声音设置**：无法直接操作时可打开 Windows 声音设置。无可用设备或读数失败时，对应控件禁用并说明原因。

面板可见时每两秒读取一次系统音频状态；隐藏、最小化或关闭后停止轮询。音量拖动合并短时间内的连续调整；写入前核对设备身份，避免延迟操作改到刚切换的设备。停用、重载和退出时释放快捷键、窗口、计时器及 COM 对象。不需要网络、外部运行时依赖或管理员权限，不在插件目录写入设备身份或音频历史。

## 构建与验证范围

在仓库根目录执行：

```powershell
.\scripts\Build-Plugin.ps1 -Project .\Extensions\Optional\PeripheralHub\PeripheralHub.csproj
```

生成 `artifacts/plugins/Perivolt-Plugin-peripheral-hub-1.0.0-preview.2.zip` 及同名 `.sha256`。ZIP 仅含插件、清单、说明及署名和许可，不附带主程序、运行时、测试或个人设置。主程序仍维持原默认插件白名单。

音量、静音和设备枚举使用 Windows Core Audio。默认输出切换使用隔离封装的**未公开 PolicyConfig COM 接口**，不同 Windows 构建或驱动可能不支持；插件会核验三个角色的结果，失败时尝试恢复原设置，并提供系统声音设置入口。实际跨设备切换、驱动异常和热插拔组合仍需更多实机覆盖；当前界面以中文为主。

技术依据：[默认音频端点与角色](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint)、[端点音量与静音](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume)、[系统快捷键](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)。PolicyConfig 不属于这些公开接口的保证范围。

分发时保留仓库附带的 `LICENSE`、`NOTICE.md`、`AUTHORS.md`、`LICENSES/MIT-legacy.txt` 和 `THIRD-PARTY-NOTICES.md`；现行非商业许可、历史 MIT 许可及第三方权利边界保持不变。
