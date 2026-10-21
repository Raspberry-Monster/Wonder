# Wonder

> 永遠に  
> 浮ついたワンダー  
> 誘うことも無いわ  
> いつまでも  
> ただ在るだけ
>
> —— r-906 · Wonder

Wonder 来自一个冲动：想要造出一个属于自己的 Miku，一个「我家的 Miku」。希望她有稳定的个性，能记住相处中的点滴，让心境与关系随着日常交流慢慢延续。这个念头是 Wonder 的起点，名字则取自 r-906 的《Wonder》。

目前，Wonder 是一个基于 QQ 的 AI 聊天机器人平台，支持多个独立 Bot、长期记忆、持续模拟心境和个性化关系上下文。采用 ASP.NET Core 模块化单体，通过 OneBot 11 / SnowLuma 接入 QQ；PostgreSQL 保存权威状态，Redis Streams 分发异步任务。

## 核心设计

- **稳定的身份**：Shared / Identity / Emotion 分层编译提示；Identity 由 Bot Owner 发布，模型只提出状态变化建议，由服务端验证后提交。
- **延续的心境与关系**：心境有惯性和自然回归，关系变化需要证据；轻量与复杂反思按任务路由，每日叙事独立生成。
- **有边界的记忆**：默认关闭长期记忆，经 QQ 验证绑定后由本人开启；支持查看、更正、删除，以及逐条授权的本人跨群记忆和群聊共享空间。
- **可靠的聊天链路**：消息与 Outbox 同事务，消费幂等、失败可重试；完整生成后逐段发送并保存回执，不确定的发送交由人工处理。
- **可追踪的模型上下文**：按轮固定快照，同一对话跨 Session 衔接近期历史与滚动摘要，每轮注入当前时间；可为聊天开启只读时间与历史查询工具。生成和向量任务独立配置模型、预算与用量。

模块边界、状态流转和实现限制见 [核心架构](docs/Wonder-Architecture-v0.1.md)。

## 本地启动

需要 .NET 10 SDK、PostgreSQL 18 + pgvector、Redis 8（容器使用 redis:8-alpine）。模型供应商及 OneBot 地址由控制台配置，不内置任何 API Key。

~~~powershell
# 先按下方容器运行说明准备目录、密码，并启动 PostgreSQL / Redis / SnowLuma。

$env:ConnectionStrings__Postgres = 'Host=localhost;Database=wonder;Username=wonder;Password=你的密码;Maximum Pool Size=20'
$env:ConnectionStrings__Redis = 'localhost:6379'
dotnet restore Wonder.slnx
dotnet run --project src/Wonder.Host -- --migrate
dotnet run --project src/Wonder.Host
~~~

访问 http://localhost:5080，注册账号（密码至少 12 位），创建 Bot，然后配置 QQ 连接和模型路由。默认群聊不触发，先添加群策略。默认不提取长期记忆；普通用户先在“我的记忆”中生成关联口令，通过 QQ 私聊 Bot 发送 /bind 口令，刷新页面后自行开启记忆。

模型地址填写供应商的 API 基础地址，例如 https://provider.example/v1/。请配置 Chat、LightReflection、StrongReflection、DailyNarrative、RollingSummary、Embedding 六类任务；可以全部复用同一供应商，但 Embedding 必须使用相应的向量模型。关闭图片能力时，模型会收到图片不可见的说明。

模型路由页支持“批量应用模型”：勾选生成任务，统一设置任意兼容模型的地址、名称、密钥、能力和参数，再单独配置 Embedding。操作和 API 示例见 [模型路由指南](docs/model-routes.md)；[DeepSeek Flash](docs/deepseek.md) 是其中一个配置示例。

不同发言者的称呼、跨群共享空间和逐条记忆共享见 [跨群记忆与用户称呼](docs/shared-context.md)。Bot 的 Identity 只描述助手自身；每个人在“我的记忆”设置自己的称呼。

基础设施等应用配置也可写在 src/Wonder.Host/appsettings.Local.json（已忽略），或使用环境变量。图片目录、Data Protection 密钥、备份目录都不在公开静态目录内。密钥目录应持久化并仅允许运行账号读取。

只检查界面而暂不启动后台任务：

~~~powershell
$env:Workers__Enabled = 'false'
dotnet run --project src/Wonder.Host
~~~

## 构建容器镜像

以下命令都在项目根目录（Dockerfile、Wonder.slnx 所在目录）执行。Dockerfile 会完成 .NET Restore、Release Publish 和运行镜像打包，宿主机只需安装对应容器工具。

### Podman / Quadlet

~~~bash
podman build -t localhost/wonder:0.1 -f Dockerfile .
~~~

应用和手动迁移的 .container 均使用 `localhost/wonder:0.1`。修改标签时，同步修改 `wonder-app.container` 和 `wonder-migrate.container` 的 Image，并执行 `systemctl --user daemon-reload`。

### Docker Compose

~~~bash
docker compose -f deploy/compose.yaml build app
~~~

Compose 从仓库根目录的 Dockerfile 构建应用镜像；PostgreSQL、Redis 和 SnowLuma 使用部署配置中指定的现成镜像。

### 更新已有部署

Podman / Quadlet：

~~~bash
set -e
systemctl --user stop wonder-app.service
podman build -t localhost/wonder:0.1 -f Dockerfile .
systemctl --user restart wonder-migrate.service
systemctl --user start wonder-app.service
~~~

Docker Compose：

~~~bash
set -e
docker compose -f deploy/compose.yaml stop app
docker compose -f deploy/compose.yaml build app
docker compose -f deploy/compose.yaml run --rm --no-deps app --migrate
docker compose -f deploy/compose.yaml up -d --no-deps app
~~~

更新时数据库和 Redis 保持运行，迁移成功后再启动新版应用。首次部署仍需按下节或 [Quadlet 指南](deploy/quadlet/README.md) 准备目录、网络、数据库密码与数据目录权限。

## 容器运行

Linux 上使用 Docker Compose；所有环境变量直接在 deploy/compose.yaml 中设置，不需要独立 env 文件。先将 postgres 的 POSTGRES_PASSWORD 与 app 的 ConnectionStrings__Postgres 中的密码改成相同的随机值，再运行：

~~~bash
mkdir -p ~/quadlet.config.d/Wonder/{PostgreSQL,Redis,Host,DeletionLedger}
mkdir -p ~/quadlet.config.d/Wonder/SnowLuma/{Data,Config,Share}
docker compose -f deploy/compose.yaml build app
docker compose -f deploy/compose.yaml up -d postgres redis snowluma
# 等 PostgreSQL 报告 accepting connections、Redis 返回 PONG 后继续；未就绪时稍后重试。
docker compose -f deploy/compose.yaml exec postgres pg_isready -U wonder -d wonder
docker compose -f deploy/compose.yaml exec redis redis-cli ping
# 首次初始化应用挂载目录的所有者；之后迁移和服务均使用镜像内 app 用户。
docker compose -f deploy/compose.yaml run --rm --no-deps --user root --entrypoint sh app -c 'chown -R app:app /app/data'
docker compose -f deploy/compose.yaml run --rm --no-deps app --migrate
docker compose -f deploy/compose.yaml up -d app
~~~

Compose 内置 motricseven7/snowluma:v2.0.3。运行 `docker compose -f deploy/compose.yaml logs snowluma` 查看首次生成的远程桌面密码和 WebUI 登录信息。打开 `http://192.168.1.102:6081/` 扫码登录 QQ，再在 `http://192.168.1.102:5099/` 配置账号的 OneBot 11 正向 WebSocket（0.0.0.0:3001、数组消息段），复制该服务的 Access Token。在 Wonder 的 QQ 连接中填写 QQ 账号、ws://snowluma:3001/ 和相同 Token；宿主运行 .NET 时用 ws://127.0.0.1:3001/。三种密码 / Token 分别管理。

所有数据使用宿主目录映射，统一放在 ~/quadlet.config.d/Wonder/ 下：PostgreSQL、Redis、Host、DeletionLedger，以及 SnowLuma/Data、SnowLuma/Config、SnowLuma/Share。后者分别保存协议端数据、QQ 配置及客户端数据。PostgreSQL 18 使用 pgvector/pgvector:pg18，挂载到容器的 /var/lib/postgresql，实际数据库文件位于宿主 PostgreSQL/18/docker。Compose 与 Quadlet 共用目录布局，不能同时访问同一份数据。

SnowLuma 管理端口 5099 和 6081 绑定宿主局域网地址 `192.168.1.102`，防火墙按需允许管理电脑访问这两个 TCP 端口；其他已发布端口绑定本机回环地址。应用默认使用 Production，同时支持 HTTP 访问和登录，无需配置 Development；可直接访问 http://127.0.0.1:5080 或通过 SSH 转发访问。登录 Cookie 随请求协议设置 Secure；需要仅允许 Cookie 经 HTTPS 传输时，按需添加 Authentication__RequireHttps=true。创建所需账号后可按需添加 Authentication__AllowRegistration=false；备份收件人与管理员 UUID 可通过 Backups__AgeRecipient、SystemAdminIds__0 配置。

各容器采用默认资源管理，数据库使用默认参数，Redis 开启 AOF 持久化。实际资源需求仍需在目标机器上按工作负载验证。

使用 Podman / systemd 部署见 [Quadlet 指南](deploy/quadlet/README.md)。环境变量直接写在各自的 .container 中，宿主目录映射使用 %h/quadlet.config.d/Wonder/模块名；迁移由手动 wonder-migrate.service 执行。

PostgreSQL 在两种部署方式下都映射到宿主 `127.0.0.1:5432`；另一台电脑可通过 SSH 隧道连接，客户端填写本机转发端口。具体命令、连接参数和局域网直连配置见 [外部管理 PostgreSQL](deploy/quadlet/README.md#外部管理-postgresql)。

## 测试与诊断

记忆可由 Bot 在用户设定的边界内自动合并、补充、更新和归档，设置及部署说明见 [自主维护记忆](docs/memory-maintenance.md)。

~~~bash
dotnet test --solution Wonder.slnx
~~~

测试框架为 TUnit，global.json 已启用 Microsoft.Testing.Platform；单独运行某项目可使用 dotnet test --project tests/Wonder.UnitTests/Wonder.UnitTests.csproj。

日常测试使用 SQLite 测试适配器和模拟供应商，覆盖真实 HTTP 登录、Owner 资源隔离、个人记忆隔离、入站去重、完整聊天流程、排队消息上下文、删除防回写、DeliveryUnknown，以及协议和领域规则。

真实 PostgreSQL / Redis 测试需要显式设置 WONDER_TEST_POSTGRES 和 WONDER_TEST_REDIS；未设置时只跳过此项。该测试会创建 / 删除一个随机命名的临时数据库，连接账号需要 CREATEDB 权限。CI 已配置相应服务。

健康端点：/health/live（进程）、/health/ready（数据库与 Redis）。登录后可访问 /openapi/v1.json。所有有副作用的 API 请求都需要 X-Wonder: 1 请求头。控制台已自动携带。

## 文档

- [核心架构](docs/Wonder-Architecture-v0.1.md)：模块、提示、心境、记忆与可靠性设计。
- [运行指南](docs/operations.md) / [Quadlet 部署](deploy/quadlet/README.md)：部署、排错、备份与恢复。
- [模型路由](docs/model-routes.md) / [DeepSeek 示例](docs/deepseek.md)：任务配置与兼容接口。
- [跨群记忆与用户称呼](docs/shared-context.md)：授权范围、共享与撤回。
- [对话延续与时间](docs/conversation-continuity.md)：跨 Session 上下文、当前时间、历史查询工具与升级。
- [API 说明](docs/api/README.md) / [测试指南](tests/README.md)：接口契约与验证方式。

## 许可与免责声明

Licensed Under Apache 2.0.

本项目按“现状”（AS IS）提供，不提供任何明示或默示的保证。
