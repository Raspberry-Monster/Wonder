# Reflection 与 Memory 触发机制

Reflection 在后台从用户消息中提取提案；Memory 校验事实候选，再交给维护任务决定是否写入或更新长期记忆。聊天与反思分别排队，当前回复不会等待反思完成，新记忆通常影响后续对话。

## 消息到记忆的链路

```mermaid
flowchart TD
    MSG[收到合格用户消息] --> LIGHT[即时反思 light]
    END[会话空闲到期或主动结束] --> ADMIT[按本人来源签名受理]
    TOOL[主动整理工具] --> ADMIT
    ADMIT --> FULL[原子创建 Full generation 与 Outbox]
    LIGHT --> LLM[LightReflection 提取提案]
    FULL --> LLM
    LLM --> CHECK{需要强模型复核？}
    CHECK -->|是| STRONG[StrongReflection]
    CHECK -->|否| RESULT[分批完成并发布反思结果]
    STRONG --> RESULT
    RESULT --> EMOTION[Emotion 情绪应用]
    RESULT --> RELATION[Relationship 偏好应用]
    RESULT --> MEMORY[Memory 校验与候选保存]
    MEMORY --> MAINTAIN[MemoryMaintenance 对照已有事实]
    MAINTAIN --> OUT[新增 / 合并 / 补充 / 更新 / 归档 / 暂缓]
```

## 即时反思 light

每条同时满足以下条件的消息都会排入任务，不按累计消息数触发，也不需要等聊天回复成功：

- Bot 已启用，且允许消息持久化。
- 消息是符合回复规则的有效私聊输入，未被 Mute 拦截；合法 Force 可按原授权参与反思。
- `/help`、`/bind`、`/end` 由服务端处理，不创建即时反思任务，命令正文不作为反思来源。
- 发言用户开启 `AllowMemory`，该设置默认关闭。
- 不包含群聊消息；生成和应用入口都校验私聊范围。

即时反思只分析当前用户的这一条有效消息。群消息不进入 light 或 full；Notes 和摘要不能替代本人原始消息证据。

当前 `AllowMemory` 同时约束反思生成及情绪、关系、记忆结果的应用，并非只控制最终事实落库。

## 会话结束反思 full

| 条件 | 当前行为 |
| --- | --- |
| 空闲到期 | 私聊、群聊默认均为 10 分钟，可按 Bot 配置；触发回复的入站消息延后截止时间 |
| 主动结束 | `/end` 将已有活动 Session 截止时间设为现在；仍需符合持久化、群聊触发及静音限制；不创建 Chat 或 light 任务，full 只整理此前的合格聊天内容 |
| 检查与排队 | Quartz 内存触发器每 15 秒扫描到期 Session，等待该 Conversation 的聊天和发送任务完成 |

主动 Full 由结构化工具在已绑定私聊的 Chat/Force 回合受理，不关闭 Session，固定当前输入上界。按 Bot 每天共用 2 Normal + 1 Important；已受理失败不自动返额，原任务重试不重复扣次。Full 以实际合格用户来源签名去重，助手使 Boundary 增加不会造成重复评估；Result 与三类下游一次发布。工具启用与调用方式见 [交互工具与 Notes](agent-tools.md#主动整理)。

规范运行以实际合格本人消息的有序 ID、正文和 Epoch 生成来源签名；Boundary 只作为快照上界。自动与主动 Full 共用受理路径，重复或无来源不消耗主动次数。结果、三类下游和运行完成状态在同一事务发布；配额、请求记录及新任务也原子提交。主动日配额按 Bot 的当地自然日计算，时区改变后以 24 小时保守隔离防止重复领取；TokenBudget 仍按 UTC。

Full 的去重键为 Bot、Session、本人和来源签名。相同来源仍在排队或运行时复用原 generation；已完成且结果可用时返回已处理；死信需要修复并重试原任务，不另扣主动次数。已取消或结果不可复用的相同来源不会自动新建任务。来源签名发生变化时，即使 Boundary 相同也允许创建新的 generation；Full 结果没有 Boundary 唯一约束。

Full 任务在调度事务中创建 generation，执行时必须已有对应记录，不会懒接入或从结果 Boundary 推断运行。Light 流程独立，不创建 Full generation；数据库只对 Light 结果的 Session、本人、Kind 和 Boundary 组合施加唯一约束。

扫描固定会话结束的消息边界，并为每位具有可反思消息、且仍开启长期记忆的用户分别排入 full。分析范围是该 Session 内、截至边界、属于该用户且允许反思的消息，不把助手回复或他人发言当成本人事实。

full 会覆盖已经做过 light 的消息。候选去重、事实合并和情绪证据检查用于限制重复应用。会话扫描等待相关 Reflection / Emotion / Relationship / Memory 的 Pending、Running 任务消失后关闭 Session；MemoryMaintenance 不属于等待条件，DeadLetter 也不继续阻塞会话关闭。

## 模型选择与长输入

`light` / `full` 表示分析范围，`LightReflection` / `StrongReflection` 表示模型路由。full 不会直接跳过轻量模型。

两种任务都先调用 LightReflection，返回 `needsStrongReview`、`emotion`、`relationships` 和 `memories`。模型要求复核时，才调用 StrongReflection；复核后仍要求进一步复核的片段不采纳为有效提案。

长输入按各自模型预算分批或分片，保存游标、来源指纹和中间结果。所有批次结束后发布一份 ReflectionResult，再创建三类下游任务。来源撤回、正文变化或授权失效会阻止旧结果提交。提案可以为空，反思成功不保证产生记忆。

预算、分片及恢复见 [统一上下文预算](context-budget.md)。

## 反思结果的应用

| 任务 | 准入和效果 |
| --- | --- |
| Emotion | 检查证据；与最近检查范围内已应用事件证据重叠时不重复应用；每用户对同一 Bot 每 UTC 日最多 5 次 |
| Relationship | 明确偏好或至少 3 条独立消息证据才能准入；按 Bot / 本人私聊范围写入 RelationshipClaims，模型读取经过资格和来源检查的投影；Tentative / Unverified 项仅作非事实参考 |
| Memory | 要求明确、非敏感、正文和 FactKey 合法、本人消息证据及授权有效，并通过旧断言抑制/引用与当前确认核验；只保存隔离候选 |

Memory 候选正文上限 1000 字符、FactKey 上限 150 字符。同一反思里的完全重复候选只排入一个维护任务。

## 从候选到正式记忆

MemoryMaintenance 是候选触发的任务，不是固定时间全库扫描：

1. 正文与已有事实完全相同，直接合并证据，不调用模型。
2. 其他候选生成向量，在同 Bot、本人及同一作用域内召回 Top 10，并加入同 FactKey 的事实。
3. 没有相关事实可直接新增；存在相关事实则调用 StrongReflection 判断操作，没有相似度阈值自动合并规则。
4. 服务端根据用户开关、证据时间、授权、来源和并发状态再次核验，再提交或暂缓。

向量 Profile 改变时，缺少的已有事实向量先补建，维护任务等待后重新检查。候选在正式准入前不参与聊天检索；不确定的事实可暂缓，已有记忆保留。详细决策、私人版本与删除规则见 [自主维护记忆](memory-maintenance.md)。

## 其他任务与故障

- 每日维护仅推进模拟心境，不调用模型或定时重跑整套 Reflection / Memory。
- 滚动摘要是对话上下文压缩，与长期事实记忆分开。
- 模型路由、密钥、供应商错误和额度会影响后台处理；修复和重试见 [运行指南](operations.md#任务失败与重试)。

## 代码入口

| 文件 | 职责 |
| --- | --- |
| [MessageProtocol.cs](../src/Wonder.Core/Protocols/MessageProtocol.cs) | 保存、回复及反思触发判断 |
| [MessageIngestor.cs](../src/Wonder.Host/Features/Chat/MessageIngestor.cs) | 会话活动、截止时间与 light 创建 |
| [MaintenanceWorkflow.cs](../src/Wonder.Host/Jobs/MaintenanceWorkflow.cs) | 会话扫描、full 创建和关闭 |
| [ScheduledJobs.cs](../src/Wonder.Host/Jobs/ScheduledJobs.cs) | Quartz 内存触发器及每日任务补排 |
| [ReflectionRuns.cs](../src/Wonder.Host/Features/Memory/ReflectionRuns.cs) | Full 来源签名、generation 受理与复用 |
| [ReflectionWorkflow.cs](../src/Wonder.Host/Features/Memory/ReflectionWorkflow.cs) | 反思生成、复核、结果应用和候选创建 |
| [MemoryMaintenanceWorkflow.cs](../src/Wonder.Host/Features/Memory/MemoryMaintenanceWorkflow.cs) | 事实召回和维护决策 |
