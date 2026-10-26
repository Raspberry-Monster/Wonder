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
| `Chat:TimeZone` | `Chat__TimeZone` | `Asia/Shanghai`，用于时间上下文和日常调度 |
| `Authentication:AllowRegistration` | `Authentication__AllowRegistration` | `true` |
| `Authentication:RequireHttps` | `Authentication__RequireHttps` | `false`；启用时 Cookie 始终带 Secure |

存储配置中的相对路径按进程工作目录解析。IDE、仓库根目录、发布目录的启动方式可能不同；需要固定存储位置时使用绝对路径。词表目录则按 Host ContentRoot 下的 `Tokenizer` 查找。

容器工作目录为 `/app`，宿主 `~/quadlet.config.d/Wonder/Host` 挂载到 `/app/data`，其中 `keys`、`media` 分别保存 Data Protection 密钥和媒体。目录布局见 [Quadlet 指南](../deploy/quadlet/README.md#宿主机目录)。

记忆管理权限见 [Memory 生命周期](memory-lifecycle.md)；工具配置与独立 Notes 授权见 [交互工具与 Notes](agent-tools.md)。

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
5. 保持数据库与原密钥目录，迁移完成后启动新版并检查健康探针、本人身份和任务状态。迁移失败应先修复，再启动普通业务。

迁移链位于 `src/Wonder.Infrastructure/Migrations`，由 EF Core 按数据库记录依次应用。运行时任务只接受事件 v2。隐私与功能精简迁移只允许前向；升级会取消旧叙事／账本导出任务、清空其进度、删除叙事配置及数据表，并将已隔离的隐私操作重新排入在线核验。已有完成操作也按在线目标重新核验，不生成恢复成功回执。

配置五类模型路由：Chat、LightReflection、StrongReflection、RollingSummary、Embedding。缺少路由的任务会失败；修正后可在“状态与任务”重试。触发条件见 [Reflection 与 Memory](reflection-memory.md)。

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

Outbox 是任务状态的权威来源。消费者查询到期候选，再通过资格条件和 LeaseId 的 EF 条件更新竞争领取；租约 10 分钟，每 30 秒续期。空队列等待 1 秒；过期 Running 可以被重新领取。Completed、DeadLetter 和 Cancelled 不会自动执行。

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

任务失败 Warning 记录任务 UUID、类型及错误类型，租约异常也会记录；默认 Information 级别可见。任务列表显示错误类型及是否保存了分批进度。日志不主动记录聊天正文、完整 Prompt、反思结果或密钥。

```bash
# Podman / Quadlet
journalctl --user -u wonder-app.service -n 200 -f
# Compose，在仓库根目录执行
docker compose -f deploy/compose.yaml logs --tail 200 -f app
```

源码或 IDE 运行查看终端、调试输出。按任务 UUID 定位；ModelConfigurationException 常见于缺少路由、端点或供应商拒绝请求，TokenQuotaException 表示周期额度耗尽，ContextLimitException 表示输入或单次额度无法容纳请求。模型错误诊断包含模型、FinishReason、输出上限等信息，供应商错误响应正文不直接写入异常。

`/health/live` 检查进程，`/health/ready` 检查数据库连接。ready 成功不代表 QQ 已登录、模型路由可调用或全部迁移已经应用，仍需分别检查控制台和任务记录。

## 运维备份与恢复

应用不提供备份、删除账本导出、恢复包或恢复重放入口。由运维在应用外使用匹配数据库版本的 PostgreSQL 客户端；应用镜像不再包含 pg_dump、pg_restore 或 age。

备份可在运维终端执行以下命令。连接参数可通过 PGHOST、PGPORT、PGUSER、PGDATABASE 与受限权限的 .pgpass 提供，避免将密码放入命令历史。

~~~bash
pg_dump --format=custom --file=wonder.dump --dbname=wonder
~~~

数据库转储包含在线隐私操作和撤销记录，以及只读 schema retired_context。另行安全保存整个 Storage:KeysPath 目录；API Key、QQ Token 与登录 Cookie 使用 Data Protection，数据库转储不能替代原密钥环。需要保留图片时，另行备份 Storage:MediaPath。备份保留、加密、异机保存和定期恢复演练由运维安排。

恢复步骤：

1. 停止 Host、Worker 和 OneBot，在隔离环境先演练恢复。
2. 准备空的 PostgreSQL + pgvector 数据库，由运维执行：

   ~~~bash
   pg_restore --exit-on-error --no-owner --dbname=wonder_restored wonder.dump
   ~~~

3. 恢复配套密钥目录及需要的媒体，确认运行账号权限，将连接串指向恢复库。
4. 显式执行 dotnet Wonder.Host.dll --migrate，再启动 Host。
5. 检查 /health/ready、登录、本人记忆、隐私清理进度及发送不确定状态。

**恢复后的数据状态以备份时点为准。备份之后的更正、Forget、Erase 或 Notes 删除不会自动重放，旧数据可能重新出现。** 应根据运维恢复范围重新核对并处理；应用不提供后续删除记录的恢复保障。

## 群／共享退役归档

旧群／共享记录保存在只读 schema retired_context.entries，没有应用读取入口，不会自动导入在线表。迁移只允许前向。归档随外部数据库备份保存；需要单独导出时由运维使用 PostgreSQL 工具处理。媒体文件需另行备份。

## 保留与清理

交互、主动整理与 Notes 写工具默认关闭，启用方式及授权见 [交互工具与 Notes](agent-tools.md#启用工具)。迁移统一按停止应用、外部备份、执行迁移、启动应用的流程部署。

数据库撤销记录继续使用 DeletionResourceKind 区分 Memory、Note 和退役上下文 MemoryContext，保留业务栅栏、防回写及审计所需的 ResourceId／ResourceVersion。Notes 与整理原因按本人确认保留到主动删除／清除，不自动过期。

原始消息默认保留 90 天，创建时记录 ExpiresAt，修改 Bot 保留期只影响后续消息；图片缓存 7 天。清理每 5 分钟分批执行，符合条件的完成发送正文按 30 天清理。Memory / Relationship 不随原始消息自动过期。

记忆管理的操作范围见 [Memory 生命周期](memory-lifecycle.md)，自动维护规则见 [自主维护记忆](memory-maintenance.md)。
