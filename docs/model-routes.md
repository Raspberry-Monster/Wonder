# 模型路由与批量应用

每项任务各自保存模型路由，可以使用相同或不同的模型。生成任务使用 OpenAI 兼容的 chat/completions 接口，Embedding 使用独立的 embeddings 接口。

## 控制台

进入 Bot → 模型路由 → “批量应用模型”：

1. 勾选需要更新的生成任务，默认选中聊天回复、轻量反思、复杂反思、每日叙事和滚动摘要。
2. 填写供应商的 API 基础地址和模型名称。入口不预填特定供应商或模型。
3. 填写共用 API Key；若所选任务均已配置密钥，可以留空保留各自原有密钥。包含未配置密钥的任务时需要填写。
4. 设置共用的上下文窗口、输出上限、价格、图片能力、JSON 能力和思考选项，再点击“应用到所选任务”。

批量应用会用表单中的整套配置覆盖所选任务，未选任务保持原样。默认上下文为 32768、最大输出为 2048、单价为 0；图片和 JSON 能力默认关闭，思考模式跟随供应商默认。能力和参数需要按实际模型填写，没有针对特定模型或复杂反思的自动覆盖规则。价格为 0 时费用估算也为 0。

Embedding 不在生成任务的批量选择中，请到“单独调整任务”设置向量模型、接口、密钥及维度。生成任务也可在这里分别调整，例如给复杂反思设置更高输出上限或不同思考模式。图片、JSON 和思考参数仅适用于提供相应兼容能力的供应商。

## API

将 [通用批量配置示例](examples/model-routes.json) 中的地址、模型和密钥替换为实际值，把数组发送到 PUT /api/v1/bots/{id}/model-routes，携带登录 Cookie 和 X-Wonder: 1。数组可以只包含部分任务。空密钥保留该任务旧密钥，新任务必须提供密钥。不要提交包含真实密钥的配置文件。

- thinkingEnabled 为 null 或省略：不发送 thinking，跟随供应商默认。
- thinkingEnabled 为 false / true：发送 thinking.type = disabled / enabled。
- reasoningEffort 允许 null、low、high、max；非空时要求 thinkingEnabled=true。
- 更新同一任务时省略思考字段会清空旧值；Embedding 不接受思考参数。

更新应用时，按 [运行指南](operations.md#更新应用) 执行数据库迁移。

DeepSeek Flash 的具体参数配置保留为一个可选示例，见 [DeepSeek 指南](deepseek.md)。
