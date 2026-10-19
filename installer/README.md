# PowerHub 安装器

先运行 `scripts\Build.ps1 -Publish`，确认 `dist\PowerHub` 包含发布程序和第三方声明，再运行 `scripts\Build-Installer.ps1`。本地编译器位置为 `.tools\inno\compiler\ISCC.exe`；脚本只编译，不运行安装器或应用。

默认按当前用户安装到 `%LOCALAPPDATA%\Programs\PowerHub`，支持 Windows 10 build 19041 及更新版本的 x64 兼容系统。桌面快捷方式和开机启动均为可选项。卸载删除已安装程序和 PowerHub 启动项，保留独立存放的用户设置。

简体中文界面由本项目编写必要消息覆盖，`ChineseSimplified.isl` 中明确标注来源；罕见错误使用 Inno Setup 原始消息回退。这不是官方中文翻译。安装引擎来自 [Inno Setup](https://jrsoftware.org/)，保留其自身版权信息。
