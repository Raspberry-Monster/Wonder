# Wonder

> 永遠に  
> 浮ついたワンダー  
> 誘うことも無いわ  
> いつまでも  
> ただ在るだけ
>
> —— r-906 · Wonder

Wonder 来自一个冲动：想要造出一个属于自己的 Miku，一个「我家的 Miku」。希望她有稳定的个性，能记住相处中的点滴，让心境与关系随着日常交流慢慢延续。这个念头是 Wonder 的起点，名字则取自 r-906 的《Wonder》。

目前，Wonder 是一个基于 QQ 的 AI 聊天机器人平台，支持多个独立 Bot、长期记忆、持续模拟心境和个性化关系上下文。采用 ASP.NET Core 模块化单体，使用 [SaltyMilky.Net](https://www.nuget.org/packages/SaltyMilky.Net/1.2.2) 客户端连接独立部署的 Milky HTTP/WebSocket 服务接入 QQ；PostgreSQL 保存业务状态，并通过 Outbox 持久任务表完成异步调度、租约、重试和死信处理。

主要面向个人自部署和少量可信用户，按单 Host 运行。可以直接从源码构建并部署，无需等待 Public Release；首次配置和上线检查见 [内部自部署验收](docs/operations.md#内部自部署验收)。

## 核心设计

- **稳定的身份**：Shared / Identity / Emotion 分层编译提示；Identity 由 Bot Owner 发布，模型只提出状态变化建议，由服务端验证后提交。
- **延续的心境与关系**：心境有惯性和自然回归，关系变化需要证据；轻量与复杂反思按任务路由，每日维护推进心境。
- **有边界的记忆**：默认关闭长期记忆，经 QQ 验证绑定后由本人开启；支持查看、更正、删除，私人事实的自动整理和独立授权 Notes。
- **可靠的聊天链路**：消息与 Outbox 同事务，消费幂等、失败可重试；完整生成后逐段发送并保存回执，不确定的发送交由人工处理。
- **可追踪的模型上下文**：按轮固定快照，同一对话跨 Session 衔接近期历史与滚动摘要，保留消息时间，并可按需调用只读时间与历史查询工具。生成和向量任务独立配置模型、预算与用量。

模块边界、状态流转和实现限制见 [核心架构](docs/architecture.md)。

数据库初始化、备份、恢复和密钥排错见 [运行指南](docs/operations.md)。

## 本地启动

需要 .NET 10 SDK、Node.js 24、PostgreSQL 18 + pgvector。模型供应商及 Bot QQ 账号由控制台配置，不内置任何 API Key。

首次应用当前 `InitialCreate` 基线需要空库。使用旧迁移链的开发库必须手动重建，不提供旧版本库的升级、导入或回填；应用不会自动删除数据库。已应用相同基线的库可重复执行 `--migrate`，已应用项会被跳过。

已绑定用户可直接管理已有记忆，无需配置启用开关或恢复证明；“允许记忆”控制后续自动学习。记忆更正、归档、精确删除和安全投影见 [Memory 生命周期架构](docs/memory-lifecycle.md)。

~~~powershell
# 先按下方容器运行说明准备目录、密码，并启动 PostgreSQL。

$env:ConnectionStrings__Postgres = 'Host=localhost;Database=wonder;Username=wonder;Password=你的密码;Maximum Pool Size=20'
npm --prefix src/Wonder.Web ci
npm --prefix src/Wonder.Web run build
dotnet restore Wonder.slnx
dotnet run --project src/Wonder.Host -- --migrate
dotnet run --project src/Wonder.Host
~~~

Windows PowerShell 若提示无法运行 `npm.ps1`，将示例中的 `npm` 换成 `npm.cmd` 即可，无需修改系统执行策略。

访问 http://localhost:5080，注册账号（密码至少 12 位），创建 Bot，然后配置 QQ 连接和模型路由。默认群聊不触发，先添加群策略。默认不提取长期记忆；普通用户先在“个人空间 → 连接你的 QQ 身份”生成绑定码，通过 QQ 私聊 Bot 发送 /bind 口令，刷新页面后在“记忆偏好”自行开启记忆。

模型地址填写供应商的 API 基础地址，例如 https://provider.example/v1/。请配置 Chat、LightReflection、StrongReflection、RollingSummary、Embedding 五类任务；可以全部复用同一供应商，但 Embedding 必须使用相应的向量模型。关闭图片能力时，模型会收到图片不可见的说明。

模型路由页支持“批量应用模型”：勾选生成任务，统一设置任意兼容模型的地址、名称、密钥、能力和参数，再单独配置 Embedding。操作和 API 示例见 [模型路由指南](docs/model-routes.md)；[DeepSeek Flash](docs/deepseek.md) 是其中一个配置示例。

不同发言者的称呼与私聊／群聊范围见 [对话范围](docs/conversation-continuity.md#发言者与私聊范围)。Bot 的 Identity 只描述助手自身；每个人在“个人空间 → 记忆偏好”设置自己的称呼。

基础设施等应用配置也可写在 src/Wonder.Host/appsettings.Local.json（已忽略），或使用环境变量。图片与 Data Protection 密钥位于私有目录；相对存储路径按进程工作目录解析。搬迁或恢复时需同时保留数据库和原密钥目录，否则已保存的模型 Key、QQ 令牌无法解密。配置说明见 [运行指南](docs/operations.md#配置与持久目录)。

仅做界面验收时使用独立测试入口（API 由测试 mock，不启动业务 Host/Worker/QQ）：

~~~powershell
dotnet build Wonder.slnx
npm --prefix src/Wonder.Web run test:e2e
~~~

## 构建容器镜像

以下命令都在项目根目录（Dockerfile、Wonder.slnx 所在目录）执行。Dockerfile 会完成前端依赖安装、类型检查和构建、.NET Restore、Release Publish 及运行镜像打包，宿主机只需安装对应容器工具。

### Podman / Quadlet

~~~bash
podman build -t localhost/wonder:0.1 -f Dockerfile .
~~~

应用和手动迁移的 .container 均使用 `localhost/wonder:0.1`。修改标签时，同步修改 `wonder-app.container` 和 `wonder-migrate.container` 的 Image，并执行 `systemctl --user daemon-reload`。

### Docker Compose

~~~bash
docker compose -f deploy/compose.yaml build app
~~~

Compose 从仓库根目录的 Dockerfile 构建应用镜像；PostgreSQL 与 Lagrange.Milky 使用部署配置中指定的现成镜像。

### 更新已有部署

以下命令用于已采用当前 `InitialCreate` 基线的部署。更新前停止 Host，并备份数据库、Data Protection 密钥及必要媒体。使用旧迁移链的开发库需先手动重建并修改连接串，不能直接套用更新命令完成升级；详见 [数据库基线与首次初始化](docs/operations.md#数据库基线与首次初始化)。

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

数据库保持运行，迁移命令成功后再启动新版应用。首次部署按下节或 [Quadlet 指南](deploy/quadlet/README.md) 准备目录、网络、数据库密码与数据目录权限，再在空库上初始化。

## 容器运行

Linux 上使用 Docker Compose；所有环境变量直接在 deploy/compose.yaml 中设置，不需要独立 env 文件。先将 postgres 的 POSTGRES_PASSWORD 与 app 的 ConnectionStrings__Postgres 中的密码改成相同的随机值。配置完成后运行：

~~~bash
mkdir -p ~/quadlet.config.d/Wonder/{PostgreSQL,Host,Milky}
cp deploy/milky.appsettings.example.json ~/quadlet.config.d/Wonder/Milky/appsettings.json
# 编辑该文件：设置 QQ 号、访问令牌及签名服务。
chmod 700 ~/quadlet.config.d/Wonder/Milky
chmod 600 ~/quadlet.config.d/Wonder/Milky/appsettings.json
docker compose -f deploy/compose.yaml build app
docker compose -f deploy/compose.yaml up -d postgres
# 等 PostgreSQL 报告 accepting connections 后继续；未就绪时稍后重试。
docker compose -f deploy/compose.yaml exec postgres pg_isready -U wonder -d wonder
# 首次初始化应用挂载目录的所有者；之后迁移和服务均使用镜像内 app 用户。
docker compose -f deploy/compose.yaml run --rm --no-deps --user root --entrypoint sh app -c 'chown -R app:app /app/data'
docker compose -f deploy/compose.yaml run --rm --no-deps app --migrate
docker compose -f deploy/compose.yaml up -d milky app
# 在 Milky 日志中查看二维码并完成登录。
docker compose -f deploy/compose.yaml logs -f milky
# 完成后按 Ctrl+C 退出日志查看，后台服务继续运行。
curl --fail http://127.0.0.1:5080/health/ready
~~~

Compose 的 `depends_on` 只控制启动顺序，当前配置没有等待数据库健康的条件；首次部署必须按上面的顺序检查 PostgreSQL 就绪并显式执行迁移。参见 [Compose 启动顺序](https://docs.docker.com/compose/how-tos/startup-order/)。

QQ 登录、设备验证、凭据保存及 Signing 均由独立的 Lagrange.Milky 处理。先在 Milky 完成登录，再在控制台的 Bot「平台连接」填写相同 QQ 号、Milky 服务基础地址和访问令牌。Compose / Quadlet 的地址为 `http://milky:3000`；本地 Host 连接其他 Milky 时使用该服务实际可达的 HTTP(S) 地址。Wonder 自动连接 `/event` WebSocket 并通过 `/api/*` 发送消息，访问令牌由 Data Protection 加密保存。每个 QQ 账号部署一个 Milky 实例；多个 Bot 使用不同实例和地址。配置示例及签名说明见 [运行指南](docs/operations.md#lagrangemilky-与签名服务)。

所有数据使用宿主目录映射，统一放在 ~/quadlet.config.d/Wonder/ 下：PostgreSQL、Host 和 Milky。Host 保存 Data Protection 密钥和媒体，数据库保存加密的 Milky 访问令牌；Milky 目录保存独立服务配置与 QQ 登录凭据。PostgreSQL 18 使用 pgvector/pgvector:pg18，挂载到容器的 /var/lib/postgresql，实际数据库文件位于宿主 PostgreSQL/18/docker。Compose 与 Quadlet 共用目录布局，不能同时访问同一份数据。

应用与数据库发布端口均绑定本机回环地址。应用默认使用 Production，同时支持 HTTP 访问和登录，无需配置 Development；可直接访问 http://127.0.0.1:5080 或通过 SSH 转发访问。登录 Cookie 随请求协议设置 Secure；需要仅允许 Cookie 经 HTTPS 传输时，按需添加 Authentication__RequireHttps=true。创建所需账号后可按需添加 Authentication__AllowRegistration=false。

仓库默认开启交互、主动整理和 Notes 的 Host 能力开关；模型路由的“支持工具调用（聊天）”仍需单独开启，长期记忆与 Notes 还需本人授权。`/help`、`/bind`、`/end` 由 Wonder 处理，只有已受理的 `/force` 会进入 Miku 的聊天流程。图片允许 DNS 解析到内网及 FakeIP，仍要求 HTTPS 443 且不跟随跳转。配置边界见 [交互工具与 Notes](docs/agent-tools.md) 和 [图片下载与网络](docs/operations.md#图片下载与网络)。

各容器采用默认资源管理，数据库使用默认参数。实际资源需求仍需在目标机器上按工作负载验证。

使用 Podman / systemd 部署见 [Quadlet 指南](deploy/quadlet/README.md)。环境变量直接写在各自的 .container 中，宿主目录映射使用 %h/quadlet.config.d/Wonder/模块名；迁移由手动 wonder-migrate.service 执行。

PostgreSQL 在两种部署方式下都映射到宿主 `127.0.0.1:5432`；另一台电脑可通过 SSH 隧道连接，客户端填写本机转发端口。具体命令、连接参数和局域网直连配置见 [外部管理 PostgreSQL](deploy/quadlet/README.md#外部管理-postgresql)。

## 测试与诊断

记忆可由 Bot 在用户设定的边界内自动合并、补充、更新和归档，设置及部署说明见 [自主维护记忆](docs/memory-maintenance.md)。

~~~bash
dotnet test --solution Wonder.slnx
~~~

测试框架为 TUnit，global.json 已启用 Microsoft.Testing.Platform；单独运行某项目可使用 dotnet test --project tests/Wonder.UnitTests/Wonder.UnitTests.csproj。

日常测试使用 SQLite 测试适配器和模拟供应商，覆盖真实 HTTP 登录、Owner 资源隔离、个人记忆隔离、入站去重、完整聊天流程、排队消息上下文、删除防回写、DeliveryUnknown，以及协议和领域规则。

真实 PostgreSQL / pgvector 测试需要专用实例及 WONDER_TEST_POSTGRES、WONDER_TEST_POSTGRES_SYSTEM_ID；未配置时跳过相应用例。测试创建／删除随机临时数据库，详细准备方式见 [测试指南](tests/README.md#真实-postgresql--pgvector)。

健康端点：/health/live（进程）、/health/ready（数据库）。登录后可访问 /openapi/v1.json。所有有副作用的 API 请求都需要 X-Wonder: 1 请求头。控制台已自动携带。

## 文档

完整导航见 [文档索引](docs/README.md)。

- [核心架构](docs/architecture.md)：模块、提示、心境、记忆与可靠性设计。
- [运行指南](docs/operations.md) / [Quadlet 部署](deploy/quadlet/README.md)：部署、排错、备份与恢复。
- [模型路由](docs/model-routes.md) / [DeepSeek 示例](docs/deepseek.md)：任务配置与兼容接口。
- [交互工具与 Notes](docs/agent-tools.md)：用户命令、静音与 Force、主动整理和独立笔记。
- [Reflection 与 Memory](docs/reflection-memory.md)：即时和会话结束反思、模型复核及候选生成。
- [自主维护记忆](docs/memory-maintenance.md) / [Memory 生命周期](docs/memory-lifecycle.md)：自动维护、本人管理与在线清理。
- [对话延续与时间](docs/conversation-continuity.md)：跨 Session 上下文、当前时间和历史查询工具。
- [统一上下文预算](docs/context-budget.md)：优先级、工具结果、后台分批恢复与配额。
- [API 说明](docs/api/README.md) / [测试指南](tests/README.md)：接口契约与验证方式。

## 许可与免责声明

Licensed Under Apache 2.0.

本项目按“现状”（AS IS）提供，不提供任何明示或默示的保证。
