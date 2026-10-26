# 模型路由与批量应用

每项任务各自保存模型路由，可以使用相同或不同的模型。生成任务使用 OpenAI 兼容的 chat/completions 接口，Embedding 使用独立的 embeddings 接口。

两类接口均通过 Microsoft.Extensions.AI.OpenAI 官方适配器调用，供应商扩展参数通过 SDK 扩展点传递。模型路由、用量和额度策略由 Host 管理，组件边界见 [核心架构](architecture.md)。

## 五类任务

| TaskKind | 用途 |
| --- | --- |
| Chat | 聊天回复，可按模型能力开启图片和只读工具 |
| LightReflection | 即时与会话结束反思的首轮提案提取 |
| StrongReflection | 反思复核，以及候选与已有事实的维护判断 |
| RollingSummary | 生成和压缩滚动摘要 |
| Embedding | 检索、事实维护和向量补建 |

反思范围与模型复核触发条件见 [Reflection 与 Memory](reflection-memory.md)。这些路由可以复用供应商和凭据，但 Embedding 需要支持向量接口的模型。

## 控制台

进入 Bot → 模型路由 → “批量应用模型”：

1. 勾选需要更新的生成任务，默认选中聊天回复、轻量反思、复杂反思和滚动摘要。
2. 填写供应商的 API 基础地址和模型名称。入口不预填特定供应商或模型。
3. 填写共用 API Key；若所选任务均已配置密钥，可以留空保留各自原有密钥。包含未配置密钥的任务时需要填写。
4. 设置共用的上下文窗口、输出上限、价格、图片能力、JSON 能力和思考选项，再点击“应用到所选任务”。

批量应用会用表单中的整套配置覆盖所选任务，未选任务保持原样。默认上下文为 32768、最大输出为 2048、单价为 0；图片和 JSON 能力默认关闭，思考模式跟随供应商默认。能力和参数需要按实际模型填写，没有针对特定模型或复杂反思的自动覆盖规则。价格为 0 时费用估算也为 0。

Embedding 不在生成任务的批量选择中，请到“单独调整任务”设置向量模型、接口、密钥及维度。生成任务也可在这里分别调整，例如给复杂反思设置更高输出上限或不同思考模式。图片、JSON 和思考参数仅适用于提供相应兼容能力的供应商。

## API

将 [通用批量配置示例](examples/model-routes.json) 中的地址、模型和密钥替换为实际值，把数组发送到 PUT /api/v1/bots/{id}/model-routes，携带登录 Cookie 和 X-Wonder: 1。数组可以只包含部分任务。空密钥保留该任务旧密钥，新任务必须提供密钥。不要提交包含真实密钥的配置文件。

- thinkingEnabled 为 null 或省略：不发送 thinking，跟随供应商默认。
- thinkingEnabled 为 false / true：发送 thinking.type = disabled / enabled。
- reasoningEffort 为 null 或供应商支持的强度字符串，例如 low、high、max；服务端限制最多 32 字符，非空时要求 thinkingEnabled=true。
- 更新同一任务时省略思考字段会清空旧值；Embedding 不接受思考参数。

密钥使用 Data Protection 加密保存，读取接口只返回是否已配置。迁移应用或数据库时需要保留原密钥目录，见 [运行指南](operations.md#密钥解密失败)。

## Tokenizer 和预算

`GET /api/v1/bots/{id}/model-routes/tokenizers` 列出 Host 发现的词表，模型路由响应同时返回配置的 `tokenizerId` 和实际解析的 `resolvedTokenizer`。

- `auto`：默认值，按模型名称匹配词表目录名或 models.json 别名；匹配不区分大小写，无匹配时估算。
- `estimate`：明确按 UTF-8 字节数保守估算文本。
- 词表 ID：手动指定已发现的词表，不依赖供应商名称。

词表位于 `src/Wonder.Host/Tokenizer`，构建和发布会复制该目录；新增或替换后重启应用。格式见 [Tokenizer 说明](../src/Wonder.Host/Tokenizer/README.md)。

`contextWindow` 是完整上下文窗口，生成任务还需配置 `maxOutputTokens`。消息封装和图片使用 `messageTokenOverhead`、`imageTokenBudget` 显式估计，默认分别为 16 和 4096。词表只计算文本，不等于供应商的完整请求计费。上下文、工具结果和日 / 月额度规则见 [统一上下文预算](context-budget.md)。

DeepSeek Flash 的具体参数配置保留为一个可选示例，见 [DeepSeek 指南](deepseek.md)。

## 聊天只读工具

在单独调整“聊天回复”时，可勾选 supportsTools 开启当前时间与当前对话历史查询。默认关闭，仅在模型支持兼容工具调用时开启。批量更新省略此字段，会保留聊天路由已有工具设置；其他任务不能开启。工具权限和时间注入见 [对话延续与时间](conversation-continuity.md)。
