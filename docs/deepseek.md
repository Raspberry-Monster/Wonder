# DeepSeek Flash 配置

五项生成任务可以使用同一个 Flash、接口和 API Key。Embedding 单独使用向量模型。这里的“复杂反思”是第二次复核，不代表自动换成更强的模型。

## 控制台配置

1. 更新已有部署时，先运行数据库迁移：

~~~powershell
dotnet run --project src/Wonder.Host -- --migrate
~~~

容器部署使用 [Compose](../README.md#容器运行) 或 [Quadlet](../deploy/quadlet/README.md) 指南中的迁移命令，再启动新版应用。

2. 登录后选择 Bot → 模型路由 → “批量应用模型”。这是所有兼容模型共用的入口。勾选目标任务，填写 Flash 的地址、名称和 API Key，按实际能力设置图片、JSON、思考模式及输出上限后保存。此操作覆盖所选任务的整套路由参数；不会更改未选任务或 Embedding 路由。

3. 在“单独调整任务”中配置 Embedding，填写相应供应商的基础地址、向量模型、API Key 和正确维度。当前 Flash 聊天接口不能替代向量接口；项目仍请求独立的 /embeddings。

使用官方 Flash 时，填写基础地址 https://api.deepseek.com 和模型名 deepseek-flash。批量入口不预填这些值，也不会自动给复杂反思开启思考。通过其他供应商调用时，填写该供应商支持的地址、模型名和参数。通用操作见 [模型路由指南](model-routes.md)。

下面是一套可选的 Flash 起始配置，批量保存后可在“单独调整任务”中分别修改：

| 任务 | 思考模式 | 最大输出 Token |
| --- | --- | ---: |
| Chat | 关闭 | 2048 |
| LightReflection | 关闭，使用 JSON 输出 | 4096 |
| StrongReflection | 开启，high，使用 JSON 输出 | 8192 |
| DailyNarrative | 关闭 | 1024 |
| RollingSummary | 关闭 | 2048 |

上述配置为五项任务各自保存独立路由，共用相同凭据。Chat 开启图片输入能力。应用上下文预算先设为 32768，不必一次用满模型提供的窗口；后续可以按实际对话调整。输出上限应同时给思考和最终答案留空间，这些数值只是起点，尚未通过真实供应商评估。

## 通过 API 配置

[五项路由示例](examples/deepseek-flash-routes.json) 对应上表的 Flash 配置，作为供应商专用示例保留，不是通用批量入口的默认参数。替换每项的 API Key 后，将数组发送到 PUT /api/v1/bots/{id}/model-routes，携带登录 Cookie 和 X-Wonder: 1。示例没有包含 Embedding，不会覆盖已配置的向量路由。不要将填入真实密钥的文件提交到仓库。

- thinkingEnabled 为 null 或省略：不发送 thinking，使用供应商默认值；DeepSeek 官方当前默认开启思考。
- thinkingEnabled 为 false：发送 thinking.type = disabled。
- thinkingEnabled 为 true：发送 thinking.type = enabled。
- reasoningEffort 允许 null、low、high、max；非空时必须明确开启思考，发送到请求顶层的 reasoning_effort。
- Embedding 不接受思考参数。单任务 PUT 不影响其他任务；同一任务更新时省略思考参数会清空原有值，恢复供应商默认。API Key 留空或 null 保留该任务的旧密钥，新任务需要提供密钥。

纯文本消息使用字符串 content，带图片的用户消息使用内容块数组。只把最终 content 作为回复，不把 reasoning_content 发到 QQ；输出用量仍按供应商返回的 completion_tokens 记录。当前费用采用手填输入 / 输出单价估算，尚未细分缓存命中和分时价格，实际账单以供应商为准。

配置和接入格式已通过模拟供应商测试；没有使用真实 DeepSeek API Key 进行联调。

参考：[官方模型说明](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)、[思考模式](https://api-docs.deepseek.com/guides/thinking_mode/)、[消息接口定义](https://api-docs.deepseek.com/api/create-chat-completion/)。
