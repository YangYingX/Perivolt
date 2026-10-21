# 电栖插件

开发者：[阳影X（YangYingX）](https://github.com/YangYingX)。此目录统一存放官方插件源码与说明；主程序的插件契约位于 [Plugins](../Plugins)，用户安装的 DLL 则放在运行程序旁的 `plugins/<插件ID>/`。

## 可选插件

可选插件独立构建、独立分发，**不进入默认插件包、安装包或便携包**。用户自行安装并启用；未安装时不运行相关功能，未启用时宿主不加载插件 DLL。后续任务栏增强及用电趋势均按此方式开发。

| 插件 | 源码位置 | 当前状态 |
| --- | --- | --- |
| 群星映色 | [CharacterThemes](CharacterThemes) | 已实现，135 套配色主题，独立分发 |
| 任务栏增强 | [Optional 开发约定](Optional) | 待开发：悬停多设备概览、滚轮切换、读数状态说明及多显示器增强 |
| 用电趋势 | [Optional 开发约定](Optional) | 待开发：本地电量历史与趋势展示 |

待开发条目尚无可安装的 DLL。新插件源码放在 `Optional/<插件项目名>/`；群星映色保留已有路径，避免影响现有构建引用。插件完成后在上表补充自己的 README、兼容版本和下载入口。

```powershell
# 在仓库根目录执行，只生成所选插件的独立 ZIP 和 SHA256
.\scripts\Build-Plugin.ps1 -Project .\Extensions\CharacterThemes\CharacterThemes.csproj
```

输出位于 `artifacts/plugins/`，不写入主程序的默认 `plugins` 输出目录。发布时将插件 ZIP 与同名 `.sha256` 作为独立附件上传到 GitHub Release，不放入主程序安装包或便携 ZIP。打包不会自动上传文件。安装方法与 API 见 [插件文档](../docs/plugins.md)。

## 现有随包插件

| 项目 | 插件 ID | 默认启用 |
| --- | --- | --- |
| [VendorProtocols](VendorProtocols) | `vendor-protocols` | 是，匹配设备时按需加载 |
| [Essentials](Essentials) | `aurora-theme`、`full-charge`、`quiet-hours` | 否 |

`scripts/Build.ps1` 只构建并复制这两个明确列出的项目及上述四个插件；新增项目不会因为放进此目录而自动随包发布。安装器也只收录这四个插件。修改默认清单须单独评估，不能将可选增强塞入现有随包 DLL。

## 署名与许可

分发时保留插件说明、开发者和来源，以及适用的许可与第三方声明。根目录的 [LICENSE](../LICENSE)、[NOTICE.md](../NOTICE.md)、[AUTHORS.md](../AUTHORS.md)、[历史 MIT 许可](../LICENSES/MIT-legacy.txt)及[第三方声明](../THIRD-PARTY-NOTICES.md)由独立打包脚本附带。历史 MIT 授权和第三方权利继续有效；不打包用户设置、诊断、本地立绘或其他个人素材。
