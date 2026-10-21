# 群星映色 · 角色主题包 1.1.1

电栖的可选离线插件。包含 109 套基础角色主题与 26 套异格、职业升变及独立身份形态，共 135 个可独立选择的主题。不需要联网、登录或额外运行服务；默认关闭，不随程序安装包发行。

| 游戏 | 数量 | 示例 |
| --- | ---: | --- |
| 明日方舟：终末地 | 19 | 祀、提弗洛斯、梨诺、诀、卡缪、弭弗 |
| 明日方舟 | 74 | 阿米娅三职业、陈三形态、凯尔希、斯卡蒂、德克萨斯、令 |
| 绝区零 | 26 | 安比、零号·安比、比利、星徽·比利、艾莲、星见雅 |
| 少女前线2：追放 | 16 | 黛烟、琼玖、索米、玛绮朵、可露凯 |

每条记录在 `Themes.txt` 单独指定强调色、辅助色、底色、深浅方向和原创配色名称。运行时从这些色种生成 12 个宿主颜色资源，并调整文字和强调色对比度。没有本地图片时使用电路、晶片、节拍、准星四组原创矢量图案；有图片时，列表显示对应角色的立绘缩略图。

异格、职业升变与独立身份形态是单独的主题选项，排列在对应原角色之后；搜索原角色名也能找到这些形态。每个选项使用自己的稳定主题 ID、配色、背景图片和透明度设置，切换形态不会覆盖其他形态的设置。例如，陈、假日威龙陈、赤刃明霄陈可以分别选用；阿米娅的术师原形态、近卫升变和医疗升变也各自保留设置。泥岩本次使用无头盔精二形象，沿用 `arknights-mudrock` 原选项，不额外增加形态条目。

已发布的旧版插件 ZIP 包含 `character-themes/` 下的 `plugin.json`、`PowerHub.CharacterThemes.dll` 和本说明 `README.md`，共三个文件；新独立构建另附许可与署名声明。解压到程序旁的 `plugins` 目录。运行需要前两个文件，`Themes.txt` 已嵌入 DLL。从插件页开启，在“设置 → 通用 → 插件主题”筛选游戏、输入角色名或英文 ID 片段并点击选用。列表采用可见条目虚拟化，缩略图最长边 256 像素、最多缓存 16 张；搜索不扫描磁盘。关闭主题包后不会影响系统主题和其他主题插件。

源码构建：在仓库根目录运行 `scripts\Build-Plugin.ps1 -Project Extensions\CharacterThemes\CharacterThemes.csproj`，独立 ZIP 与 SHA256 输出到 `artifacts/plugins/`，不进入默认安装包或便携包。插件总目录与后续可选插件规划见 [Extensions](../README.md)。

终末地名单核对至 2026-10-20，包含截至「祀」的 12 位特许寻访六星角色：莱万汀、洁尔佩塔、伊冯、洛茜、汤汤、庄方宜、弭弗、卡缪、诀、梨诺、提弗洛斯、祀。19 套主题均可匹配个人单独准备的本地立绘。

角色名字保留中文，游戏分组和控件支持应用的四种语言。没有附带游戏立绘、官方 Logo、语音、字体；配色及几何装饰为本项目设计。属于非官方同好主题，游戏及角色权利归各权利方。已发布的 1.1.1 包及主项目 v1.0.5 及以前版本的 MIT 授权保留；许可变更后的新增原创修改按根目录 [LICENSE](../../LICENSE) 提供，不覆盖游戏角色和商标等第三方权利。开发者：阳影X（YangYingX），来源：[YangYingX/Perivolt](https://github.com/YangYingX/Perivolt)。

## 独立本地立绘

1.1.1 新生成背景的目录为 `Perivolt-CharacterBackgrounds-1.1.1-LocalOnly/artwork`，独立本地包包含 135 张成品背景及其索引与提示词，不含插件 DLL。旧的 `Perivolt-CharacterArtwork-LocalOnly` 是绘制时核对身份的原设参考包；查看本轮生成效果时请选择新版背景目录。

在主题列表选中角色或形态后，点击“选择立绘文件夹”，指定独立本地包的 `artwork` 目录。文件按完整主题 ID 命名，例如 `endfield-si.png`、`arknights-amiya.png`、`arknights-amiya-guard.png`、`arknights-amiya-medic.png`、`arknights-chen-dawnstreak.png`，支持 PNG/JPEG。可为当前选项单独选图、切换显示、设置透明度与左中右位置；这些设置按主题 ID 各自保存。立绘继续保存在本地独立包，不随程序安装包、公开仓库或发行附件分发；应用不联网获取图片，个人准备的素材不受本仓库许可覆盖。

名单核对来源（开发时参考，运行时不会访问）：

- [终末地官方干员情报](https://endfield.hypergryph.com/operator)
- [明日方舟官方干员活动名单](https://ak.hypergryph.com/news/7306)
- [终末地「丹青渡」版本公告](https://endfield.hypergryph.com/news/6094)
- [绝区零官方角色页](https://zenless.hoyoverse.com/zh-cn/character/detail?id=155657)
- [少女前线2：追放官方人形名单](https://gf2.haoplay.com/tw/roleinfo/allroles.html)
