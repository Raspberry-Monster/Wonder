# DeepSeek Flash 配置示例

这是仓库的可选供应商配置示例；通用入口支持任意符合兼容接口要求的供应商。五项生成任务可以复用模型和凭据，Embedding 需要独立的向量模型。

## 配置方式

1. 登录后进入 Bot → 模型路由 → “批量应用模型”，选择生成任务，填写供应商实际提供的 API 基础地址、模型名和 Key。
2. 按供应商支持的能力设置图片、JSON、思考模式、窗口和输出上限，再应用到所选任务。未选任务与 Embedding 不受影响。
3. 在“单独调整任务”配置 Embedding 的地址、模型、Key 和维度。

[路由示例](examples/deepseek-flash-routes.json) 使用 `https://api.deepseek.com` 和 `deepseek-flash`，这些是示例参数，不是应用内置默认值。使用前核对供应商文档中的模型名称、能力、思考参数和输出限制；第三方兼容供应商应填写其自己的值。

示例中的生成任务设置如下：

| 任务 | 思考模式 | 最大输出 Token |
| --- | --- | ---: |
| Chat | 关闭 | 2048 |
| LightReflection | 关闭，声明支持 JSON 输出 | 4096 |
| StrongReflection | 开启，high，声明支持 JSON 输出 | 8192 |
| DailyNarrative | 关闭 | 1024 |
| RollingSummary | 关闭 | 2048 |

示例为每项任务保存独立路由，上下文窗口为 32768，Chat 声明支持图片。这些数值与能力需按实际供应商调整，不代表经过真实供应商评估的推荐配置。StrongReflection 表示复核任务，是否使用不同或更强的模型由配置决定。

## Tokenizer 与预算

默认使用 `auto`，根据 Host 的 Tokenizer 目录名和 models.json 别名匹配。也可手动选择词表或使用 UTF-8 字节估算。文本词表不是完整 Chat Prompt 编码器，消息、工具、图片和安全余量还需占用预算。

词表配置见 [模型路由](model-routes.md#tokenizer-和预算)，计数和结算见 [统一上下文预算](context-budget.md)。实际费用按手填单价和 usage 估计，未细分缓存或分时价格，最终以供应商账单为准。

## API 参数

将示例中的 Key 替换为实际值后，把数组发送到 `PUT /api/v1/bots/{id}/model-routes`，携带登录 Cookie 和 `X-Wonder: 1`。示例未包含 Embedding，不会覆盖已有向量路由；填入真实密钥的文件不要提交到仓库。

- `thinkingEnabled` 为 null 或省略：不发送 thinking，由供应商决定默认行为。
- false / true：发送 `thinking.type = disabled / enabled`。
- `reasoningEffort` 非空时必须明确开启思考，作为顶层 `reasoning_effort` 传递；值需要由供应商支持，服务端不限定专用枚举。
- 同一任务更新时省略思考字段会清空旧值；ApiKey 留空保留旧密钥，新任务需要提供密钥。
- Embedding 不接受思考参数。

纯文本消息使用字符串 content，图片使用内容块。工具链按兼容协议保留需要回传的 reasoning_content；只有最终 content 发送到 QQ。生成用量按供应商 usage 记录，缺失时估算。

部署和密钥持久化见 [运行指南](operations.md)。协议测试使用模拟供应商，真实模型语义、图片和工具兼容性需单独验收。

参考：[模型与价格](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)、[思考模式](https://api-docs.deepseek.com/guides/thinking_mode/)、[消息接口](https://api-docs.deepseek.com/api/create-chat-completion/)。
