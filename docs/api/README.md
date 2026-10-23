# API v1

已导出的 [OpenAPI 文件](openapi.v1.json) 可直接查看；运行时 OpenAPI 文档由 Host 在 /openapi/v1.json 提供，需要先登录。请求和响应使用 camelCase；枚举使用名称字符串。

- POST /api/v1/auth/register、/auth/login：email / password；登录写入 HttpOnly、SameSite=Strict Cookie。默认支持 HTTP，Secure 标记随请求协议设置；Authentication:RequireHttps=true 时始终设置 Secure。
- POST /api/v1/auth/logout；GET /api/v1/me：当前登录身份。
- GET / POST /api/v1/bots；GET / PATCH /api/v1/bots/{id}：当前 Owner 的实例。
- GET / PUT /api/v1/bots/{id}/identity：读取历史 / 发布新身份。
- GET / PUT /api/v1/bots/{id}/binding：QQ 账号、正向 WebSocket 与令牌。
- GET / PUT /api/v1/bots/{id}/model-routes：按任务更新路由；PUT 为数组，可只更新其中一个任务。
- GET /api/v1/bots/{id}/model-routes/tokenizers：列出可用词表；路由响应提供 tokenizerId 和 resolvedTokenizer。
- GET /api/v1/bots/{id}/groups；GET / PUT /groups/{groupId}/policy：群触发策略，contextSpaceId 指定共享空间；PUT 省略或 null 表示退出空间。
- GET / POST /api/v1/bots/{id}/context-spaces；PUT / DELETE /context-spaces/{spaceId}：Owner 管理本 Bot 的共享空间，创建和改名接收 name。
- GET /api/v1/bots/{id}/emotion、/usage、/jobs、/deliveries：脱敏诊断。
- POST /api/v1/bots/{id}/jobs/{jobId}/retry：重试持久失败任务。
- POST /api/v1/bots/{id}/deliveries/{partId}/resolve：action 为 confirm-sent 或 retry。后者允许再次发送，可能产生外部重复。
- POST /api/v1/me/qq-binding-challenge：生成 QQ 私聊绑定口令。
- GET / PUT /api/v1/me/memory-settings：allowMemory、allowCrossGroupMemory、preferredName、allowMemoryMerge、allowMemoryUpdate、allowMemoryArchive、retainMemoryHistory；可选字段省略时保留原值，preferredName 传空字符串可清空。
- GET /api/v1/me/memories：本人本地记忆及 sharing 状态，includeArchived=true 包含保留的归档历史；返回 archivedAt、supersedesMemoryId、lastConfirmedAt、revision。GET / PATCH / DELETE /api/v1/me/memories/{id}：本人记忆，PATCH 接收 text；更正和删除处理完整版本链并撤回关联共享。
- GET /api/v1/me/memory-maintenance：本人候选和维护记录，支持 skip 分页。详见 [自主维护记忆](../memory-maintenance.md)。
- GET /api/v1/me/memories/{id}/sharing-options：查询来源群当前共享空间。
- POST /api/v1/me/memories/{id}/sharing：接收 scope（UserShared / SpaceShared）；SpaceShared 还须传当前 contextSpaceId，成功返回共享副本 id。
- DELETE /api/v1/me/memories/{id}/sharing/{sharedId}：本人撤回指定共享，保留原始记忆。
- POST /api/v1/system/backups、/system/deletion-ledger：仅配置在 SystemAdminIds 中的系统管理员。

API 按功能整理在 src/Wonder.Host/Controllers，输入类型位于 Contracts，使用 ApiController 模型绑定和 FluentValidation 异步过滤器。字段错误返回 ValidationProblemDetails，资源归属和业务规则在工作流中校验。

所有 POST / PUT / PATCH / DELETE 都要求 X-Wonder: 1。未授权返回 401，非本人 Bot 资源返回 404；Bot Owner 不能通过此身份读取其他人的私人记忆。列表支持固定上限；jobs 和 memories 接收 skip 分页。

API 返回的密钥字段仅有 hasApiKey / hasAccessToken，绝不返回保护后的密文。更新密钥时留空表示保留旧值。消息保留期更改只作用于新消息。

模型路由的 supportsTools 仅支持 Chat 任务开启；省略时保留原值，false 显式关闭。对应只读时间与当前对话历史查询工具，详见 [对话延续与时间](../conversation-continuity.md)。

模型路由支持可选 thinkingEnabled（true / false / null）与 reasoningEffort（最多 32 字符的供应商强度字符串，例如 low / high / max，或 null）。非空强度要求 thinkingEnabled=true；Embedding 不接受思考参数。null 或省略表示供应商默认，不发送相应参数；更新时会清空该任务旧的思考设置。tokenizerId 默认 auto，也可指定 estimate 或可用词表 ID。通用配置与请求数组见 [模型路由指南](../model-routes.md)。

使用步骤与共享范围见 [跨群记忆与用户称呼](../shared-context.md)。

## 持久事件契约

运行时接受的事件版本为 v2，定义见 [outbox.v2.schema.json](outbox.v2.schema.json)。Schema 从 Core 的强类型事件注册表和 Outbox 类型导出，覆盖事件类型、载荷和任务进度元信息；业务有效性与作用域由服务端验证。

在仓库根目录生成事件 Schema：

```powershell
dotnet run --project src/Wonder.Host -- --export-event-schema (Join-Path $PWD 'docs/api/outbox.v2.schema.json')
```

## 更新 HTTP 契约

运行测试时设置 `WONDER_EXPORT_OPENAPI` 为导出路径，再运行 API 集成测试以导出 OpenAPI；同步前端类型使用：

```powershell
$env:WONDER_EXPORT_OPENAPI = Join-Path $PWD 'docs/api/openapi.v1.json'
dotnet test --project tests/Wonder.IntegrationTests/Wonder.IntegrationTests.csproj
npm --prefix src/Wonder.Web run generate:api
```

API v1 是 HTTP 路径版本，事件 v2 是内部任务契约版本，两者独立。
