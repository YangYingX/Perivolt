# Perivolt 诊断接收服务

桌面端向此 Worker 提交单独生成的、经过脱敏的 schema v1 报告；Worker 再次验证字段并以 GitHub App 身份在 **YangYingX/Perivolt** 创建 Issue。仓库和 GitHub API 地址写死在服务端。没有个人访问令牌，也不要求最终用户登录 GitHub。

**项目接收服务已部署**：`https://perivolt-diagnostics.perivolt-diagnostics-worker.workers.dev/v1/reports`。已验证健康检查与无效报告拒绝；尚未提交真实诊断或创建测试 Issue。客户端构建需写入此地址才会启用上传。请勿把未脱敏的本地诊断导出文件直接发往此接口。

## 部署

需要 Node.js 22+、Cloudflare 账号，以及有权为目标仓库安装 GitHub App 的账号。在本目录运行：

```powershell
npm install
npx wrangler login
```

1. 在 GitHub Settings → Developer settings → GitHub Apps 创建项目机器人。取消 **Active webhook**；此服务无需回调和用户 OAuth。Repository permissions 只开 **Issues: Read and write**（Metadata 的默认只读权限保留）。
2. 将 App 安装到 **YangYingX/Perivolt**，选择 **Only select repositories**，只选该仓库。记录 App ID 和安装页面 URL 中的 Installation ID。
3. 在 App 左侧 **Credentials → Key pairs → New key** 生成并下载私钥；旧界面可能显示在 General 下的 Private keys。下载的 RSA PEM（PKCS#1）和 PKCS#8 PEM 均支持。私钥仅放到 Cloudflare secret，不放进客户端、Git 或 `wrangler.jsonc`。若要本地调试，用已忽略的 `.dev.vars`；本代码固定正式仓库，运行真实上传会产生公开 Issue，应使用模拟 GitHub 响应进行无副作用的验证。
4. 先部署正式 Worker 代码和 Durable Object migration；首次部署尚无 secret 时，上报接口返回 503，health 为 false。然后通过交互提示依次写入四个 secret。不要在首次 deploy 前运行 secret put/bulk，否则 Wrangler 会自动创建不含本项目代码和绑定的草稿 Worker。`GITHUB_PRIVATE_KEY` 粘贴完整多行 PEM；`IP_HASH_SALT` 使用至少 32 字符的随机值。命令行不包含 secret 的实际值：

```powershell
npm run check
npm run deploy
npx wrangler secret put GITHUB_APP_ID
npx wrangler secret put GITHUB_INSTALLATION_ID
npx wrangler secret put GITHUB_PRIVATE_KEY
npx wrangler secret put IP_HASH_SALT
```

Worker 名为 `perivolt-diagnostics`。`wrangler.jsonc` 使用单个 SQLite-backed Durable Object，并通过 `new_sqlite_classes` 的 `v1` migration 创建存储；后续部署保留此 migration，不能另建随机对象或改为普通 KV 去重。Wrangler 会显示最终域名，例如 `https://perivolt-diagnostics.<你的子域>.workers.dev`；示例地址不可直接用于客户端。

部署完成后，先访问 `GET https://<实际域名>/health`，应返回 `{"configured":true}`。该检查仅确认 secret 格式及 DO binding 存在，不验证 GitHub 权限或私钥是否匹配；只有经过授权的真实手动上传才能验证完整链路，并会创建公开 Issue。

在项目根目录，使用项目构建脚本写入部署后的地址，同时保留插件复制和打包 staging：

```powershell
.\scripts\Build.ps1 -Publish -DiagnosticUploadEndpoint 'https://<实际域名>/v1/reports'
```

随后按当前版本执行项目原有的 `scripts\Build-Installer.ps1` 安装包流程。此地址会嵌入程序元数据；缺省为空时客户端禁用上传。GitHub App 私钥和令牌不会被编译进程序。

## 接口

`POST /v1/reports`，`Content-Type: application/json`，**请求体直接为脱敏报告 JSON**，不是 `{report: ...}` envelope，也不是原始本地日志。仅接收 schemaVersion 1，最大 50 KiB（按实际流字节计），不支持压缩请求。只接收白名单字段和协议/状态枚举，拒绝名称、序列号、路径、任意 URL、自由文本错误、未知属性、不合法数值/引用及未知原始响应。Logitech 仅保留已验证的电池 feature 发现/读取链；耳机/键鼠前缀只允许已知电池消息格式。

新建成功：HTTP 201；七天内同类故障复用：HTTP 200。

```json
{"issueUrl":"https://github.com/YangYingX/Perivolt/issues/123","duplicate":false}
```

错误响应只有固定错误代码：400 无效报告；408 请求超时；413 太大；415 非 JSON/不支持编码；429 限频（带 `Retry-After` 秒数）；502 GitHub 认证或 API 失败；503 缺少配置或服务不可用。不会向客户端转发上游错误内容、令牌或配置值。`GET /health` 仅返回 `configured` 布尔值。

指纹由服务器根据已验证报告重新计算，与客户端算法一致：schema、程序版本、故障/手动模式，以及设备 VID/PID、类别、连接、协议、状态、电量能力（百分比/档位/无），排序并去重后做 SHA-256。不包含设备别名或精确百分比。健康的手动报告根据完整硬件组合区分。相同指纹七天内仅返回原 Issue，不追加评论或覆盖原报告；新版本或故障状态变化会产生新指纹。

## 隐私、限频与运行

- 一个固定名称的 Durable Object 串行完成限频、指纹查询、GitHub 创建和存储，避免并发请求创建重复 Issue。GitHub 安装令牌仅在对象内存中短期缓存，到期或认证失败时清除。
- `CF-Connecting-IP` 仅在 Worker 内存中与 secret 盐哈希；只把哈希传入对象。每个哈希每整点小时最多 5 次有效提交，全局每小时 30 次、UTC 每日 100 次，重复报告也计数。全局拒绝时不创建新的 IP 桶。多人共用出口 IP 会共享配额。
- SQLite 仅保存指纹、脱敏后的 Issue URL、计数和过期时间；不存完整报告、原始 IP、设备序列号或令牌。计数到期清除，指纹七天后清除，DO alarm 负责空闲时清理。公开 Issue 会保留脱敏报告，存储过期不会删除 GitHub 内容。
- Worker 不打印请求/配置/异常内容，且配置禁用了 Workers observability。Cloudflare 自身的基础设施日志与 GitHub 公开内容受各平台政策约束；不要在 Dashboard 另开请求体采集。不要把此接口代理到会保留请求体的服务，也不要通过自定义 Header 传入用户密钥。
- 此接口为桌面匿名上传，内置限频控制 Issue 数量。上线时建议在 Cloudflare WAF 对 `/v1/reports` 再加请求频率规则，并设置预算/使用量告警，避免大量非法流量造成额外运行费用；不要缓存此路径，保持 Cloudflare 原始客户端 IP Header 语义。
- GitHub 故障不会返回成功，也不自动重试创建 Issue。极端情况下 GitHub 已创建但连接中断或对象未持久化结果，随后人工重试仍可能重复；可通过 Issue 中的诊断指纹核对。盐轮换会重置 IP 桶的身份，迁移/删除 DO 存储会丢失去重记录。

项目所有者已完成 App 安装、Cloudflare 授权和 secret 配置；.NET 10 HttpClient 使用系统代理及显式代理均验证健康检查 200、无效报告 400，保持 TLS 校验。尚未在正式仓库发测试 Issue；完整的公开 Issue 创建链路待用户在预览内容后手动上报验证。

参考：[Cloudflare SQLite-backed Durable Objects](https://developers.cloudflare.com/durable-objects/api/sqlite-storage-api/)、[并发控制](https://developers.cloudflare.com/durable-objects/api/state/#blockconcurrencywhile)、[GitHub App JWT](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app)、[Installation token](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation)、[创建 Issue 所需权限](https://docs.github.com/en/rest/issues/issues#create-an-issue)。
