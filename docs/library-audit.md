# 框架与自实现清单

原则：协议、通用基础设施优先使用官方或成熟组件；Wonder 保留授权、证据、撤销、会话顺序及角色行为等业务规则。本表记录 2026-10-22 的代码核对结果，不代表已经替换所有候选项。

## 已经使用的组件

| 部分 | 当前依赖与入口 | 说明 |
| --- | --- | --- |
| HTTP / 路由 / 模型绑定 / 问题响应 | ASP.NET Core Controllers、ProblemDetails | 没有自行实现 HTTP 服务框架 |
| OpenAPI | Microsoft.AspNetCore.OpenApi；Program.cs 的 AddOpenApi / MapOpenApi | docs/api/openapi.v1.json 来自真实运行接口的导出，不是独立手写生成器 |
| 数据库 / 迁移 | EF Core、Npgsql | 授权查询及业务事务由 Wonder 定义 |
| Redis | StackExchange.Redis | Streams 上的持久任务状态机由 Wonder 定义 |
| Cookie / 密码哈希 / 密钥保护 | ASP.NET Core Cookie、PasswordHasher、DataProtection | 注册、登录、锁定计数流程仍有自实现部分 |
| 限流 / 配置 / 日志 / HTTP 工厂 | ASP.NET Core RateLimiter、Microsoft.Extensions.* | 已使用框架能力 |
| JSON / WebSocket / 时间与时区 | System.Text.Json、ClientWebSocket、TimeProvider、TimeZoneInfo | 不是自行实现这些基础协议或算法 |
| 数据库备份与加密 | pg_dump、age | 自实现的是进程编排及删除账本，不是备份格式或加密算法 |

## 本次替换

| 原自实现 | 代码位置 | 改为 |
| --- | --- | --- |
| Chat Completion 请求 / 响应 JSON、工具调用协议、图片编码 | Wonder.AI/CompatibleClients.cs（已删除） | OpenAI .NET SDK 的 ChatClient + Microsoft.Extensions.AI.OpenAI.AsIChatClient |
| Embedding 请求 / 响应 JSON、向量转换 | 同上 | EmbeddingClient + AsIEmbeddingGenerator |
| 手动调用工具并循环请求模型 | Wonder.Infrastructure/ChatWorkflow.cs | FunctionInvokingChatClient，限制为最多 4 次模型请求、8 个工具调用 |
| 手写工具 JSON Schema | Wonder.Infrastructure/ConversationTools.cs | AIFunctionFactory 从函数签名生成 Schema |
| HTTP 短时重试循环 | 原 CompatibleHttp.PostAsync | SDK ClientRetryPolicy；长 Retry-After 通过扩展点交给现有持久任务 |

StandardClients.cs 中保留薄装饰器：通过官方 RawRepresentationFactory / SDK JsonPatch 传递 thinking、reasoning_effort、兼容接口的 max_tokens，以及 Embedding 的 float 编码；通过 SDK AssistantChatMessage 保留工具轮次需要的 reasoning_content。它们不自行编码整个模型请求、解析工具协议或解释正文中的调用标签。

模型输出使用标准 TextContent / TextReasoningContent / FunctionCallContent / FunctionResultContent。只有结构化 FunctionCallContent 进入工具执行器。正文中的 DSML（包括残缺标签）作为异常响应处理，计入用量并交给现有任务重试；不会从字符串推断或执行工具。发送前也阻止旧版本队列中的这类内容，避免内部格式进入 QQ。

## 值得后续替换或改进

| 部分 | 当前位置 / 自实现内容 | 可用组件或方向 | 取舍 |
| --- | --- | --- | --- |
| 账号流程 | AuthController：注册校验、查用户、失败计数、锁定、登录 | ASP.NET Core Identity 的 UserManager / SignInManager / Store | 优先级高；需迁移现有用户存储映射，保持账号 ID、密码和 Cookie 行为兼容 |
| OpenAPI 返回模型 | 多个 Controller 的 IActionResult + 匿名对象 | ActionResult<T>、具名响应 DTO、ProducesResponseType | 生成器已经官方化；下一步应补足类型元数据，减少仅有状态码而没有响应 Schema 的接口 |
| 普通输入校验 | Controller 中手写长度、空值、数值范围判断 | DataAnnotations + ApiController 自动校验 | 适合逐步替换；资源归属、证据和撤销等业务校验不能只靠字段属性 |
| 向量映射与距离 SQL | real[] 存储，MemoryQueries 用参数化 SQL 转 vector | pgvector-dotnet / Pgvector.EntityFrameworkCore | 需要存储迁移和授权查询回归；不能只替换计算函数而丢失作用域 / Profile 过滤 |
| 定时清理与每日任务调度 | JobWorkers、MaintenanceWorkflow 的定时扫描 | Quartz.NET 等成熟调度组件 | 调度可以替换；用户撤销及清理规则仍需保留 |
| 可靠任务投递 | Jobs、JobWorkers：Outbox / Inbox、Redis Streams、租约、重试、死信 | 评估成熟持久消息框架 | 与事务、顺序和“不确定发送”密切关联，应作为独立迁移；调度库本身不能自动替代所有语义 |
| OneBot 客户端 | OneBotConnectionManager、OneBotProtocol：消息归一化、echo 回执、重连与发送状态 | 评估覆盖 OneBot 11 与实际协议端的 SDK | 需确认数组消息段、引用、回执与断线行为；QQ 触发及授权规则保留 |
| Token 预算 | ContextBuilder、ModelRouter 的保守字符估算 | 与具体模型匹配的 tokenizer | 通用 tokenizer 不等同于供应商计费；应保持无匹配分词器时的保守回退 |
| 事件 Schema | docs/api/events.schema.json | 先将事件载荷强类型化，再用 System.Text.Json Schema 导出能力生成 | 这是内部事件契约，和 HTTP OpenAPI 是两件事 |
| 图片下载与缓存 | MediaStore：大小限制、类型识别、文件缓存、网络策略 | 复用 IHttpClientFactory / 官方 HTTP 组件 | 配额、允许来源及缓存撤销是业务规则，通用图片包不会自动代替 |
| 控制台页面 | 原生 JS 的表单、表格、API 调用 | 随页面规模评估 UI 框架 / OpenAPI 生成客户端 | 当前不是协议漏洞的来源，优先级低于客户端和账号流程 |

## 需要保留的 Wonder 逻辑

- 记忆准入、维护权限、作用域与共享来源校验。
- 撤销 Epoch、证据校验、版本链及派生上下文失效。
- Conversation / Session 边界、发言者身份、历史可见范围。
- 角色身份发布、模拟情绪和关系规则。
- 模型路由、预算上限、用量持久化和工具结果的来源追踪。

这些逻辑可以拆成更清楚的服务、策略或中间件，但不能期待通用库替项目定义业务含义。

参考：[M.E.AI](https://learn.microsoft.com/dotnet/ai/ichatclient)、[函数调用中间件源码](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs)、[ASP.NET Core OpenAPI](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0)、[ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity?view=aspnetcore-10.0)、[pgvector-dotnet](https://github.com/pgvector/pgvector-dotnet)、[Quartz.NET](https://www.quartz-scheduler.net/documentation/quartz-4.x/quick-start.html)。

本次验证：完整回归 111 项全部通过（包含随机临时 PostgreSQL / pgvector 数据库及独立 Redis Stream）；Embedding float 编码兼容性与其他协议专项 15 项通过，Release 发布通过。供应商响应由模拟 HTTP 验证，未调用真实模型 API，也未修改运行中的应用数据库或记忆内容。
