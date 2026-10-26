# 交互工具与 Notes

用户通过 QQ 命令与管理台操作，Miku 通过结构化工具执行交互、笔记和主动整理。工具的 Bot、用户和 Conversation 均来自真实 Chat 回合，模型不能指定任意主体。写工具只向已绑定私聊开放。

## 用户命令

| 命令 | 行为与条件 |
| --- | --- |
| `/help` | 服务端返回命令帮助；静音期间可用，不调用模型、不扣 Force 次数、不延长 Session |
| `/bind <口令>` | 私聊绑定控制台账号与 QQ；一次性口令由管理台生成，5 分钟有效 |
| `/end` | 将当前分段截止时间设为现在；需允许保存消息，群聊还需符合触发策略 |
| `/force <完整内容>` | 已绑定本人在静音期间发起一次接触；每个静音期初始 3 次，未静音时不扣次数 |

群策略可以额外配置聊天触发前缀。Mute、Unmute、Reset、主动 Reflection 和 Notes 是 Miku 专用工具，输入同名文字不会执行。实时帮助的实现见 [UserCommands.cs](../src/Wonder.Host/Features/Chat/UserCommands.cs)。

## 启用工具

Bot 的 Chat 模型路由需要开启 `supportsTools`，模型需支持兼容的 function calling。时间和历史查询的只读范围见[对话延续与时间](conversation-continuity.md#两个只读工具)。写能力另由 Host 配置控制，默认全部关闭：

```json
{
  "Chat": {
    "InteractionToolsEnabled": false,
    "ReflectionToolsEnabled": false,
    "NotesToolsEnabled": false
  }
}
```

环境变量分别为 `Chat__InteractionToolsEnabled`、`Chat__ReflectionToolsEnabled`、`Chat__NotesToolsEnabled`。Notes 还需本人开启独立授权，主动 Reflection 还需本人允许记忆。上述开关不限制管理台中对已有记忆的纠正、归档、Forget 和 Erase。

## 静音与 Force

| 工具 | 行为 |
| --- | --- |
| `interaction_mute(expires_at, reason?)` | 设置绝对结束时刻，静音普通输入；不限制 Miku 的合法输出 |
| `interaction_reset_force` | 在获授权的 Force 回合中，将同一有效静音期的剩余次数设为 3，并推进代际；不解除静音 |
| `interaction_unmute` | 在获授权的 Force 回合中，提前结束同一有效静音期 |
| `interaction_no_reply` | 在 Force 回合中选择不回复，立即持久结束该回合，不再执行后续工具或发送同次模型文字 |

普通回复不会自动解除静音。静音截止时间由 PostgreSQL 时钟判断，不依赖定时扫描成功。被阻止的输入只保留去重标识，正文和消息段为空，不进入 Session、上下文、反思或后续补送。

Force 绑定原静音期，跨 Session 仍使用同一余额。只有永久失败、尚未开始 Agent 处理、同一有效静音期、同一代际且未补偿的 Force 才返还一次，余额最多 3。Session 扫描补做崩溃窗口中的返额；已返额的旧任务不能再次进入 Agent。

## 主动整理

`reflection_request_review(priority, reason)` 请求整理当前输入及之前的合格本人消息，不结束聊天。每 Bot 按 `Chat:TimeZone` 的自然日共用 2 次 Normal 和 1 次 Important；受理后失败不自动返额，重试原任务不重复计数。业务次数与 UTC Token 日／月额度分别计算。

`reflection_list_runs`、`reflection_get_run` 只读取本人运行记录。界面分别显示反思阶段、实际 Job 状态、直接下游和记忆维护进度；反思完成不代表所有记忆已落地。整理原因保留到本人清除，清除原因不删除整理结果、次数或任务记录。

自动与主动 Full 共用来源签名和 generation 受理路径；重复、无合格来源或不可复用的请求不新建 generation，也不消耗新的主动次数。来源签名、结果复用与原任务重试见 [Reflection 与 Memory](reflection-memory.md#会话结束反思-full)。

## Notes 授权与版本

Notes 与正式 Memory 分开保存，独立授权默认关闭。Notes 可以包含观察、猜测或过时判断，不能自行认证用户事实，与 Memory 冲突时需要确认。没有 Notes Embedding。

| 来源 | 管理范围 |
| --- | --- |
| Agent Notes | Miku 可在当前授权私聊内创建、更新和软删除；不直接向用户展示正文 |
| User Notes | 本人在管理台查看、修订、移除及查看历史；Agent 可在授权范围内读取，不能修改 |

工具包括 `notes_create`、`notes_search`、`notes_read`、`notes_update`、`notes_delete`。默认读取当前有效版本；显式读取历史时标明已被取代。更新需匹配期望版本，修订保留旧文与原因。Notes 不自动过期。

读取 Note 时记录 `NoteId + Version` 依赖，沿 Chat、助手消息、摘要和下游 Note 传播。修订时在同一事务中隔离旧版派生内容与待发送结果；每次模型续请求、结果提交及发送领取都会复核来源。在途模型持有旧来源时停止输出，按原任务有限重试。

关闭授权会停用相关依赖。本人选择彻底删除关联 Notes 时，服务端推进 `NotesEpoch`、保留最小数据库撤销记录，清除关联笔记当前正文、全部修订及已知派生正文。已发送的 QQ 内容无法由本地清理撤回，在途或回执不明仍保留真实发送状态。

Memory 纠正或删除只隔离有明确来源依赖的 Notes，不会顺带彻底删除独立笔记。数据库备份恢复的边界见[运行指南](operations.md#运维备份与恢复)。

## 幂等与提交

写工具在短事务中校验当前 Running 租约、主体、授权、来源及目标版本，并提交变更、回执和必要 Outbox。网络调用不持有该事务。

`AgentOperation` 使用 ChatJob 与工具槽位作为持久身份，核对动作和参数摘要后返回已提交回执，不依赖 SDK 临时 CallId。已提交的 Unmute 或 Note 修订重试，不重新要求旧目标状态仍然成立；撤销授权后不能读回敏感正文。

重新领取租约后，只能重放已提交且参数匹配的写槽位。无法证明的新写动作返回 `AmbiguousRetry`，避免模型重新生成工具序列造成重复副作用。NoReply 的持久终态也在任务恢复和失败处理之前检查。

实现入口为 [InteractionBoundary.cs](../src/Wonder.Host/Features/Chat/InteractionBoundary.cs)、[NoteSources.cs](../src/Wonder.Host/Features/Chat/NoteSources.cs) 和 [NotesController.cs](../src/Wonder.Host/Controllers/NotesController.cs)。测试方式见[测试指南](../tests/README.md)。
