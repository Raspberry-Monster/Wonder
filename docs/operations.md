# 运行、更新、备份与恢复

首次本地启动和 Compose 命令见 [主 README](../README.md)，rootless Podman 见 [Quadlet 指南](../deploy/quadlet/README.md)。两种容器部署方式选择其一；完整应用按单 Host 运行。

## 配置与持久目录

配置可以写入 Host 的 `appsettings.Local.json` 或环境变量。环境变量使用双下划线表示层级。

| 配置键 | 环境变量 | 默认值或用途 |
| --- | --- | --- |
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | 必填，PostgreSQL + pgvector 数据库 |
| `Workers:Enabled` | `Workers__Enabled` | `true`；关闭时不启动 OneBot、Outbox 消费者和 Quartz |
| `Storage:KeysPath` | `Storage__KeysPath` | `data/keys`；Data Protection 密钥环 |
| `Storage:MediaPath` | `Storage__MediaPath` | `data/media` |
| `Backups:Path` | `Backups__Path` | `data/backups` |
| `Backups:DeletionLedgerPath` | `Backups__DeletionLedgerPath` | `data/deletion-ledger` |
| `Backups:RetiredContextPath` | `Backups__RetiredContextPath` | `data/retired-context`，退役数据的独立加密导出目录 |
| `Backups:AgeRecipient` | `Backups__AgeRecipient` | age 公钥收件人，备份和账本导出需要 |
| `SystemAdminIds` | `SystemAdminIds__0` 等 | 可执行备份 API 的账号 UUID |
| `Chat:TimeZone` | `Chat__TimeZone` | `Asia/Shanghai`，用于时间上下文和日常调度 |
| `Authentication:AllowRegistration` | `Authentication__AllowRegistration` | `true` |
| `Authentication:RequireHttps` | `Authentication__RequireHttps` | `false`；启用时 Cookie 始终带 Secure |

存储配置中的相对路径按进程工作目录解析。IDE、仓库根目录、发布目录的启动方式可能不同；需要固定存储位置时使用绝对路径。词表目录则按 Host ContentRoot 下的 `Tokenizer` 查找。

容器工作目录为 `/app`，宿主 `~/quadlet.config.d/Wonder/Host` 挂载到 `/app/data`，其中 `keys`、`media` 和 `backups` 分别保存密钥、媒体和备份。删除账本另挂载到 `/app/data/deletion-ledger`。目录布局见 [Quadlet 指南](../deploy/quadlet/README.md#宿主机目录)。

## 首次部署和更新应用

1. 首次部署准备 .NET 10、Node.js 24、PostgreSQL 18 + pgvector，或使用仓库容器配置。数据库账号需要执行迁移及创建 vector 扩展的权限。
2. 更新时停止当前 Host，包括 IDE 调试进程和后台任务；备份数据库、密钥和必要的媒体文件。
3. 构建前端及应用。容器构建包含这些步骤；源码运行可执行：

   ```powershell
   npm --prefix src/Wonder.Web ci
   npm --prefix src/Wonder.Web run build
   dotnet restore Wonder.slnx
   dotnet run --project src/Wonder.Host -- --migrate
   ```

4. 发布包使用 `dotnet Wonder.Host.dll --migrate`；容器使用对应指南中的手动迁移入口。普通启动不会自动执行迁移，不要让旧 Host 同时使用正在升级的数据库。
5. 保持原数据库连接及密钥目录，启动新版，检查 `/health/ready`、登录、模型路由和任务状态。迁移失败时先修复，不启动应用。

迁移链位于 `src/Wonder.Infrastructure/Migrations`，由 EF Core 按数据库记录依次应用。运行时任务只接受事件 v2。需要回退时，停止应用并使用与旧版匹配的数据库备份；不能假定 Down 迁移无损保留新版字段、任务进度和配置。

配置六类模型路由：Chat、LightReflection、StrongReflection、DailyNarrative、RollingSummary、Embedding。缺少路由的任务会失败；修正后可在“状态与任务”重试。触发条件见 [Reflection 与 Memory](reflection-memory.md)。

## 密钥解密失败

日志出现 `CryptographicException: The key {...} was not found in the key ring`，表示当前密钥环缺少用于加密已有数据的密钥。数据库备份或发布目录本身不能替代密钥目录。

1. 确认当前 `Storage:KeysPath` 实际指向的绝对路径。
2. 在原运行环境或备份中找到整个密钥目录，核对日志里的 `key-{UUID}.xml`。
3. 停止 Host，将原密钥文件补充到当前目录，保留现有密钥，确保运行账号可读，然后重启。
4. 如果原密钥不可恢复，重新填写受影响的模型 API Key、QQ 令牌并重新登录。新生成的密钥无法解开旧密文。
5. 修复后重试 DeadLetter 任务；Pending 任务会按计划继续。

Windows PowerShell 可在启动前固定目录，例如：

```powershell
$env:Storage__KeysPath = 'D:\WonderData\keys'
dotnet run --project src/Wonder.Host
```

Data Protection 使用应用名 `Wonder`。迁移机器时，数据库和原密钥目录需要配套保留；密钥目录应私有持久化，并另行加密备份。

## 任务失败与重试

Outbox 是任务状态的权威来源。消费者查询到期候选，再通过资格条件和 LeaseId 的 EF 条件更新竞争领取；租约 10 分钟，每 30 秒续期。空队列等待 1 秒；过期 Running 可以被重新领取。Completed 与 DeadLetter 不会自动执行。

| 情况 | 当前处理 |
| --- | --- |
| 普通失败 | Chat 最多 2 次，其他任务最多 5 次；指数退避最高 5 分钟 |
| 配置错误、必要上下文超限、输出截断、不确定发送 | 进入 DeadLetter；摘要和反思流程可在内部缩小输入重试后再上报截断 |
| 后台任务日 / 月额度耗尽 | 排到下一 UTC 配额周期，保留进度，不消耗失败重试次数 |
| Chat 额度耗尽 | 进入 DeadLetter，并排入额度提示回复 |
| 供应商要求长时间等待 | 按 Retry-After 重新排队，仍受任务尝试次数限制 |
| 分批任务成功但尚未处理完 | 保存 ProgressJson 后重新排队，不消耗失败重试次数 |

模型请求失败也可能产生费用。供应商明确拒绝或要求延迟的请求释放预留；响应丢失等不确定情况按预留上限估算结算。上下文、输出和配额诊断见 [统一上下文预算](context-budget.md)。

在控制台“状态与任务”重试死信，会重置尝试次数、错误和租约，并保留已有分批进度。API 为 `POST /api/v1/bots/{id}/jobs/{jobId}/retry`，需登录并携带 `X-Wonder: 1`。

不确定发送先处理分段，再重试 outgoing。`confirm-sent` 确认已发送，可补平台消息 ID；`retry` 允许重新发送，可能重复。恢复时尚为 Sending 的分段会转为 DeliveryUnknown，需先去 QQ 核对。

## 日志与健康检查

任务失败和租约异常的 Warning 包含任务 UUID、异常消息和堆栈；默认 Information 级别可见。任务列表只存错误类型，另显示进度是否存在。日志不主动记录聊天正文、完整 Prompt、反思结果或密钥。

```bash
# Podman / Quadlet
journalctl --user -u wonder-app.service -n 200 -f
# Compose，在仓库根目录执行
docker compose -f deploy/compose.yaml logs --tail 200 -f app
```

源码或 IDE 运行查看终端、调试输出。按任务 UUID 定位；ModelConfigurationException 常见于缺少路由、端点或供应商拒绝请求，TokenQuotaException 表示周期额度耗尽，ContextLimitException 表示输入或单次额度无法容纳请求。模型错误诊断包含模型、FinishReason、输出上限等信息，供应商错误响应正文不直接写入异常。

`/health/live` 检查进程，`/health/ready` 检查数据库连接。ready 成功不代表 QQ 已登录、模型路由可调用或全部迁移已经应用，仍需分别检查控制台和任务记录。

## 手动备份

应用镜像内包含 PostgreSQL 18 客户端及 age。源码运行时需自行提供 `pg_dump` 和 age。使用 age-keygen 生成密钥，仅将公开 `age1...` 收件人填入 `Backups:AgeRecipient`，私钥另存。

将管理员账号 UUID 写入 SystemAdminIds。登录后调用 `POST /api/v1/system/backups`，携带 `X-Wonder: 1`。接口将 pg_dump 自定义格式流式 age 加密，并输出独立的加密删除账本，不写明文数据库转储。Data Protection 密钥和媒体文件需另外备份。

配置收件人后，删除会排入账本导出任务，也可调用 `POST /api/v1/system/deletion-ledger` 手动导出。默认账本仍在同一主机，应另行安排异盘或异机保存，并关注失败和积压。

## 恢复流程

1. 停止 Host，保留最新独立删除账本和密钥，暂不启动 OneBot 或消费者。
2. 用 age 私钥解密数据库备份，在空的 PostgreSQL + pgvector 数据库中用 pg_restore 恢复。
3. 恢复原密钥及必要媒体，确认目录权限，显式执行 `--migrate`。
4. 将最新删除账本解密到私有临时文件。旧文件先通过 `dotnet Wonder.Host.dll --convert-ledger /private/old.json /private/current.json` 显式转换（不覆盖原件）；再运行 `dotnet Wonder.Host.dll --replay-ledger /private/latest-ledger.json`。该入口不启动 HTTP、OneBot 或消费者，且可重复执行。
5. 验证撤销记忆、向量和派生内容不可检索，再启动 Host，处理不确定发送和死信。
6. 检查 ready、任务、本人记忆及模型配置，再恢复流量。

恢复点之后的删除只有在独立账本已成功保存且可取得时才能重放；数据库与最新账本同时丢失时不能保证保留这些删除记录。

## 群／共享退役归档

用户已授权完整归档后退出在线表。停止 Host 并先备份，再依次应用 TypedBusinessTags、ArchiveRetiredContext、PrivateMemoryOnly。归档记录保存在独立只读 schema `retired_context.entries`，没有应用读取入口；迁移不可通过降级恢复旧群功能。运行 `dotnet Wonder.Host.dll --export-retired-context`，使用 Backups:AgeRecipient 流式加密导出到 Backups:RetiredContextPath，输出文件名与 SHA-256；请将密文另存异盘／异机。命令不启动 HTTP 或 Worker。旧备份和删除账本不删除；归档不会被恢复入口自动导入在线表。

## 保留与清理

ADR-013 新增迁移应按原 stop-backup-migrate-start 流程部署。`Chat__InteractionToolsEnabled`、`Chat__ReflectionToolsEnabled`、`Chat__NotesToolsEnabled` 默认 false；需显式开放模块并配置 Chat SupportsTools。Notes 还需本人独立授权。未对旧只读设置自动追加写权限。

删除账本使用 DeletionResourceKind 区分 Memory、Note 和退役上下文 MemoryContext；不存在产品层的 Memory v1 / Note v2。旧格式只在离线转换器处理，在线重放拒绝未转换文件。Note 的 ResourceVersion 是实际修订号，继续保留。恢复仍须在任何 Worker/OneBot/外部查询前重放最新账本。Notes 与整理原因按本人确认保留到主动删除/清除，不自动过期。验收边界见 [实施附录](adr-013-implementation.md)。

原始消息默认保留 90 天，创建时记录 ExpiresAt，修改 Bot 保留期只影响后续消息；图片缓存 7 天。清理每 5 分钟分批执行，符合条件的完成发送正文按 30 天清理。Memory / Relationship 不随原始消息自动过期。

记忆更正、撤回和删除的范围见 [跨群记忆](shared-context.md) 及 [自主维护记忆](memory-maintenance.md)，整体结构见 [核心架构](architecture.md)。
