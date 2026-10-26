# API v1

已导出的 [OpenAPI 文件](openapi.v1.json) 可直接查看；运行时 OpenAPI 文档由 Host 在 /openapi/v1.json 提供，需要先登录。请求和响应使用 camelCase；枚举使用稳定的名称字符串；数字或未定义枚举值返回 400。

## 认证与身份

- POST /api/v1/auth/register、/auth/login：email / password；登录写入 HttpOnly、SameSite=Strict Cookie。默认支持 HTTP，Secure 标记随请求协议设置；Authentication:RequireHttps=true 时始终设置 Secure。
- POST /api/v1/auth/logout；GET /api/v1/me：当前登录身份。
## Bot 管理

- GET / POST /api/v1/bots；GET / PATCH /api/v1/bots/{id}：当前 Owner 的实例。
- GET / PUT /api/v1/bots/{id}/identity：读取历史 / 发布新身份。
- GET / PUT /api/v1/bots/{id}/binding：QQ 账号、正向 WebSocket 与令牌。
- GET / PUT /api/v1/bots/{id}/model-routes：只接受 Chat、LightReflection、StrongReflection、RollingSummary、Embedding 五类任务；历史 DailyNarrative 用量仍保留。PUT 为数组，可只更新其中一个任务。
- GET /api/v1/bots/{id}/model-routes/tokenizers：列出可用词表；路由响应提供 tokenizerId 和 resolvedTokenizer。
- GET /api/v1/bots/{id}/groups；GET / PUT /groups/{groupId}/policy：基础群触发和消息保存策略。
- GET /api/v1/bots/{id}/emotion、/usage、/jobs、/deliveries：脱敏诊断。
- POST /api/v1/bots/{id}/jobs/{jobId}/retry：重试持久失败任务。
- POST /api/v1/bots/{id}/deliveries/{partId}/resolve：action 为 confirm-sent 或 retry。后者允许再次发送，可能产生外部重复。
## 个人记忆

- POST /api/v1/me/qq-binding-challenge：生成 QQ 私聊绑定口令。
- GET / PUT /api/v1/me/memory-settings：allowMemory、preferredName、allowMemoryMerge、allowMemoryUpdate、allowMemoryArchive、retainMemoryHistory；可选字段省略时保留原值，preferredName 传空字符串可清空。
- GET /api/v1/me/memories：本人私人记忆，includeArchived=true 包含授权保留的归档历史；返回 archivedAt、isOutdated、supersedesMemoryId、lastConfirmedAt、revision。
- Edit/Archive：PATCH /api/v1/me/memories/{id} 接收 `{text, expectedRevision}`；POST `.../{id}/archive` 接收 `{expectedRevision}`；两者要求 `Idempotency-Key`，成功返回 MemoryOperationReceipt，缺版本/键 400，版本或同键异参冲突 409，已提交同键同参返回原回执。
- Forget：DELETE `.../memories/{id}?expectedRevision=N`，要求 `Idempotency-Key`。在线目标清理核验完成才返回 200；否则 202 + 持久 operationId + Location。GET `/me/memory-operations` / `/{id}` 仅返回本人操作；POST `/{id}/retry` 只续跑冻结范围的未完成项。阶段、隔离、清理核验和逐项状态分开。
- Erase：POST `.../memories/{id}/erase-preview` 接收 `{expectedRevision, messageIds?}`，返回十分钟期限的 previewId、scopeHash、完整本人私聊消息和派生清单；POST `.../{id}/erase` 接收 `{previewId, expectedRevision, scopeHash}` 并要求幂等键。只擦完整选定消息，不接收片段偏移。版本/证据/范围变化返回 409，跨身份预览/操作返回 404。Forget 回执保留 targetMemoryId/targetRevision，供本人随后预览仍保留的历史；不返回已删 Memory 正文。
- 已绑定用户可直接使用四项管理操作；“允许记忆”控制后续自动学习。GET `/me/memory-capabilities` 返回 edit、archive、forget、erase 四项能力字段。GET `.../{id}/index-state` 显示索引状态，POST `.../{id}/retry-index` 只重试索引，不改事实。设计见 [生命周期架构](../memory-lifecycle.md)。
- GET /api/v1/me/memory-maintenance：本人候选和维护记录，支持 skip 分页。详见 [自主维护记忆](../memory-maintenance.md)。

## Notes 与整理记录

- GET / PUT `/api/v1/me/notes/settings`：本人独立 Notes 授权，字段为 allowNotes。
- GET / POST `/api/v1/me/notes`：列出或创建本人 User Notes；GET `/api/v1/me/notes/bots` 列出可用私聊 Bot。
- PUT / DELETE `/api/v1/me/notes/{noteId}`：按 expectedVersion 修订或移除本人 User Notes；GET `/api/v1/me/notes/{noteId}/history` 读取修订历史。
- DELETE `/api/v1/me/notes/personal-data`：本人彻底删除全部关联 Notes 及已知派生正文。
- GET `/api/v1/me/reflection-runs`、`/api/v1/me/reflection-runs/{runId}`：本人整理记录和下游状态；DELETE `/api/v1/me/reflection-runs/{runId}/reason` 只清除整理原因正文。

Agent Notes 不通过本人笔记列表直接展示，Bot Owner 不取得额外读权。工具调用与授权细节见 [交互工具与 Notes](../agent-tools.md)。

## 调用约定

API 按功能整理在 src/Wonder.Host/Controllers，输入类型位于 Contracts，使用 ApiController 模型绑定和 FluentValidation 异步过滤器。字段错误返回 ValidationProblemDetails，资源归属和业务规则在工作流中校验。

所有 POST / PUT / PATCH / DELETE 都要求 X-Wonder: 1。未授权返回 401，非本人 Bot 资源返回 404；Bot Owner 不能通过此身份读取其他人的私人记忆。列表支持固定上限；jobs 和 memories 接收 skip 分页。

API 返回的密钥字段仅有 hasApiKey / hasAccessToken，绝不返回保护后的密文。更新密钥时留空表示保留旧值。消息保留期更改只作用于新消息。

模型路由的 supportsTools 仅支持 Chat 任务开启；省略时保留原值，false 显式关闭。启用时提供时间与私聊历史查询；写工具还需独立服务端开关及本人授权，见 [交互工具与 Notes](../agent-tools.md)。

模型路由支持可选 thinkingEnabled（true / false / null）与 reasoningEffort（最多 32 字符的供应商强度字符串，例如 low / high / max，或 null）。非空强度要求 thinkingEnabled=true；Embedding 不接受思考参数。null 或省略表示供应商默认，不发送相应参数；更新时会清空该任务旧的思考设置。tokenizerId 默认 auto，也可指定 estimate 或可用词表 ID。通用配置与请求数组见 [模型路由指南](../model-routes.md)。

发言者、私聊与群聊的读取范围见 [对话范围](../conversation-continuity.md#发言者与私聊范围)。

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
