# 2026-10 重构迁移

## 升级步骤

1. 停止旧版 Host（包括后台任务），备份 PostgreSQL、Data Protection 密钥和媒体目录，避免新旧事件处理器同时运行。
2. 使用 Node.js 24 执行 `npm --prefix src/Wonder.Web ci` 和 `npm --prefix src/Wonder.Web run build`，然后构建 .NET 10 项目。容器构建已包含前端步骤。
3. 保持原有 ConnectionStrings:Postgres 和密钥目录配置，执行 `dotnet run --project src/Wonder.Host -- --migrate`。迁移创建 Quartz 表、预算预留字段和 Tokenizer 设置，并将 Outbox 事件升级到 SchemaVersion 2。
4. 启动新版 Host。检查 /health/ready、登录、Bot 配置及任务记录。已有路由默认 tokenizerId=estimate；请按实际模型选择词表。

本次没有自动迁移业务数据库；回归测试仅使用临时数据库。

## 事件与任务

运行时只接受 v2，不保留 v1 解析分支。迁移保留旧任务状态，旧每日任务按原创建时间的 UTC 日期转换为 DailyJob。新的每日任务按 Chat:TimeZone（默认 Asia/Shanghai）计算日期，每个 Bot / 类型 / 日期唯一。停机恢复只补当前日，不补发多天陈旧生成任务。

Quartz.NET 使用同一 PostgreSQL 持久化定时器：会话检查每 15 秒、清理每 5 分钟、每日零点调度及每 5 分钟对当日任务补漏。Outbox 仍负责业务任务、租约、重试与死信；任务领取使用 EF 条件更新。Quartz 自身使用官方 PostgreSQL 持久化 SQL。当前按单 Host 部署配置，不启用 Quartz 集群。调度与缓存均不依赖 Redis。

事件 JSON Schema 见 [outbox.v2.schema.json](api/outbox.v2.schema.json)。生成命令：

~~~powershell
dotnet run --project src/Wonder.Host -- --export-event-schema (Join-Path $PWD 'docs/api/outbox.v2.schema.json')
~~~

## 模型与 Tokenizer

词表保存在 `src/Wonder.Host/Tokenizer`，程序枚举子目录中的 Hugging Face `tokenizer.json`。每个目录可通过 `models.json` 数组声明模型别名，匹配规则不绑定供应商。详见 [Tokenizer 目录说明](../src/Wonder.Host/Tokenizer/README.md)。

- `auto`：新路由默认值，按模型名称匹配词表目录名或模型别名，未匹配时使用估算。
- `estimate`：明确按 UTF-8 字节数估算，不承诺任意供应商的精确计费。
- 词表 ID：从程序自动枚举的列表中手动选择，与当前模型独立配置。不再使用旧的 Path / Sha256 配置项。

新增或替换词表后重启应用。构建和发布自动携带 `Tokenizer` 目录。已有路由保留原选择，可在界面改为“自动匹配”，无需数据库迁移。

词表只计算文本分词，消息封装、工具和图片采用显式预算。messageTokenOverhead 默认 16，imageTokenBudget 默认 4096，这些不是供应商承诺的实际用量。[DeepSeek 官方说明](https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash)还提供了完整提示编码工具 encoding / deepseek-recipe，文本词表计数不能等同于完整请求精确计数。最终按供应商 usage 结算，缺失 usage 时使用估算。

每日与每月额度按 UTC 统计。调用前用短事务预留输入预算与最大输出额度，调用后结算；失败释放预留，异常终止的预留 15 分钟后失效，过期一天后清理。跨日仍存活的预留计入额度。网络调用不占用事务，不增加进程级全局锁。

## 前端与项目结构

Memory / Persona / Emotion / Chat 已并入 Host/Features，AI 项目删除，模型传输位于 Infrastructure/Models。必要的 OpenAI 协议适配继续支持自定义供应商，不承担旧事件版本兼容。旧静态控制台和旧事件解析路径已移除。Contracts 按用途和类型拆分。

Vue 控制台使用同源 Cookie、X-Wonder 请求头和原有 Identity 账号，无需重新注册。开发运行 `npm --prefix src/Wonder.Web run dev`，代理到 localhost:5080。发布自动构建前端并复制至 wwwroot，也可先构建前端再用 `-p:SkipFrontendBuild=true` 发布。

控制台使用官方 Arco Design Vue 组件与默认主题，通过外部 CSS 加载样式。开发与生产使用相同的样式入口，无需静态提取或运行时 nonce 补丁。CSP 的 style-src 只允许同源资源，不允许 unsafe-inline。

## 回退

不要让旧程序直接消费已升级事件。优先停止应用后恢复升级前数据库备份并部署旧版。Down 迁移会移除 Quartz 表、Tokenizer 配置和未结算预算，不是保留新版配置的无损回退。
