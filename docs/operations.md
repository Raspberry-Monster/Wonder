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
| `Chat:InteractionToolsEnabled` | `Chat__InteractionToolsEnabled` | `true`；允许向合格 Chat 回合提供交互工具 |
| `Chat:ReflectionToolsEnabled` | `Chat__ReflectionToolsEnabled` | `true`；允许主动整理工具 |
| `Chat:NotesToolsEnabled` | `Chat__NotesToolsEnabled` | `true`；允许 Notes 工具，仍需本人独立授权 |
| `Authentication:AllowRegistration` | `Authentication__AllowRegistration` | `true` |
| `Authentication:RequireHttps` | `Authentication__RequireHttps` | `false`；启用时 Cookie 始终带 Secure |

存储配置中的相对路径按进程工作目录解析。IDE、仓库根目录、发布目录的启动方式可能不同；需要固定存储位置时使用绝对路径。词表目录则按 Host ContentRoot 下的 `Tokenizer` 查找。

容器工作目录为 `/app`，宿主 `~/quadlet.config.d/Wonder/Host` 挂载到 `/app/data`，其中 `keys`、`media` 分别保存 Data Protection 密钥和媒体。目录布局见 [Quadlet 指南](../deploy/quadlet/README.md#宿主机目录)。

记忆管理权限见 [Memory 生命周期](memory-lifecycle.md)；工具配置与独立 Notes 授权见 [交互工具与 Notes](agent-tools.md)。

## 内部自部署验收

Wonder 的主要使用方式是个人自部署，面向单 Host、少量可信用户。使用仓库 Docker Compose 或 rootless Quadlet 即可运行应用、PostgreSQL + pgvector 和 SnowLuma，不需要独立调度数据库。首次部署按以下顺序验收：

1. 修改数据库密码和端口绑定。仓库 SnowLuma 管理端口使用 `192.168.1.102`，必须替换为宿主机实际 IP；Wonder 5080 默认只绑定回环地址，远程管理可通过 SSH 转发。
2. 持久化数据库与 Host 数据目录，确认应用用户可写密钥和媒体；按下节在空库上显式执行 `--migrate`，成功后启动应用。Compose 和 Quadlet 选择其一，使用一个 Host。
3. 检查 `/health/live`、`/health/ready` 和控制台登录。配置五类模型路由、QQ 账号、OneBot 正向 WebSocket 与 Access Token，检查实际私聊收发；ready 只证明数据库可连接。
4. 按需启用 Chat 的“支持工具调用（聊天）”，通过 `/bind` 绑定本人，并分别开启长期记忆和 Notes 授权。确认 `/help`、`/end` 不产生 Chat 任务，`/end` 不出现在模型历史中；此前聊天内容仍会正常参与分段结束整理。
5. 启用记忆时，发送可验证的简单事实，检查反思、向量和维护任务；发送图片验证目标环境的 DNS、FakeIP 与 HTTPS 连接。检查“运行状态”中是否存在持续失败或死信。
6. 重启 Host，验证控制台登录、密钥解密、QQ 重连和待处理任务恢复；开始长期使用前，保存数据库与配套密钥备份，并在隔离库验证一次恢复。已有任务可以恢复，停机期间未接收的 QQ 消息不会自动补拉。

前端构建、协议测试和 SQLite 集成测试可验证应用逻辑；真实 PostgreSQL 的迁移、锁和向量查询需要隔离 PG 测试。QQ 登录与回执、供应商能力、Linux 容器启动及目标机器资源用量仍以上述实际验收为准。检查方法见 [测试指南](../tests/README.md)。

## 图片下载与网络

图片地址仍要求 HTTPS、443 端口且不能带用户名信息，不跟随重定向。下载前保留 DNS 解析及空结果检查，但不再要求解析结果为公网地址；内网和 FakeIP 地址可直接用于连接，`MediaStore.IsPublic` 判断方法仅保留供后续使用。

媒体客户端关闭显式代理，不使用 `HTTP_PROXY` / `HTTPS_PROXY`；需要代理的环境应提供可用的直连或 TUN／透明代理网络路径。FakeIP 对应的连接仍需由该网络正确接管，TLS 证书校验仍按正常 HTTPS 执行。允许内网目标也意味着图片下载不再提供内外网地址隔离，适用范围是可信的自部署输入。

单张上限 5 MiB，每轮最多 3 张，并检查图片格式与像素数。模型需声明支持图片。失败时先检查实际 HTTPS 地址、DNS 和代理路径，再检查模型能力与后台任务错误；允许 FakeIP 不等于保证任意代理配置都可下载。

## 数据库基线与首次初始化

当前只有一条 `InitialCreate` 迁移，位于 `src/Wonder.Infrastructure/Migrations`，包含当前业务表、Identity、pgvector、索引和约束。首次应用这条基线需要空库；不创建 `retired_context` 或 Quartz 表。

使用旧迁移链的开发库不能直接升级到此基线。开发者需自行备份、重建开发库并修改连接串；应用不提供旧库转换、导入或回填，也不会自动删除数据库。以下初始化命令不承担重建旧库的工作。

1. 准备 .NET 10、Node.js 24、PostgreSQL 18 + pgvector，或使用仓库容器配置。数据库账号需要执行迁移及创建 vector 扩展的权限。
2. 停止当前 Host，包括 IDE 调试进程和后台任务，确认连接串指向准备好的空库。
3. 构建前端及应用。容器构建包含这些步骤；源码运行可执行：

   ```powershell
   npm --prefix src/Wonder.Web ci
   npm --prefix src/Wonder.Web run build
   dotnet restore Wonder.slnx
   dotnet run --project src/Wonder.Host -- --migrate
   ```

4. 发布包使用 `dotnet Wonder.Host.dll --migrate`；容器使用对应指南中的手动迁移入口。普通启动不会自动执行迁移，不要让其他 Host 同时使用正在初始化的数据库。
5. 初始化完成后启动 Host，检查健康探针，再创建账号、Bot 并配置模型路由和 QQ 连接。迁移失败应先修复，再启动普通业务。

已经应用相同基线的数据库可以重复运行 `--migrate`；EF Core 根据迁移历史跳过已应用项，不重建表或清空数据。更新使用同一基线的应用时，先停止 Host、备份数据库和配套文件，再构建、检查迁移并启动。容器命令见 [主 README](../README.md#更新已有部署)；旧迁移链的数据库不适用该更新流程。

运行时仅接受事件 v2 和当前注册的任务类型，没有旧任务升级或取消兼容流程。

配置五类模型路由：Chat、LightReflection、StrongReflection、RollingSummary、Embedding。缺少路由的任务会失败；修正后可在 Bot 的“运行状态”重试。触发条件见 [Reflection 与 Memory](reflection-memory.md)。

## 调度器与重启恢复

保持单 Host 部署。`Workers:Enabled=true` 时启动 OneBot 连接、Outbox 消费者和 Quartz；设为 `false` 时这三部分均不启动，HTTP 服务仍可运行。

Quartz 显式使用内存存储，每次启动注册三个 Job 和四个触发器：

| 触发器 | 启动与周期 | 执行内容 |
| --- | --- | --- |
| `sessions-interval` | 启动后立即执行，此后每 15 秒 | 扫描到期会话、排入符合条件的 Full 反思并检查会话关闭条件 |
| `retention-interval` | 启动后立即执行，此后每 5 分钟 | 分批清理到期消息、媒体、投影和其他到期缓存 |
| `daily-cron` | `Chat:TimeZone` 的每日零点 | 检查启用 Bot 当天的心境维护任务 |
| `daily-reconcile` | 启动后立即执行，此后每 5 分钟 | 补排当天缺少的心境维护任务，也覆盖当天新启用的 Bot |

这些触发器不写入数据库，不需要独立调度库或 Quartz SQL 初始化。停机期间不会保存并逐次重放触发；重启后重新扫描到期状态，只补排当前当地日期的每日维护，不为停机期间每一天建任务。

业务任务、状态、租约和 `ProgressJson` 保存在 PostgreSQL Outbox。重启后，已入队任务继续按到期时间和租约规则领取；过期 Running 可以恢复，DeadLetter 仍需人工重试。每日维护按 Bot、任务类型和当地日期查询去重，并有数据库唯一索引；当天已有任务即不再补排，包括已完成或失败的任务。维护任务执行时仅推进模拟心境，不调用模型。

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

在控制台 Bot 的“运行状态”重试死信，会重置尝试次数、错误和租约，并保留已有分批进度。API 为 `POST /api/v1/bots/{id}/jobs/{jobId}/retry`，需登录并携带 `X-Wonder: 1`。

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

当前使用 Quartz 4 的内存存储，不需要调度表。若仍出现 `Database schema validation failed`，检查启动目录和实际加载的依赖是否来自旧构建：先停止 IDE 调试或旧 Host，确认重新生成成功，再启动新产物。升级依赖后若编译失败，不要继续启动上次成功的程序。关闭 schema validation 不能将数据库存储改为内存存储。

## 运维备份与恢复

备份和恢复由运维在应用外使用匹配数据库版本的 PostgreSQL 客户端执行。应用没有备份、恢复包或删除记录重放入口；应用镜像不包含 pg_dump、pg_restore 或 age。

备份可在运维终端执行以下命令。连接参数可通过 PGHOST、PGPORT、PGUSER、PGDATABASE 与受限权限的 .pgpass 提供，避免将密码放入命令历史。

~~~bash
pg_dump --format=custom --file=wonder.dump --dbname=wonder
~~~

数据库转储包含业务数据、Outbox、在线隐私操作和撤销记录。Quartz 内存触发器不需要备份。另行安全保存整个 Storage:KeysPath 目录；API Key、QQ Token 与登录 Cookie 使用 Data Protection，数据库转储不能替代原密钥环。需要保留图片时，另行备份 Storage:MediaPath。备份保留、加密、异机保存和定期恢复演练由运维安排。

恢复步骤：

1. 确认备份使用当前 `InitialCreate` 基线，停止 Host（包括其中的 Worker、Quartz 和 OneBot 连接），在隔离环境先演练恢复。
2. 准备空的 PostgreSQL + pgvector 数据库，由运维执行：

   ~~~bash
   pg_restore --exit-on-error --no-owner --dbname=wonder_restored wonder.dump
   ~~~

3. 恢复配套密钥目录及需要的媒体，确认运行账号权限，将连接串指向恢复库。
4. 确认恢复库已记录当前基线，再启动 Host；需要检查迁移时可显式运行 `--migrate`。不要对完整恢复库重新执行建表脚本，旧版本备份也不能作为当前基线的升级入口。
5. 检查 /health/ready、登录、本人记忆、隐私清理进度及发送不确定状态。

**恢复后的数据状态以备份时点为准。备份之后的更正、Forget、Erase 或 Notes 删除不会自动重放，旧数据可能重新出现。** 应根据运维恢复范围重新核对并处理；应用不提供后续删除记录的恢复保障。

## 保留与清理

交互、主动整理与 Notes 的 Host 能力开关默认开启；是否向模型提供工具，还取决于 Chat 路由的工具开关、私聊绑定及本人授权，见 [交互工具与 Notes](agent-tools.md#启用工具)。

数据库撤销记录继续使用 DeletionResourceKind 区分 Memory 和 Note，保留业务栅栏、防回写及审计所需的 ResourceId／ResourceVersion。Notes 与整理原因按本人确认保留到主动删除／清除，不自动过期。

原始消息默认保留 90 天，创建时记录 ExpiresAt，修改 Bot 保留期只影响后续消息；图片缓存 7 天。清理每 5 分钟分批执行，符合条件的完成发送正文按 30 天清理。Memory / Relationship 不随原始消息自动过期。

记忆管理的操作范围见 [Memory 生命周期](memory-lifecycle.md)，自动维护规则见 [自主维护记忆](memory-maintenance.md)。
