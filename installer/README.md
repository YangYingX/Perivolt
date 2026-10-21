# 电栖 · Perivolt 安装器

先运行 `scripts\Build.ps1 -Publish`，确认 `dist\PowerHub` 包含发布程序和第三方声明，再运行 `scripts\Build-Installer.ps1`。本地编译器位置为 `.tools\inno\compiler\ISCC.exe`；脚本只编译，不运行安装器或应用。

当前正式版为 `1.0.7`，安装包文件名为 `Perivolt-Setup-1.0.7-x64.exe`；安装器显示项目根目录的个人非商业许可，发布者署名为“阳影X (YangYingX)”。构建脚本会核对载荷中的许可、署名和第三方声明与源码一致，避免用旧载荷打包新许可。`LICENSE`、`NOTICE.md`、`AUTHORS.md` 和 `LICENSES/MIT-legacy.txt` 随包保留；1.0.5 及以前的发行文件和原 MIT 授权保持不变。

当前源码构建版本为 `1.0.8-preview.1`，安装器脚本默认生成同版本体验包，尚未公开发布。该预览包增加 API 2 最小接口；两个交互插件仍单独打包，默认安装器不附带。历史正式发行文件保持不变。

按当前用户安装，优先推荐 D: 至 Z: 中可用的非系统固定磁盘（如 `D:\Apps\Perivolt`）。目录选择页始终显示；已有非系统盘安装路径可以沿用。没有可用候选时必须手动选择目录，静默安装需要明确提供 `/DIR="路径"`，不会自动回退到 C 盘。程序无需管理员权限，所选目录需可写。

支持 Windows 10 build 19041 及更新版本的 x64 兼容系统。桌面快捷方式和开机启动均为可选项。设置独立保存到 `%LOCALAPPDATA%\PowerHub`；卸载删除已安装程序和 PowerHub 启动项，保留用户设置。

简体中文界面由本项目编写必要消息覆盖，`ChineseSimplified.isl` 中明确标注来源；罕见错误使用 Inno Setup 原始消息回退。这不是官方中文翻译。安装引擎来自 [Inno Setup](https://jrsoftware.org/)，保留其自身版权信息。

安装载荷改为自带运行时的文件夹部署，首次运行不依赖单文件临时解压。快捷方式使用 --show，安装完成页可启动面板。Diagnose-Startup.cmd 随安装包提供；托管启动错误写入本地日志，运行时加载错误通过脚本收集。预览构建须给 Build-Installer.ps1 提供与载荷一致的版本号，脚本会拒绝版本错配。

默认构建和安装器仅收录 `vendor-protocols`、`aurora-theme`、`full-charge`、`quiet-hours` 四个插件。安装器排除整个插件目录的通配复制，再逐项收录白名单清单与 DLL，因此输出目录中额外的可选插件也不会被安装器带入。群星映色及后续任务栏增强、用电趋势均独立分发，不进入默认安装包或便携包；源码与独立打包方法见[插件目录](../Extensions/README.md)。用户已自行安装的插件和原有设置继续保留。
