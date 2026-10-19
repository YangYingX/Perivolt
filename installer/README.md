# PowerHub 安装器

先运行 `scripts\Build.ps1 -Publish`，确认 `dist\PowerHub` 包含发布程序和第三方声明，再运行 `scripts\Build-Installer.ps1`。本地编译器位置为 `.tools\inno\compiler\ISCC.exe`；脚本只编译，不运行安装器或应用。

按当前用户安装，优先推荐 D: 至 Z: 中可用的非系统固定磁盘（如 `D:\Apps\PowerHub`）。目录选择页始终显示；已有非系统盘安装路径可以沿用。没有可用候选时必须手动选择目录，静默安装需要明确提供 `/DIR="路径"`，不会自动回退到 C 盘。程序无需管理员权限，所选目录需可写。

支持 Windows 10 build 19041 及更新版本的 x64 兼容系统。桌面快捷方式和开机启动均为可选项。设置独立保存到 `%LOCALAPPDATA%\PowerHub`；卸载删除已安装程序和 PowerHub 启动项，保留用户设置。

简体中文界面由本项目编写必要消息覆盖，`ChineseSimplified.isl` 中明确标注来源；罕见错误使用 Inno Setup 原始消息回退。这不是官方中文翻译。安装引擎来自 [Inno Setup](https://jrsoftware.org/)，保留其自身版权信息。
