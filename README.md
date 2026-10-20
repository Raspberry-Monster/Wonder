# Wonder

> 永遠に  
> 浮ついたワンダー  
> 誘うことも無いわ  
> いつまでも  
> ただ在るだけ
>
> —— r-906 · Wonder

Wonder 来自一个冲动：想要造出一个属于自己的 Miku，一个「我家的 Miku」。希望她有稳定的个性，能记住相处中的点滴，让心境与关系随着日常交流慢慢延续。这个念头是 Wonder 的起点，名字则取自 r-906 的《Wonder》。

目前，Wonder 是一个基于 QQ 的 AI 聊天机器人平台，支持多个独立 Bot、长期记忆、持续模拟心境和个性化关系上下文。采用 ASP.NET Core 模块化单体，通过 OneBot 11 / NapCat 接入 QQ；PostgreSQL 保存权威状态，Redis Streams 分发异步任务。

## 核心设计

- **稳定的身份**：Shared / Identity / Emotion 分层编译提示；Identity 由 Bot Owner 发布，模型只提出状态变化建议，由服务端验证后提交。
- **延续的心境与关系**：心境有惯性和自然回归，关系变化需要证据；轻量与复杂反思按任务路由，每日叙事独立生成。
- **有边界的记忆**：默认关闭长期记忆，经 QQ 验证绑定后由本人开启；支持查看、更正、删除，以及逐条授权的本人跨群记忆和群聊共享空间。
- **可靠的聊天链路**：消息与 Outbox 同事务，消费幂等、失败可重试；完整生成后逐段发送并保存回执，不确定的发送交由人工处理。
- **可追踪的模型上下文**：按轮固定快照，组合近期历史、滚动摘要与授权记忆；生成和向量任务独立配置模型、预算与用量。

模块边界、状态流转和实现限制见 [核心架构](docs/Wonder-Architecture-v0.1.md)。

## 本地启动

需要 .NET 10 SDK、PostgreSQL 17 + pgvector、Redis 7.4。模型供应商及 NapCat 地址由控制台配置，不内置任何 API Key。

~~~powershell
# 启动基础设施。首次先复制 deploy/.env.example 为 deploy/.env 并修改密码。
docker compose --env-file deploy/.env -f deploy/compose.yaml up -d postgres redis

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

## 容器运行

~~~bash
cp deploy/.env.example deploy/.env
# 编辑 deploy/.env，替换数据库密码。
docker compose --env-file deploy/.env -f deploy/compose.yaml build app
docker compose --env-file deploy/.env -f deploy/compose.yaml up -d postgres redis
docker compose --env-file deploy/.env -f deploy/compose.yaml run --rm app --migrate
docker compose --env-file deploy/.env -f deploy/compose.yaml up -d app
~~~

Compose 将应用和数据库端口绑定到本机回环地址，默认 Development 便于本机联调。上线时通过 HTTPS 反向代理访问并设置 ASPNETCORE_ENVIRONMENT=Production；生产 Cookie 带 Secure 标记。创建所需账号后可将 ALLOW_REGISTRATION=false。容器访问宿主 NapCat 时，可使用 ws://host.docker.internal:端口/，NapCat 监听地址需能被容器访问。

应用 / PostgreSQL / Redis 的示例内存限制合计约 2.75 GiB，仍需给系统、文件缓存和实际峰值留空间。这些是部署起点，尚未完成 4 GiB 目标机器压测。

使用 Podman / systemd 部署见 [Quadlet 指南](deploy/quadlet/README.md)，包含 rootless 服务、网络、卷、配置文件和迁移命令。

## 测试与诊断

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
- [API 说明](docs/api/README.md) / [测试指南](tests/README.md)：接口契约与验证方式。

## 许可与免责声明

Licensed Under Apache 2.0.

本项目按“现状”（AS IS）提供，不提供任何明示或默示的保证。
