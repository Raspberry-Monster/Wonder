# 测试指南

PG 测试创建随机库之前要求 `WONDER_TEST_POSTGRES_SYSTEM_ID` 与经独立确认的专用测试实例匹配，管理库必须是 `postgres`。先核实专用服务、数据目录与实例标识，不从应用连接推断隔离。CI 从本作业的 PostgreSQL 服务容器内部读取标识。缺标识跳过 PG 用例，标识不匹配拒绝创建库；禁止使用原始应用服务。关键断言见 [Memory 验收约束](../docs/memory-lifecycle.md#验收约束)。

三个项目均使用 TUnit 与原生异步断言，global.json 选择 Microsoft.Testing.Platform。

## 执行

在项目根目录运行：

~~~bash
dotnet test --solution Wonder.slnx --configuration Release
dotnet test --project tests/Wonder.IntegrationTests/Wonder.IntegrationTests.csproj
~~~

参数化测试使用 Test + Arguments，异步断言必须 await。TUnit 默认并行，普通 HTTP 测试独立创建 TestApp 和 SQLite 数据库；记忆维护及其分批预算测试使用独立的临时 PostgreSQL 数据库。报告输出到 TestResults。

## 覆盖范围

| 项目 | 核心覆盖 |
| --- | --- |
| `Wonder.UnitTests` | 领域规则、提示编译、情绪限幅与记忆作用域 |
| `Wonder.ContractTests` | 兼容模型协议、文本 / 图片、JSON 与思考参数、最终回答及用量解析 |
| `Wonder.IntegrationTests` | HTTP 认证与资源授权、聊天工作流、私人记忆与旧数据归档、持久任务和真实基础设施 |

集成测试重点验证：

- 登录与 Owner 隔离、本人记忆权限、输入校验和密钥脱敏。
- 入站去重、排队消息上下文顺序、完整生成与不确定发送处理。
- 发言者归属、用户称呼、历史与引用及滚动摘要。
- 共享／空间接口已移除；私人 Bot/作者隔离；旧数据完整只读归档及派生历史／摘要／Notes 失效、待发送回复取消。
- 删除防回写、损坏来源缓存的保守失效。
- 记忆候选隔离、向量召回与作用域、自动合并 / 补充 / 更新 / 归档、用户维护边界、版本链删除、并发重判与 Profile 回填。
- 模型路由批量与单任务更新、独立 Embedding 路由、空回答与截断反思的拒绝。
- 查询的稳定排序，以及维护清理对待处理发送与反思的保留。

## 真实 PostgreSQL / pgvector

设置 WONDER_TEST_POSTGRES 后运行集成测试；未设置时跳过依赖 PostgreSQL 的基础设施、记忆维护及相关分批预算用例。测试创建 / 删除随机临时数据库，PostgreSQL 账号需要 CREATEDB 权限，实例须支持 pgvector。使用专用测试服务。记忆维护直接验证生产使用的向量查询，不在业务代码中提供 SQLite 替代算法。

这些用例覆盖完整迁移和历史数据升级、私人授权下的向量查询与距离排序、自主维护更新与版本链删除、维护分批预算，以及并发领取、任务顺序、租约续期与恢复、旧租约写入拦截、重试退避和死信。CI 使用 pgvector PostgreSQL 服务并配置此环境变量；本地运行可加 `--maximum-parallel-tests 6` 限制独立数据库用例并发，避免管理连接池耗尽；不降低用例内部的竞争并发。本地示例：

```powershell
$env:WONDER_TEST_POSTGRES = 'Host=localhost;Database=postgres;Username=wonder;Password=测试服务密码'
$env:WONDER_TEST_POSTGRES_SYSTEM_ID = '已独立核实的专用测试实例标识'
dotnet test --solution Wonder.slnx --configuration Release
```

`TestPostgres` 为每个记忆维护用例创建独立数据库，并在结束后删除；`LiveInfrastructureTests` 自行管理临时库。普通测试的 SQLite 不用于替代 pgvector SQL、迁移或 PostgreSQL 并发语义。模型响应和 QQ 发送由测试适配器模拟；当前未引入 Testcontainers 或 DbSet Mock 库。

## 契约与验收边界

运行测试时可通过 WONDER_EXPORT_OPENAPI 指定 OpenAPI 导出路径，用于同步 [API 契约](../docs/api/openapi.v1.json)。

前端脚本与 CI 验证入口为：

```powershell
npm --prefix src/Wonder.Web ci
npm --prefix src/Wonder.Web run build
npm --prefix src/Wonder.Web test
dotnet build Wonder.slnx
npm --prefix src/Wonder.Web run test:e2e
npm --prefix src/Wonder.Web run test:dev
```

浏览器测试需要 Playwright 浏览器，首次运行在 Wonder.Web 目录执行 `npx playwright install`；服务器和运行参数见该目录的 Playwright 配置。真实 SnowLuma / OneBot 回执与断线行为、模型供应商的 JSON / 图片 / 用量兼容性、容器部署、备份恢复与目标机器负载仍需分别验收。

`InteractionTests` 和 `AgentAutonomyTests` 覆盖 Force 并发、结构化写操作重放、NoReply 恢复、Notes 版本隔离和在线隐私清理、规范 Full 竞争、配额和本人权限。依赖 PG 的用例仍使用随机临时数据库，禁止将应用库作为迁移测试目标。Windows 若遇到 .NET 10 `dotnet test` 参数解析异常，可在构建后直接运行 `dotnet tests/Wonder.IntegrationTests/bin/Debug/net10.0/Wonder.IntegrationTests.dll --report-trx`；该进程同样读取 `WONDER_TEST_POSTGRES`。

浏览器测试通过 `tests/server.setup.ts` 启停专用测试子进程，拒绝占用已有测试端口，不接触运行中的应用服务。dev 与构建产物测试分别保存结果，避免同时清理同一输出目录。交互与 Notes 的行为边界见 [功能说明](../docs/agent-tools.md)，外部备份恢复见 [运行指南](../docs/operations.md#运维备份与恢复)。

生产浏览器测试使用 tests/Wonder.BrowserHost，只提供静态资源、CSP、SPA 回退及健康探针。HTTP 业务由 TestApp 验证，正常启动无恢复替身。PrivacyTests、PrivacyBoundaryTests、MemoryLifecycleTests 与 SimplificationUpgradeTests 使用隔离 PG 验证权限、锁、迁移、向量、物理清理、重试及历史用量保留。外部 PostgreSQL 备份恢复、密钥目录和真实 QQ 仍需运维演练。

参考：[TUnit 安装说明](https://tunit.dev/docs/getting-started/installation/)、[运行测试](https://tunit.dev/docs/getting-started/running-your-tests/)。
