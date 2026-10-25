# ADR-013 本地实施附录

2026-10-24。依据下载目录 v0.5、`agent-autonomy-requirements.md` 和本轮用户确认；源码是实现事实。原架构副本保存在 `architecture.pre-adr013.md`。本轮未修改原有需求澄清与登录页 CSS 改动，未迁移或重启运行中的服务。

## 用户确认与覆盖关系

- Mute 只限制接收普通输入，Miku 的合法输出继续；回复不自动 Unmute。仅已绑定私聊的 Conversation 开放交互工具与 Force，Force 仅该用户可用。Mute 必须给出绝对结束时刻，不设置未批准的默认时长、最长时长或 Reset 次数上限。
- Reset 恢复剩余数为 3、推进代际，不解除 Mute；NoReply 是单次 Force Chat 的持久终态。
- 主动 Reflection 按 Bot 共用每日 2 Normal + 1 Important，上界固定当前 Chat 输入；受理后永久失败不自动返额，重试原任务不重复计数。
- **最终归档澄清覆盖先前选择**：不增加逐条审批。自动独立 Archive 继续受现有用户开关、证据及版本校验控制；Update 替代旧版保持原行为。
- Notes 独立授权、默认关闭。Agent Notes 不向用户直接展示；User Notes 可由本人查看、修订、移除。用户可以关闭授权并彻底删除与自己关联的全部 Notes。Notes 与 Memory 冲突需要确认，不能自行认证用户事实。
- 保留期已确认：Notes 与整理原因不自动过期，保留到本人主动删除/清除；整理原因清除入口只清除原因正文，不删除结果、次数和任务记录。
- **最终群聊澄清覆盖最初保留完整群聊的要求**：群聊保留基础聊天、当前发言者标签和本人设置的称呼；不读取记忆、关系、历史或滚动摘要，不生成 Reflection、维护或群记忆向量。旧群／共享数据按 2026-10-25 后续授权完整归档后退出在线表，清理兼容入口已移除；私人事实和独立 Notes 保留。

## 最小数据增量

沿用同一个 DbContext 和迁移链、PG Outbox/Inbox、Quartz、六类模型路由及 UTC TokenBudget。新增 10 张业务表，无第二任务系统，无 Notes Embedding。

| 数据 | 键、用途和边界 |
| --- | --- |
| MuteSession | Conversation、已验证用户、绝对到期、剩余数、ResetGeneration、并发 Revision；余额约束 0～3 |
| ForceReceipt | 唯一 Bot+平台事件、唯一 ChatJob；关联原 Mute、真实消息、ChargeGeneration、处理开始和补偿结果 |
| AgentOperation | 唯一 ChatJob+工具槽位；执行 Lease、主体、动作、参数哈希和无正文回执 |
| ChatDisposition | ChatJob 主键；ForceReceipt、NoReply、操作与提交时刻 |
| ReflectionGeneration | 唯一 Bot+Session+Subject+有效来源签名；规范 Job、原始 Boundary、Epoch、Result 和取消状态 |
| AgentReflectionQuota / Request | Bot+当地自然日配额；请求关联 Generation、原始上界、优先级、原因、操作 |
| Note / NoteRevision | Agent/User 来源、当前已验证私聊主体、当前版本、正文、时间、历史修订及原因；无任意用户/群作用域 |
| NoteDependency | NoteId+Version → Chat/Message/Summary/Note；覆盖工具读取、历史继承、摘要与出站 |

LogicalUser 新增 AllowNotes 和独立 NotesEpoch；NotesEpoch 是并发令牌。Notes 通过已认证私聊的 UserId/ConversationId 关联单一数据主体，不新建可任意指定主体的共享笔记。

撤销账本使用类型化 ResourceKind/ResourceId/ResourceVersion。重复编码资源类型的 RecordVersion 已删除；旧格式由 --convert-ledger 离线转换，Note 修订号继续保留。旧群／共享事件转换为 MemoryContext，不能误删私人事实。只新增前向迁移，不改既有历史迁移。

## 工具与用户命令

模型的 SupportsTools 仍表示兼容 function calling。另有独立服务端配置：

```json
"Chat": {
  "TimeZone": "Asia/Shanghai",
  "InteractionToolsEnabled": false,
  "ReflectionToolsEnabled": false,
  "NotesToolsEnabled": false
}
```

三个新开关默认关闭；显式开启所需模块并为 Chat 开启 SupportsTools 后才暴露对应写能力。开启 Notes 还需要本人独立授权。不从旧只读开关附带授予全部写权限。工具 Schema 参与原有统一预算。

- 只读：get_current_time、search_conversation_history；群聊只有时间工具，没有历史读取。
- 交互：interaction_mute(expires_at, reason?)、interaction_reset_force、interaction_unmute、interaction_no_reply。后三者绑定真实 Force 回合；新 Reset/Unmute 只能操作 receipt 绑定的同一有效 Mute。
- Notes：notes_create/search/read/update/delete。Agent 创建、更新、软删除只作用于 Agent Notes；读取可包含同范围的 User Notes。默认只查当前有效观点，显式历史标注 Superseded。正文与工具结果均不可信。
- Reflection：reflection_request_review(priority, reason)、reflection_list_runs、reflection_get_run。范围全部派生自真实 Chat；不能传任意 Bot/User/Session。
- 用户命令：/help、/bind <口令>、/end（/结束）、/force <完整内容>，以及群策略配置的触发前缀。/help 服务端直接回答，区分用户命令和 Miku 工具；Mute 中可用，不调用模型、不扣次数、不延长 Session。规划中的能力不作为用户命令列出。

每轮只提供消息自身的时间、固定时区和必要的跨 Session 标记，不注入当前钟点、星期或相对现在的 elapsedSeconds。未开启工具时明确告知不能实时查询时间，不静默恢复时钟注入。

## 事务、重放和终态

**入站**：绑定/去重 → 服务端命令 → Conversation → PG 时钟判定 Mute/Force 权限及完整输入预算 → Active Session → Message+Sequence → Receipt/扣次 → Chat/合格 light，一个短 Serializable 事务提交。受阻消息仅保留去重标识，正文和 Segments 为空、ContextInvalidated=true、无 Session；不补送，不延长活动时间，不进入任何派生路径。Force 跨旧 Session 关闭后仍归属于原 Mute。

**写工具**：有效 Running Lease 和当前回执访问资格 → 查持久工具槽位 → 核对主体/动作/参数哈希并返回已提交回执 → 仅新操作检查设置版本、来源、授权与目标前置状态 → 同事务提交变更/回执/必要 Outbox。已提交 Unmute 不重新要求 Mute 有效；已提交 v1→v2 不重新要求 Note 仍是 v1。撤销资格不能读回敏感内容。

工具身份不依赖 SDK 临时 CallId。第一版采用保守恢复：重新领取 Lease 后，只能重放已提交且参数匹配的槽位；不接受无法证明是新动作的新写槽位，返回 AmbiguousRetry，避免模型重生成工具序列导致重复副作用。网络请求不持有事务。

**NoReply**：工具受理事务写入终态后立即终止循环；忽略同次供应商响应中的文字。恢复先查终态完成 Job/Inbox，不再调用模型；失败处理同样先排除 NoReply，禁止额外失败发言。

**Reset 与返额**：Reset 原子设置 3 并递增代际。仅终结失败、从未开始 Agent 处理、同一有效 Mute、同代且未补偿的 Force 可返一次，最多 3；否则记录跳过。Quartz 的既有 Session 扫描补做“失败已提交但返额前崩溃”的回执；已返额的旧 Force 即使被人工重试，也不再投递给 Agent。

## Notes 即时隔离与恢复

工具读到 Note 时记录版本依赖；Chat 将实际选入的历史/摘要依赖继承到本轮，再传到助手消息；摘要继续继承来源。模型每次后续请求、结果提交和每段出站之前检查来源。普通修订保留旧文和原因，同事务隔离旧版衍生的助手历史、摘要、下游 Note 和待发送内容；不能等异步摘要重建后才失效。活动模型持有旧源时停止输出并走原任务有界重试。

独立 Notes 隐私删除在同事务推进 NotesEpoch、写稳定 Note 删除账本/墓碑、使目标及衍生内容不可用，清除目标当前正文和全部 Revision，并清除其已知派生正文。已发送内容无法本地撤回，发送中/回执未知沿 DeliveryUnknown 处理。没有 Notes 向量或额外明文缓存需要清理。

恢复顺序仍是 stop → restore → migrate → --replay-ledger 最新独立账本 → start。重放在启动 Worker、OneBot、外部查询前完成；旧格式账本必须先显式离线转换。保证取决于最新账本已独立导出且可取得，原有 age 配置和丢失边界保持不变。

Memory 更正/删除或来源 Note 修订不会顺带彻底删除所有独立 Notes；只将明确来源依赖的判断隔离。独立 Notes 的彻底删除走本人专用入口。Notes 的删除范围不因共享功能退役而扩大；共享退役的数据归档已经后续明确授权。

## Full 规范运行与旧任务兼容

Full 的签名来自实际合格本人用户消息的有序 ID、正文和 Epoch，原始 Boundary 只用作快照上界/审计。用户 100 / 助手 101 对应相同来源，只关联同一规范运行。自动 Session Full 和 Agent Full 共享受理方法；重复或无来源不扣主动次数，不另建生成任务。空提案属于成功，无输入源或撤销属于显式取消。

Result、Emotion/Relationship/Memory 三类 Outbox 和规范 Completed 同事务发布一次。日配额、受理请求和新规范 Job 也原子提交。日配额按 Bot 共用，时区改变后以 24 小时保守隔离防止重复领取；TokenBudget 仍按 UTC。

旧 v2 Chat/Outgoing/Reflection 载荷不变。旧 Full 在执行前加入同一规范协议，重复任务完成而不重复生成；旧结果缺乏可核验签名时不按 Boundary 猜测复用。同原始结果键拒绝硬插；没有新合格用户来源时，增加助手边界也不能绕过旧结果限制。死信返回 FailedRetryExisting，修复原任务，不另扣次数。

History/API/工具按本人读取，Owner 不获得额外正文读权；分开展示反思阶段、真实 Job 状态、直接消费者和记忆维护状态/数量，不把 ReflectionCompleted 当作记忆已全部落地。

群聊旧 Reflection、MemoryMaintenance、Embedding 和 Summary 任务在入口停止。旧群来源不能经私人摘要/历史或待发送输出重新进入上下文。ContextSpace、全部共享 API 和 UserShared 运行时例外已移除；迁移先归档旧记录再退出在线表。

## 2026-10-25 类型与兼容清理

封闭业务标签已改为 Enum，保留稳定协议值和现有整数编号；新增数据库 CHECK，拒绝未知值和数字枚举输入。ContextBuilder 的类别与排序权重已分离。ChatDisposition 记录本身表示 NoReply，不再存单值 Outcome。

Memory 仅以 BotId + SubjectId 授权，Scope/ScopeOwnerId/SharedFromMemoryId、ContextSpace 与群关系字段已移除。旧数据先进入无业务入口的只读归档；CLI 可独立加密导出。Reflection 的旧 Full 接管与旧结果防重继续保留，缺乏历史来源签名的结果不能冒充规范运行。详见 [检查与方案](enum-and-compatibility-cleanup-plan.md)。

## 验证与仍待确定

2026-10-25 清理后最终验证：构建零警告、EF 快照一致；单元 30、契约 15、启用真实 PG 的集成 112（无跳过）、前端单元 2、生产浏览器 5、开发浏览器 1，共 165 通过。原有预算事务并发回归补充短暂退避与有限重试，不改变额度规则。归档与旧账本转换详见清理方案；未对运行库执行迁移，未验证真实 age 加密导出往返。

测试覆盖位于 InteractionTests、AgentAutonomyTests 及既有 Continuity、Speaker、SharedContext、Reflection、MemoryMaintenance、LiveInfrastructure 等模块。真实 PG 临时库验证迁移升级、并发最后一次 Force、同槽位 Notes 创建/修订重放、授权撤销、100/101 去重和旧 Notes 状态恢复后的账本重放；不使用 SQLite 替代 PG 并发语义。

前端测试覆盖独立 Notes 授权/删除、User Notes 编辑、整理状态、群策略精简及登录样式。模型与 QQ 使用模拟适配器；未进行真实 QQ/供应商联调、加密 pg_dump 全流程恢复或生产部署。

本轮最终验证：解决方案构建通过、零警告；单元 23、契约 15、带真实 PG 的集成 109（无跳过）、前端单元 2、生产构建浏览器 5、开发服务器浏览器 1，均通过。PG 报告位于 `TestResults/adr013-pg-full`；运行库未执行迁移。OpenAPI 和前端类型已同步。

Notes 与整理原因按本人已确认的策略保留到主动删除/清除，不新增自动过期。History 的连续用满提醒阈值、无正文运行审计标识的最终保留周期仍未批准，本轮不新增相应提醒或自动清理。三个新增能力开关默认关闭，启用需显式配置。
