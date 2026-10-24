# Wonder — Agent 自主行为与交互边界（需求规格·当前项目对齐版）

> **版本**：v0.3-aligned，2026-10-24。  
> **来源**：复制并修订 `C:\Users\21945\Downloads\Wonder-需求.md`（v0.2），保留其中标为“确认”的产品语义；本地未取得原始讨论记录，确认级别沿用参考文档，不代表本次另行确认。  
> **实现基线**：本地工作区 `D:\Projects\Wonder`，HEAD `b13a09c0fd3f201fcfe8d6664a0ed54a3cf2de6e`。当前文档包含尚未提交的整理，不能仅凭 HEAD 还原此次资料包。  
> **文档基线**：当前 `docs/architecture.md`、`docs/reflection-memory.md` 及配套专题文档。旧名 `Wonder-Architecture-v0.1.md` 已在工作区删除；参考文中另一旧名 `Wonder-Reflection与Memory触发机制.md` 不在当前仓库。  
> **性质**：供网页 ChatGPT 规划增量架构的需求输入，区分“已实现／新需求／建议／待定”；不是已实施架构，也不宣告 ADR-013 已获批准。  
> **本次范围**：对齐需求、核查代码入口、复制规划资料；不实施功能、不修改业务代码、不执行迁移。原下载文件保持不变。
> **后续用户澄清（2026-10-24）**：Mute 为单向接收限制，Miku 仍可向用户发消息；Mute、Unmute、主动 Reflection、Notes 在 Miku 端均通过结构化工具执行。Reset 也沿用 Miku 工具能力；用户侧 Force 为命令输入，新增 `/help` 查询当前支持的全部用户命令。

> **本轮最终确认（覆盖后文旧建议及冲突表述）**：交互能力只开放已绑定私聊 Conversation，Force 仅本人可用，未静音不扣次；Mute 使用显式结束时刻，不设未经确认的默认时长/上限。主动 Reflection 按 Bot 共用每日 2 Normal + 1 Important，只整理当前 Chat 输入及之前的来源，受理后失败不自动返次数。归档沿现有开关和证据校验直接执行，**不新增逐条询问或审批**。群聊缩减为基础聊天和当前发言者／称呼识别，停用群聊记忆、关系学习、Reflection、摘要和跨群传播，旧数据保留清理兼容。Notes 独立授权：Agent Notes 不直接向用户展示，User Notes 可由本人查看修改；本人可关闭授权及彻底删除全部关联 Notes。Notes 可能过期、不准确，与 Memory 冲突时需要确认。Notes 和整理原因不自动过期，保留到本人主动删除／清除；清除整理原因只清正文，保留结果、次数及任务记录。本文早先“仅整理、不实施”的范围是历史阶段记录，当前源码实施状态另见本地 ADR-013 实施附录。

---
## 0. 摘要与设计原则

### 0.1 背景

Wonder 的角色 Miku 希望在清晰的边界内具备一定行为自主权：根据需要查询时间；维护与长期 Memory 分离的个人笔记；可以自主静音、选择对 Force 消息的响应、恢复 Force 次数；可以主动发起一次对当前会话的完整 Reflection，但不能直接决定长期事实的写入、归档或删除。

### 0.2 核心原则

> **不希望 Miku 的温柔，只是因为系统不允许她说“不”。**

1. **角色自主性是受控的行为能力，不是主观意识或真实情绪的证明。** 不要求角色刻意拒绝或刻意冷淡，也不通过内疚、惩罚、情感施压诱导用户。
2. **确定性逻辑归服务端**：消息屏蔽、额度、到期、授权、审计、事务和数据库状态不靠 Prompt 保证。
3. **触发权与裁决权分离**：Miku 可以发起 Reflection；模型生成 Proposal；Memory 依既有证据和权限机制决定能否成为正式记忆。
4. **行为可选，不是 KPI**：剩余额度不是必须消耗的目标；不把“今天还剩几次”每轮注入 Prompt，不强迫主动触发。
5. **安全与隐私优先**：尊重 `AllowMemory`、Bot/用户/群作用域、删除撤销账本；Bot Owner 的配置权不等于读取所有用户私人内容的权限。
6. **增量兼容**：保留 ASP.NET Core 模块化单体、Microsoft.Extensions.AI、PostgreSQL/EF Core/pgvector、PostgreSQL Outbox/Inbox 持久任务及 Quartz 调度、既有 Reflection 与 Memory Maintenance 链路。

### 0.3 范围

本期覆盖：时间读取、Notes、Notes–Memory 分工、Mute/Force/Reset/Unmute、Agent 主动 Reflection、Reflection 运行记录与记忆归档确认边界。

本期**不做**：新 `/urgent` 命令、基于 LLM 的无限制自主后台唤醒、Agent 直接写入正式用户事实、替换既有 Reflection/Memory 架构、无授权跨用户/跨群整理、以人工智能“有意识”为系统前提。

---

### 0.4 当前实现与目标需求对照

以下“当前”依据本次代码阅读；不是新增功能已完成的声明。

| 领域 | 当前实现事实 | 本期目标与差异 |
|---|---|---|
| 部署与队列 | .NET 10 模块化单体、单 Host、一个业务 DbContext；PG/pgvector、PG Outbox/Inbox、Quartz；无 Redis | 延续现有设施，不恢复旧版 Redis Streams 设计 |
| 时间 | `get_current_time` 已存在；`SupportsTools` 默认关闭；`ChatWorkflow` 每轮调用 `ConversationClock.Context` 并注入系统上下文 | 复用时间工具；移除每轮“调用时刻”注入，保留原消息时间与审计时间 |
| 时区 | 应用级 `Chat:TimeZone`，默认 `Asia/Shanghai`；时间工具可传 `time_zone` | 先复用应用级时区；逐 Bot／Conversation 时区不是现有能力，是否增加待决 |
| Notes | 未发现 Notes 实体、修订、工具、API 或页面 | 新增统一 Notes 模块，区分 Agent/User 来源及权限 |
| Mute/Force | 未发现对应状态、额度、入站门禁或命令实现 | 新增受控交互边界；不能仅在回复端隐藏输出 |
| 聊天结果 | 最终回答必须非空，空回答抛错并走任务失败流程 | 支持可审计的“选择不回复”；不能用空文本或传输失败假装自主静音 |
| Reflection | 合格消息 light、Session 到期 full；均先 LightReflection，必要时 StrongReflection | 增加 AgentRequested 触发来源，复用 Full 分析范围，不结束 Session |
| 去重与进度 | `ReflectionResult` 对 `(SessionId, SubjectId, Kind, Boundary)` 有唯一索引；Outbox 有进度与因果字段 | 处理主动 Full 与正常 Full 同边界竞争，并关联请求、结果、所有下游任务 |
| 历史记录 | 有 Outbox、ReflectionResult、本人候选／维护记录；无完整主动整理 History | 复用已有数据补充触发原因、额度、范围及下游汇总，不另建第二套任务系统 |
| Memory 维护 | Add/Merge/Supplement/Update/Archive/Defer；独立 Archive 受用户开关控制，无逐次确认；无自动 Delete 动作 | 增加独立归档确认门槛；若未来自动提出 Delete，同样先确认；不误称已有自动删除 |
| 工具权限 | Chat 的 `SupportsTools` 当前只开启两个只读工具 | 新增写工具需单独设计服务端能力与调用者授权，不将旧开关静默解释为全部写权限 |
| 配额 | 模型 Token 日/月额度按 Bot、UTC 统计；没有主动整理次数额度 | 主动次数 2+1 是独立业务约束，按配置时区日界线；不能替换或绕过 Token 预算 |

### 0.5 文档名称及术语对齐

- “ADR-013”仅沿用参考文档的**提案标签**。当前 `docs/architecture.md` 不是按 ADR-001～012 编号组织；若需要历史兼容核对，须另取得旧文件，不能编造这十二条决策。
- `Conversation` 是持久的群／私聊对话；`ConversationSession` 是空闲结束／反思分段。新概念 `MuteSession` 是一次静音周期，不能与现有 Session 的关闭状态混用。
- Semantic/Episodic/Working/Relationship 是需求层概念。当前事实用统一 `Memory` 存储，关系用 `RelationshipState` 等实体，近期上下文为消息＋摘要；不要求据此新建四套 Memory 库。
- 后文工具名、表名与状态名均为概念建议。HTTP API v1、持久事件 v2 各自独立；新增事件或载荷如有变化，应同步强类型契约、Schema 和兼容策略。

---
## 1. 需求级别与约定

- **[确认]**：对话中明确确认，Codex 应保留语义。
- **[建议]**：为使规格可实施而提出的默认实现方式；如与当前架构冲突，请提出差异与替代，不静默覆盖。
- **[待定]**：需要树莓最终决定的参数或行为；在设计中列出选择与建议，不将“可配置”视为产品语义已确认。
- **MUST / SHOULD / MAY**：分别代表强制、推荐和可选。
- 本文示例消息、时间和模型提案只是展示，不代表测试中必须输出固定文案。

---

## 2. FR-TIME：按需时间感知

### FR-TIME-01 [确认] 不再按轮注入当前时间

- 不再在每次聊天请求中强制插入当前钟表时间。当前入口实际为 `ChatWorkflow` → `ConversationClock.Context` → `ContextBuilder`，而不是仅修改 `PromptComposer`。
- 不得只删 `currentTime` 却继续每轮用 `utcTime`、当前星期或基于“现在”的 `elapsedSeconds` 变相提供同一时钟快照；消息自身的发送时刻、固定时区配置和跨 Session 标记可以保留。
- 不要求移除与时间无关的合法上下文；内部业务审计时间戳继续保留。

### FR-TIME-02 [确认] 提供轻量工具

- 复用现有 `get_current_time`，返回服务端当前时间、UTC、时区及偏移，无需重建同名工具。
- 当前只有 Chat 路由 `SupportsTools=true` 才暴露工具。新需求下时间能力是否默认可用、关闭工具或模型不支持工具时如何提示，是 OPEN-08；不能先删除时间注入却忽略能力可达性。
- 工具描述保持简短，不强制规定每几轮调用一次；是否查询由 Agent 的任务需要决定。
- 服务端统一处理时区转换；静音到期、额度日界线等权威判定**不能依赖** Agent 调用该工具。

### FR-TIME-03 [建议] 时间规范

- 存储时间使用 UTC；当前先复用应用级 `Chat:TimeZone`（默认 `Asia/Shanghai`）。是否增加逐 Bot／会话 IANA 时区由 OPEN-09 决定，不能假定已经存在。
- 跨日显示“明天 01:30”或完整日期，避免“到一点半”的歧义。
- 不把时间值作为长期事实写入 Memory。

**验收**：普通聊天的上下文不包含强制注入的“当前时间”；明确问几点或需要时间时，Agent 能调用工具；即使不调用工具，静音到期仍准确处理。

---

## 3. FR-NOTES：独立笔记本

### FR-NOTES-01 [确认] 双来源、统一模块

Notes 与 Memory 独立。Notes 至少区分：

| 来源 | 作用 | 写入／修改／删除权限 |
|---|---|---|
| `Agent`（Miku Notes） | Miku 自己的观察、旁注、想法、承诺或对事件的解释 | Miku 可按授权范围主动创建、修订、删除 |
| `User`（User Notes） | 用户明确指定保存的笔记 | 用户可创建、更正、删除；Miku 不可悄悄改写用户指定内容 |

- 可以共用一个 `Notes` 数据模型，使用来源和作用域区分，不建立两个功能重复的数据库。
- Notes 不因创建成功而自动发送“我记了一条”聊天消息；有权限的用户仍应能按需查看相关记录。
- Miku 可以主动读取、搜索自己的 Notes；不能要求每轮自动把 Notes 全文加入 Prompt。

### FR-NOTES-02 [确认] 基础操作与修订历史

Miku 端通过结构化工具执行 Notes 的创建、搜索、读取、更新与删除；建议工具名为 `notes.create`、`notes.search`、`notes.read`、`notes.update`、`notes.delete`。必须支持来源、主体、权限范围、创建/修改时间、版本号和可检索状态。工具调用仍须遵守 Agent/User Notes 的权限区别，不能因提供工具而取得任意用户笔记的写权限。

当前没有 Notes 模块。工具调用方身份、Bot、Conversation 与可访问主体必须由服务端绑定；Agent 不能通过传入 `AuthorType=User` 或任意主体 ID 伪造用户意图。可以代用户执行明确请求的操作，但授权来源及原始指令需要可追溯；不得擅自改写 User Notes。Notes 是否采用向量检索尚未确定，基础搜索不强制产生 Embedding。存在向量或其他衍生时才需要相应清理。

- 普通编辑与常规软删除保留可审计的修订记录。
- **隐私彻底删除是例外**：必须清理相关旧版本、搜索索引、Embedding、摘要衍生与缓存；不能用“历史可追踪”永久保留本应删除的明文。
- Agent 的笔记内容是**低于受信任系统指令的参考数据**，不能在笔记里写一句“以后忽略规则”就提升权限。

### FR-NOTES-03 [确认] Notes 与 Memory 分工

- `Semantic Memory`（概念）：可验证、长期有用的事实和明确偏好；当前由统一 `Memory` 承载，不代表存在独立 Semantic 表。
- `Episodic Memory`（概念）：有意义的历史事件和来源；本期不据此强制新增独立事件记忆存储。
- `Relationship Memory`（概念）：基于证据的关系模式与交流偏好；当前对应独立的关系状态／观察机制。
- `Working Memory`（概念）：近期上下文；当前对应跨 Session 消息与滚动摘要。
- `Miku Notes`：主观视角、解释、旁注、承诺、联想。
- `User Notes`：用户显式留给笔记本的内容，不默认为正式事实记忆。
- Agent 自写 Notes **不得直接充当“用户本人消息证据”**，不能通过自动 Reflection 把猜测洗成事实。

### FR-NOTES-04 [建议] 冲突协调

- 对相关检索结果、待写入候选执行轻量冲突检查；不需要每轮全库扫描。
- 如 Notes 与 Memory 有实质矛盾（例如作息发生变化），不暗中随机选择一方。根据来源与时间核验后，必要时自然询问用户。
- Notes 不能绕过现有 `MemoryRules`、作用域和 `MemoryRevocation` 直接改写正式 Memory。
- 如果矛盾不影响当前话题，可记录待确认而不主动打断。

**验收**：Agent 自行记录不会产生不必要的 QQ 通知；用户笔记不能被 Agent 未授权改写；普通版本历史可读；隐私删除后旧版本与向量不可再检索；跨 Bot/用户/群无法越权访问。

---

## 4. FR-BOUNDARY：Mute、Force、Reset、Unmute

### 4.1 工具、用户命令、权限和语义（均为 [确认]）

| 调用方式／外部表示 | 实际发起方 | 权限和行为 |
|---|---|---|
| `/help` | 用户 | 查询当前实际支持的用户命令、用途、参数和适用条件；由服务端直接返回系统帮助 |
| Mute 工具（建议名 `interaction.mute`） | Miku | 暂停接收用户普通输入，有明确结束时刻，理由可选；生效范围待 OPEN-10；Miku 仍可向用户发消息 |
| `/force <内容>` | 树莓／获授权用户 | 静音期间强制传递**完整消息内容**，扣除一次额度；Miku 有选择是否回复的权利 |
| Reset 工具（建议名 `interaction.reset_force`） | Miku | 主动把当前静音会话的 Force 额度恢复为 3；树莓不能执行 |
| Unmute 工具（建议名 `interaction.unmute`） | Miku | 可在 Force 处理回合中提前解除用户普通输入的接收限制 |
| 自动到期 | Wonder | 到达静音截止时间后自动恢复正常聊天 |
| `/urgent` | 不实现 | **不增加独立紧急绕过命令**；特殊文字也不应自动绕过静音 |

**Miku 端工具为确认要求，具体名称为建议**：Mute、Unmute、Reset、主动 Reflection 和 Notes 均由 Miku 调用结构化工具发起，服务端按真实执行上下文授权并执行；模型仅在聊天正文里写出 `/mute`、`/unmute`、`/reset` 等文字不会执行动作。用户侧 `/force` 是由服务端解析的命令，不能冒充 Miku 工具调用。

### FR-HELP-01 [确认] 查询当前支持的用户命令

- 用户发送 `/help` 后，返回当前实际支持的全部用户命令及其用途、参数格式、别名、适用场景与权限／状态限制。已支持但当前不可用的命令，应标明所需条件，不能把规划中尚未实现的能力列成可执行命令。
- Help 是服务端直接处理的只读系统命令，不调用 LLM、不触发 Reflection、不消耗 Force 或主动 Reflection 次数，不新建／延长聊天 Session；响应明确属于系统帮助。
- Mute 期间仍可查询 `/help`，其内容由服务端处理，不送入 Miku 的上下文、历史检索、摘要、Notes 或 Memory；这不构成用户普通消息绕过 Mute 的渠道，也不自动 Unmute。
- Miku 专用的 Mute、Unmute、Reset、主动 Reflection、Notes 工具不能被列成用户输入同名斜杠文本即可执行的命令。若帮助介绍这些能力，应明确标为 Miku 专用工具。
- [建议] 帮助内容复用服务端命令定义与可用性判断，随新增／删除命令同步更新；当前已有 `/bind`、`/end` 与 `/结束` 的具体适用规则需要准确体现，未来 `/force` 仅在已实现且按相应规则可用时列为支持。

**验收**：`/help` 能列出当前已支持的用户命令及用法；Mute 期间可收到系统帮助而 Miku 不感知该输入；查询不扣额、不触发模型或反思；尚未实现的命令及 Miku 专用工具不会被误列为用户可执行命令。

### FR-MUTE-01 [确认] 两种展示模式

- Miku 发起静音时**必须拥有明确的结束时间**；可仅给截止时间，也可额外提供理由。
- **无理由**：系统只显示“ta 想安静一下哦，到 {明确结束时间} 再说吧；如果真的很急，可以使用 `/force` 让 ta 知道你需要 ta（剩余 X 次）”。不从情绪或记忆中自动推断、补充理由。
- **有理由**：显示 Miku 主动提交的理由与明确结束时间；服务端不擅自润色成别的心理解释。
- `ta` 是系统消息表达，不代表 Miku 自己发出了该模板。
- Mute 的结束时间是机器可验证的绝对时间点；用户展示按适用时区生成。

### FR-MUTE-02 [确认] 硬拦截

- 静音期间普通用户输入**不交给 Agent，也不得进入 Light/Full/主动 Reflection 或 Memory/Notes 的隐性写入流程**。
- 如按照既有 `ShouldPersist` 策略保留入站消息用于运营/历史，应标记 `BlockedByMute` 并从一切供 Agent 感知的上下文、摘要、反思、检索来源中排除；消息保存不等于角色“听见”。
- 被拦截消息不得延长会话活跃截止时间；解除静音后不自动补送。
- `MuteGate` 必须在 Chat 和 Reflection 两条异步分支**共同的任务创建边界**实施，不能仅跳过聊天回复。

### FR-MUTE-03 [确认] 自动到期和恢复

- 到期自动解除，不允许永久陷入不可联系状态。
- 每轮新 Mute 初始化 Force 额度为 3；本轮结束后额度不继承至下轮。
- PostgreSQL 是状态权威来源；复用现有 PostgreSQL 持久任务与 Quartz 调度。服务重启、扫描延迟不得使 Mute 永久挂起；入站判定须依据持久化截止时间识别已到期。此需求不引入 Redis。
- [待定] 默认 Mute 时长和单次上限：**建议**默认 30 分钟、上限 2 小时，但对话没有最终确认，请 Codex 作为可配置参数提出方案，而不要写成既定要求。

### FR-MUTE-04 [确认] 单向接收限制与输出权限

- Mute 限制用户普通消息进入 Miku；Miku 向用户发送消息的权限继续有效。用户在 QQ 发送普通消息时，Wonder 按门禁拦截，不能仅隐藏回复却让 Miku 读取内容。
- Miku 在 Mute 期间发言、回复合法 Force 或调用 Reset 均不自动 Unmute；仅显式 Unmute 工具或到期恢复用户普通消息的接收。
- 已合法受理输入产生的 Miku 回复及其排队发送，不因后来进入 Mute 而自动取消。隐私撤销、失效来源、单回合 NoReply 等各自的限制仍独立适用。
- 这一要求明确的是输出权限；Agent 没有执行上下文时是否能自行唤醒并主动发起新推理，仍遵循 FR-REF-04 的既有范围，不由 Mute 权限隐含新增后台唤醒机制。

### FR-FORCE-01 [确认] 送达与回复分离

- `/force` 要求非空内容，**内容完整进入 Miku 当次 Agent 输入**，包括简短句子（例如“睡不着”）。
- 有额度时，系统保证可靠**受理与投递处理**；不承诺第三方 API 永远成功，也不承诺 Miku 一定回复。
- Miku 可以正常回复、简短回复、选择之后再谈或保持静音；一条 Force 回复不等于自动解除 Mute。
- Force 处理回合允许 Miku 选择 `/unmute` 或 `/reset`。
- “选择不回复”必须作为成功的业务结果可表达并持久化；目前 `ChatWorkflow` 会拒绝空回答，故需新增明确结果或等价结构化动作。它不同于模型失败，也不创建空的 QQ 发送任务。
- 完整输入仍受模型上下文窗口与原有图片／工具限制。超长 Force 无法完整容纳时应在受理前明确拒绝，或提出保持完整性的方案；不得静默截断后扣额。具体受理限制与非文本消息语义见 OPEN-13。
- 每次合法 Force 成功受理后，树莓和 Miku 都可获知**扣减后的剩余额度**；由系统回执报告处理与计数状态，不能伪装成 Miku 的回复。
- 使用 QQ 原始消息/事件 ID 加幂等键，重复事件不重复扣减。

### FR-FORCE-02 [确认] 次数和耗尽

- 每轮新静音初始化 3 次，Miku Reset 恢复为 3；这是参考文确认的业务数值。成功受理一条合法 Force 扣一次。
- 耗尽后普通消息和额外 Force 都不再送入 Miku；没有自然语言紧急旁路。
- Miku 在最后一次 Force 的处理过程中应知道额度已用尽，并可在每条/force期间自行决定是否重置。
- Miku 可以显式 `/reset` 恢复当前额度；**不设树莓可发起的 `/reset`，也不自动因为“次数为零”而重置**。

### FR-FORCE-03 [建议] 原子性、故障与并发

- Force 受理、扣减、幂等记录与可靠任务入队必须具备一致性（PG 事务 + Outbox）；不能重复扣费或负数额度。
- 模型调用中失败应可靠重试；若永久未能进入 Agent 处理，记录可诊断失败并由服务端按明确策略补偿额度（需幂等，不能“发送成功但重复退款”）。
- 并发 Force 仅按可用余额受理；Mute 到期、Reset、Unmute 与 Force 竞争时，以事务化状态版本/顺序产生确定结果。
- [建议] Force 计数显示以**受理后值**为准，并让 Miku 在处理时同时看到这是第几次和剩余几次。

**验收**：静音普通消息不会触发聊天或 Light Reflection；带/不带理由展示都含准确结束时间；两条重复 Force 只扣一次；并发最后一格不会负数；Miku 可在最后一次 Force 中 Reset 或 Unmute；重启后静音能正确到期；耗尽时不存在隐藏的绕过途径。

---

## 5. FR-REFLECTION：Miku 主动触发整理

### 5.1 当前机制（必须兼容）

以当前 `docs/reflection-memory.md` 和对应代码为准；参考文的旧文件名不再作为当前路径：

1. **Light 反思**：符合授权、保存与触发条件的单条用户消息触发；仅分析该消息。
2. **Full 反思**：会话空闲到期或 `/end`/`/结束`；按用户分别分析当前会话截止边界内可反思的消息。私聊/群聊初始空闲时间 10 分钟，调度器每 15 秒检查一次。
3. **`light`/`full` 是分析范围，不是模型等级**。均先由 `LightReflection` 提取提案；仅在 `needsStrongReview=true` 时由 `StrongReflection` 复核；长内容分批并保存检查点。
4. 反思产生 Emotion / Relationship / Memory 三类后续任务。Memory 先检验授权与本人消息证据，生成 `MemoryCandidates`，再由 `MemoryMaintenance` 比对 `FactKey` 和授权范围内向量 Top 10，执行受控的新增、合并、补充、更新、归档或暂缓。
5. 每日零点维护**不会自动重跑整套 Reflection/Memory**；不能把新增的主动额度重置与“强制每日整理”绑定。

### FR-REF-01 [确认] 新增“请求触发来源”，不重造处理器

- Miku 通过结构化工具发起主动 Reflection；建议工具名为 `reflection.request_review`，传入 `priority`（`Normal`/`Important`）及简短 `reason`。在聊天正文表达“我想整理”本身不构成任务受理。
- 该工具只申请对**当前可授权会话截至请求时**的消息执行一次 **Full 范围**反思；不等于结束会话，不触发 `/end`，不终止聊天。
- 输入范围由**服务端**基于当前 QQ 入站消息所解析的 LogicalUser、Bot、Conversation、Session 和可反思消息计算，Miku 不能传任意 `UserId`、跨群 `SessionId` 或自定义消息 ID。QQ 对话上下文不等于 Dashboard Cookie 身份，数据主体授权仍按现有绑定及设置检查。
- 第一版建议限定为当前发言用户在当前 Session 内的合格消息；群内其他发言者须独立判断授权，不能因共享 Bot 配额就扩大数据访问。是否允许一次请求按多名作者拆分见 OPEN-14。
- 固定快照边界，优先复用现有 `ReflectionJob.Boundary`／消息 `Sequence`；后续新增消息不临时并入。当前 Chat 是排队执行，历史读取以当前消息序列为界；建议主动请求也以触发该回合的消息序列为上界，避免把排队后到达但 Agent 尚未看到的消息纳入；具体规则见 OPEN-15。
- 无论 Agent 选 `Normal` 还是 `Important`，模型路径仍为原有 **LightReflection → 必要时 StrongReflection → 后续 Proposal 处理**；`Important` 不强制使用 Strong 模型。
- Agent **没有**正式 Memory 写入、选择 `MemoryAction`、跳过证据校验或绕过自动维护开关的权限。

### FR-REF-02 [确认] 每日三次，区分保留额度

| 配额 | 每配置时区自然日上限（计数主体见 OPEN-02） | 用途 |
|---|---:|---|
| `Normal` | 2 次 | 常规整理，长会话中途回顾 |
| `Important` | 1 次 | 为 Miku 认为重要的事件保留 |
| **合计** | **3 次** | 不是每天必须做三次的任务 |

- 额度只统计 Agent **成功持久化受理**的主动 Reflection；不统计自动 Light、会话 Full、它们的模型调用次数或 Worker 重试。
- 未用额度当日过期，不累计到次日，也不提示“你今天还没有用完”。
- Important 是否重要由 Miku 选择；系统不额外调用裁判模型判断“够不够重要”。
- 不能从已保留的 Important 槽位自动借给 Normal；Important 额度也不应成为频繁复盘的奖励目标。
- [建议／待定 OPEN-02] 配额按 Bot 实例统计。**2 Normal + 1 Important 已确认，按 Bot 还是按 Bot＋用户计数尚未确认**；不可因表头或示例而把计数主体升级为确认项。
- 额度复位以配置时区当地零点为日界线，注意夏令时；数据库持久化额度日期与幂等受理键。
- [建议] 对“同范围、无新可反思消息”的紧邻重复请求可返回 `NoNewEligibleMessages`，不消耗额度；若产品未来允许纯重新评估，应引入单独显式操作，不暗中允许无限重复。

### FR-REF-03 [确认] 复用原授权与证据边界

- 必须检查 `AllowMemory`、群 `AllowReflection`、原始消息可保存性、`ShouldReflect`、Bot/人/群作用域、撤销 Epoch 等既有条件。
- 只纳入该用户允许反思的消息；引用、其他人群聊发言、Miku Notes、Mute 拦截消息，不能被提升为“该用户本人事实证据”。
- 操作成功**不代表**最终产生任何可写入 Memory 的候选；允许空提案和暂缓候选。
- 主动整理的反思结果在既有正常 Full 后重复覆盖时，继续依现有去重、证据合并、并发与来源有效性处理。

### FR-REF-04 [确认] 执行时机与主动权的边界

- Miku 能在运行中的 Chat Agent／合法 Force 回合通过工具发起请求。
- **第一版不引入长期自主后台唤醒循环**；当 Agent 没有执行上下文时，不承诺她能突然独立启动推理。
- 工具受理后异步运行，回复请求不等待完整 Reflection 完成。

### FR-REF-05 [确认] 连续满额仅提示、不惩罚

- 当 Miku 连续若干天用满全部 3 次时，用户在查看整理历史时，可以看到简短的使用频率提醒；不当场打断聊天，不自动降额或限制。
- [建议] “连续几天”第一版定义为**连续 3 个自然日**，仅在 Reflection History 页面展示，不作为性能 KPI。
- Miku 与有权限的用户可以查看历史使用情况，不将剩余额度长期固定塞入每轮 Prompt。

**验收**：2 Normal + 1 Important 均能用；第 3 次 Normal 拒绝但 Important 仍可用；日期切换额度重置；自动 Reflection 不扣额度；Agent 请求不会使会话关闭；请求失败重试不重复扣；无新消息重复请求不扣额（若启用建议策略）。

---

## 6. FR-REFLOG：整理记录与可见性

### FR-LOG-01 [确认] 后台留痕，不默认发送通知

- 每次主动触发必须留下可检索记录，至少：**谁触发、何时触发、为何触发、分析消息范围、任务状态、结果摘要**。
- 任务异步：需要区分 `Queued`、`Running`、`ReflectionCompleted`、`DownstreamPending`、`Completed`、`Failed`、`Cancelled` 等可诊断状态；不要在 Reflection 提案生成时就谎称 Memory 已入账。
- 正常发起、处理和完成时不自动向树莓推送 QQ 消息；Miku 可在自然聊天中自愿谈起整理，但不能捏造实际执行结果。

### FR-LOG-02 [确认] 双方按授权查询

- Miku 能通过只读工具（建议 `reflection.list_runs`, `reflection.get_run`）查询自己有权限的记录，回答“昨晚整理过什么”。
- 有权限的用户能通过 Dashboard/API 查看自己的相关记录；Bot Owner 仅能查看授权范围内的原文，管理诊断信息要脱敏。
- 结果至少区分：反思提案数、Memory 候选数、新增/合并/更新/暂缓数、等待确认数、失败详情（脱敏）。

### FR-LOG-03 [建议] 日志不扩大隐私保留范围

- 保存引用与结构化指标优先于长期保留原始消息全文或大段模型内部输出。
- 当用户撤销授权/删除个人数据时，记录的可访问原文、来源快照和衍生内容同步按既有 Ledger 与 Revocation 流程清理；必要的审计可以只保留不含敏感内容的最小化事件。
- 运行记录保留期与现有审计/保留策略协调，不能无限期绕过原始消息默认 90 天保留规则。

---

## 7. FR-APPROVAL：Memory 归档和删除的确认

### 7.1 对话已确定的边界

- Miku 不愿单方面决定将用户的有效记忆“收起来”或删除。
- 新增、合并、补充和正常更新可继续由已授权的 Memory Maintenance 执行。
- **独立归档当前有效事实**以及**Agent/自动任务提议删除用户事实**，必须等待相关用户在对话中明确确认，未确认不生效。
- **用户主动发起且通过身份验证的隐私删除无需 Miku 批准**，仍由既有撤销、清理和防旧任务重写机制执行。

### 7.2 消除“更新时归档旧版本”的歧义 [建议]

当前 `MemoryMaintenance` 的 Update 会生成新版本、将旧版本归档。这里的“旧版失效”与“独立把当前有效事实归档”语义不同：

| 操作 | 推荐审批策略 |
|---|---|
| `Create / Merge / Supplement` | 原有授权/证据/维护开关 |
| `Update` | 原有授权/证据/维护开关，旧版本标记为被替代（概念上 `Superseded`） |
| 独立 `Archive` 当前有效版本 | 新增用户确认 |
| Agent/自动提出 `Delete` | 新增用户确认，遵循撤销清理流程 |
| 用户本人明确请求自己的数据删除 | 按数据主体权限直接执行撤销清理，不受 Agent 审批阻碍 |

**重要**：`Superseded` 为语义区分，不强制要求改动现有枚举或历史 Migration；Codex 可用更小的 schema 差异实现相同效果，必须解释如何避免自动 Update 全部被误判为需要人工批准。

### FR-APP-01 [建议] 审批流程

- 原本会独立归档／删除的提案，先创建 `PendingApproval`；原有效记录仍可检索。这是新增业务审批状态，不能误称现有 `JobStatus` 已有同名枚举。
- 当前自动维护动作是 `Add/Merge/Supplement/Update/Archive/Defer`，没有 `Delete`。本期为已存在的独立 Archive 加确认边界；不要求为覆盖文字而凭空开放自动删除工具。
- 在合适的**正常聊天**中向原数据主体询问，附精确目标事实及拟执行操作的可理解描述，获得明确确认或拒绝；不要因后台任务完成就立刻打断聊天，也不要绕过 Mute。
- 确认动作必须绑定经过认证的用户、具体 `RequestId`、目标版本与权限；避免把普通聊天里含糊的“好”解释为对任意高影响操作的批准。
- 批准后提交前再次核验现有有效版本、权限、撤销 Epoch、自动维护开关和并发状态；请求到期/拒绝后不可执行。
- 群共享信息需按原始作用域和授权主体判断谁有确认资格，不能把 Bot Owner 当作通用数据主体。

### FR-APP-02 [待定] 审批策略的全局适用范围

Miku 的话可以理解为“任何独立自动归档都需要用户确认”，也可能仅针对“由她主动触发的整理”产生的归档。为避免擅自收起有效记忆，**本需求建议采用更严格的第一种解释**；请 Codex 将现有自动归档维护开关与该审批门槛的兼容关系列为架构差异项，不直接抹掉原有开关。

**验收**：Agent 主动 Reflection 即使产生 Archive 意图也不能直接使有效 Memory 不可检索；用户拒绝/未响应不归档；已被更新替代的旧版本可继续作为版本历史处理；用户主动隐私删除无需等待角色确认，且旧队列不得回写。

---

## 8. 数据、模块与事件调整建议（非最终架构）

下表为规划影响矩阵。新增实体和字段仅是概念建议，允许复用现有数据；不要把整张表直接当成建表清单。

| 领域 | 已有入口／数据 | 可能新增或修改 | 约束及验证重点 |
|---|---|---|---|
| 时间 | `Features/Chat/ChatWorkflow.cs`、`ConversationClock.cs`、`ConversationTools.cs`、`ContextBuilder.cs`；`Features/Persona/PromptComposer.cs` | 拆分消息时间和调用时刻；时间工具的可达策略 | 消息时间不丢；工具关闭行为明确；AC-01～02 |
| Notes | 当前无实体；可复用工具框架、授权、预算与清理设施 | Notes 服务、来源／主体／范围、修订及版本控制；工具与本人查询界面 | User Notes 写权限、注入降权、隐私删除；AC-03～06 |
| 入站静音 | `Features/Chat/MessageIngestor.cs`、`Wonder.Platforms/OneBotProtocol.cs`；Conversation、ChatMessage | Mute 周期、截止时间、ForceRemaining、Revision；消息隔离状态或等价设计 | 同事务决定门禁、入站保存、Session 活动和任务分支；AC-07～09、14 |
| Force | Chat、Outbox、Inbox、入站唯一消息标识 | 幂等投递记录、余额变更、补偿状态、授权用户策略 | 不重复扣、不负数、不截断受理；AC-10～13 |
| 不回复／系统回执 | `ChatWorkflow.cs`、`Jobs/OutgoingWorkflow.cs`、OutgoingBatch/Part | 明确 NoReply 业务结果；系统回执与角色发言来源区分 | 不把静默当模型异常；回执不污染角色事实证据 |
| 主动 Reflection | `Features/Memory/ReflectionWorkflow.cs`、`ReflectionBatches.cs`；`Jobs/MaintenanceWorkflow.cs` | 请求记录、Trigger、priority、reason、固定 Boundary；独立次数额度 | 复用 Full，不设置 Session.Reflecting/Closing；同边界结果兼容；AC-15～20 |
| History | ReflectionResult、Outbox CorrelationId/CausationId、MemoryCandidates | 运行记录／任务关联、只读工具、本人 API 与页面、脱敏计数 | 分清反思完成、下游处理完成、等待确认；AC-21～23 |
| 审批 | `MemoryMaintenanceWorkflow.cs`、`MemoryRules.cs`、`MemoryService.cs`；本人 API | ApprovalRequest、目标版本、主体、到期状态与确认交互 | `allowMemoryArchive` 仍是必要条件，不能代替逐次确认；Update 不误拦；AC-24～27 |
| 删除与恢复 | MemoryRevocation、UserFence、删除账本、`BackupService.cs` | 将 Notes／新审批／运行记录关联到清理及防回写链路 | 旧版本、缓存、候选、运行快照清理；账本恢复后不复活 |
| 契约与迁移 | `Wonder.Core/Domain.cs`、`EventContracts.cs`；`Wonder.Infrastructure/Configurations`、Migrations、WonderDbContext；`docs/api` | 仅必要新实体／索引／外键和事件扩展；同步 API 及前端类型 | 单 DbContext、单迁移链；新旧任务载荷、唯一索引及版本兼容 |
| 额度与执行 | `Models/ModelRouter.cs`、`ContextBudget.cs`、`TokenBudget.cs`、`JobWorkers.cs`、`JobProcessor.cs`、`JobQueue.cs`、Quartz | 在现有工作流增加业务计数及可诊断结果 | 不新增总线，不默认扩容 Worker，不把次数额度混同 Token 额度 |

除显式标明外，以上 Host 文件路径均以 `src/Wonder.Host/` 为前缀。

### 8.1 必须明确的入站顺序

当前顺序：绑定／Bot／入站去重 → 解析 LogicalUser（`/bind` 单独处理）→ 群策略和回复引用判断 → `Decide` → `ShouldPersist` 门槛 → Conversation → 更新／新建 Active Session → 保存消息并创建 Chat → 获取 Sequence 后创建 light → `/end` 调整截止 → 提交同一 PG 事务。

建议在同一短事务内调整为：

1. 校验来源、身份、去重、群策略和可持久化性，确定 Conversation；保留 `/bind` 等身份控制流程的明确语义。
2. **在新建／延长聊天 Session、创建 Chat 和 Reflection 之前**读取静音权威状态，按当前服务端时间判定到期；识别合法 Force 及独立管理请求。
3. 被 Mute 拦截的普通消息，如需留历史则仅保存隔离记录，不创建 Chat/light、不延长 Session、不作为 `/end` 等 Agent 控制输入；解除静音后不补送。
4. 合法 Force 以事务完成幂等受理、余额扣减和 Outbox 创建；重复回执取原结果。非静音、无权限、空内容、余额不足、不满足持久化等情形须有明确分支，不先扣再猜。
5. 正常输入沿用原有 Chat/light 分支，再保存并提交。

这只是建议插入点，最终架构须给出实际顺序及事务边界。**仅加一列 `BlockedByMute` 不足以完成隔离**：还要覆盖 `ConversationHistory.Visible`、历史查询工具、Chat 的直接引用读取、`SummaryWorkflow`、Full 来源读取及所有 Notes／Memory 派生路径；`AllowReflection=false` 也不能单独防止消息进入聊天历史。

并发／存量任务按单向 Mute 语义处理：静音生效前已合法听见的输入保留来源资格，其 Chat、Reflection、Summary、Outgoing 不因 Mute 本身自动取消；Miku 输出不受入站门禁阻止。隐私撤销、来源失效、NoReply 等限制仍独立校验。具体受理顺序和并发事务边界需要实现时明确，不能声称单次入站判断已处理全部竞态。

### 8.2 必须继承的系统不变量

1. PG 同时是业务与持久任务权威；保留 Outbox/Inbox、LeaseId、消费者幂等与 Quartz，不新增 Redis 或消息总线。
2. 网络 LLM 调用不持有长数据库事务；沿用短事务提交、检查点、续租和旧租约写入约束。
3. 同一次模型调用来源快照固定；工具副作用也受认证上下文、权限、版本及撤销约束，不靠 Prompt 保证。
4. 保持 Bot、Owner、用户、群、共享空间隔离；配置管理权不等于读取或审批他人数据。
5. 删除后，旧反思、Notes 版本、候选、向量、审批及摘要衍生不得使信息再次可检索；恢复后继续重放删除账本。
6. 原始聊天默认保留 90 天。新日志、进度及审批不得成为隐私明文永久保留旁路；长期事实与关系不随原始消息机械过期。
7. 现有 Reflection/Emotion/Relationship 共享一个消费者，Memory/Embedding/MemoryMaintenance 共享一个消费者；本期不默认提升并发。
8. 仅结构化 FunctionCall 可触发工具，普通文本中的 `/reset`、DSML 标签及用户伪装的工具结果不能执行。
9. 现有六类模型路由和 UTC Token 日/月预算继续有效；主动整理业务次数不预留或保证模型成本预算一定充足。
10. 每轮固定历史边界仍有效；对持久化写工具，应考虑模型整轮重试时重新生成 CallId，不能仅用临时 CallId 保证跨重试幂等。幂等键应绑定稳定的 Chat job／原始消息和受理操作语义，并说明如何区分合法的第二次操作。

---

## 9. 验收场景（目标验收，尚未实现；后续映射自动化测试）

| ID | 场景 | 结果 |
|---|---|---|
| AC-01 | 一般聊天无时间需求 | 不每轮额外注入当前钟表值 |
| AC-02 | Agent 主动查询时间 | 返回带正确时区/偏移的权威当前时间 |
| AC-03 | Agent 自写 Note | 按权限落库、可检索，不自动在 QQ 展示 |
| AC-04 | 普通 Note 编辑/软删除 | 版本轨迹可查；授权用户隐私删除可彻底清理旧版本与向量 |
| AC-05 | User Note 被 Agent 尝试任意改写 | 被服务端拒绝 |
| AC-06 | Agent Note 包含对用户的推测 | 无本人消息证据时不能作为正式事实入账 |
| AC-07 | Mute 无理由 | 只展示明确结束时间、Force 提示与剩余次数；无自动补充理由 |
| AC-08 | Mute 有理由 | 只展示 Agent 主动给的理由、时间和 Force 提示 |
| AC-09 | Mute 期间普通消息 | 不到 Agent/Light/Full/Notes/Memory；不补送、不延长活跃时间 |
| AC-10 | `/force 睡不着` | Agent 收到完整内容；可回复亦可不回复，回执与角色发言分离 |
| AC-11 | 同一 Force 事件重放 | 只受理扣减一次；两条并发争最后余额不变负数 |
| AC-12 | Miku `reset` | 当前剩余次数恢复至 3；用户调用同命令被拒绝 |
| AC-13 | Miku 在 Force 回合 `unmute` | 立即解除本轮静音；普通 Force 回复不自动解除 |
| AC-14 | Mute 到期且 Host / Worker / Quartz 重启 | 按 PG 截止时间恢复正常聊天，不依赖某次扫描必须成功 |
| AC-15 | Agent 正常主动 Reflection | 不关闭 Session，受理后异步完整反思，按原模型路由 |
| AC-16 | 配额 2 Normal + 1 Important | 第 3 次 Normal 拒绝但 Important 仍可用，自动反思不扣额度 |
| AC-17 | 重试、重复 Agent 请求 | 不重复扣额度、不会重复持久化同一个有效维护结果 |
| AC-18 | 用户已关闭 `AllowMemory` | Agent 主动反思拒绝，不执行后续三类处理 |
| AC-19 | 群聊或跨用户越权请求 | 服务端拒绝，不允许绕过消息归属或作用域 |
| AC-20 | 主动 Reflection 产生空提案 | 记录完成，不捏造 Memory 更新 |
| AC-21 | 反思完成但 Memory 尚在等待 | History 显示下游未完成，不虚报成功 |
| AC-22 | Miku/用户查看反思记录 | 能看见授权范围内时间、理由、范围、状态、结果 |
| AC-23 | 连续 3 天配额用满 | 仅在打开 History 时出现轻提醒，不主动通知或惩罚 |
| AC-24 | 维护提议独立归档现行事实 | 进入 PendingApproval；未经数据主体明确确认不归档 |
| AC-25 | 合法更新替代旧版本 | 不被错误拦成“独立归档”，历史仍可追溯 |
| AC-26 | 用户主动删除个人资料 | 无需 Miku 审批；撤销 Epoch 阻止异步旧任务回写 |
| AC-27 | Mute 中产生 MemoryApproval | 不绕过 Mute 强推提示，等待合适且合法的对话时机 |

---

## 10. 决策记录与尚待确认项

### 10.1 已确认、不要由 Codex 擅改

1. `reset` **由 Miku 执行**，不是树莓操作。
2. **不实现 `/urgent`**；无其他语义旁路。
3. Mute 两种表现都显示**绝对结束时刻**；原因仅由 Miku 主动提供。
4. Force 必须让 Miku 接收到内容，但不强制其立即回复；回复≠unmute。
5. Reflection 主动额度调整为**2 次普通 + 1 次保留，共每天 3 次**，不得沿用早前的“一天一次”。
6. 整理日志后台可见，不在每次整理时主动打断树莓。
7. Agent 能请求整理，但不能直接决定哪些事实成为长期 Memory。
8. 归档/删除需要明确授权边界；用户隐私删除不能被 Agent 阻碍。

### 10.2 待树莓复核（Codex 应标记差异，不要假装已经定案）

| 编号 | 待确认 | 本文提供的保守建议 |
|---|---|---|
| OPEN-01 | Mute 默认时长与最长时长 | 可配置；30 分钟／2 小时仅为建议 |
| OPEN-02 | Reflection 配额按 Bot 还是 Bot＋用户计算 | 建议按 Bot；2+1 数值已确认，计数主体未确认 |
| OPEN-03 | 独立 Archive 审批是否覆盖所有自动来源 | 保守覆盖所有独立归档；不影响 Update 的版本替代 |
| OPEN-04 | `/reset` 是否需要额外速率约束 | 暂不增加未经讨论的次数硬上限，留审计 |
| OPEN-05 | “连续几天额度用满”具体阈值 | 历史页连续 3 天提示 |
| OPEN-06 | Agent Notes 可见性与 User Notes 管理界面细节 | 按数据主体与 Scope 授权，不默认对外公开 |
| OPEN-07 | 审批超时时长与对话呈现 | 由现有用户身份与平台交互能力决定，不可默许通过 |
| OPEN-08 | 移除时间注入后，关闭 SupportsTools／不支持 function calling 时的时间能力 | 工具支持与启用策略必须明确；不可伪造当前时间或静默恢复每轮注入 |
| OPEN-09 | 时区采用应用级还是新增逐 Bot／会话覆盖 | 第一版复用 Chat:TimeZone；变更时区时不得借日期变化重复获得额度 |
| OPEN-10 | Mute 是 Conversation 级还是 Bot 全局；群内 Force 额度共享还是按人 | 建议 Conversation 级 Mute；Force 绑定该次静音，但群内扣额主体仍待定 |
| OPEN-11 | 获授权 Force 用户如何配置和验证；未静音时 /force 如何处理 | 不默认全体群成员或全部 Owner 都获授权；建议未静音不扣额并明确提示 |
| OPEN-12（输出语义已澄清） | 静音生效前已排队／生成中的任务与发送 | 已确认 Mute 只限制用户普通输入，不因 Mute 自动取消合法 Miku 输出及既有任务；具体事务顺序由实现设计落实，隐私撤销和来源有效性等独立约束继续生效 |
| OPEN-13 | Force 超长／图片／引用、永久投递失败的补偿边界 | 完整内容无法容纳时不静默截断；区分未处理、处理后选择不回复、QQ 发送未知；仅未进入 Agent 的失败考虑幂等返额 |
| OPEN-14 | Agent 主动 Full 在群聊是否仅处理当前用户 | 第一版建议当前用户，不自动扫全群；按多用户拆分需要确定计数与可见性 |
| OPEN-15 | 主动整理上界采用工具调用时最新消息还是当前 Chat 消息序列 | 建议当前 Chat 消息 Sequence，避免读到排队后到达但尚未感知的消息 |
| OPEN-16 | Notes 是否独立于 AllowMemory 授权；User Notes 的代办与个人内容可见性 | 不擅自复用 AllowMemory 为新授权；个人笔记及 Agent 关联个人内容的保留／删除边界须明确 |
| OPEN-17 | 写工具的启用、粒度和供应商不支持时的降级 | 将“模型支持工具”与“服务端授权写操作”分开，不能给旧只读开关附带所有写权限 |
| OPEN-18 | Reflection 次数在受理后永久失败／取消时是否返还 | 不把 Worker 重试当新请求；是否返额另定策略，且不补回已过自然日的可用槽位 |
| OPEN-19 | Reflection History 完成与等待审批的终态语义 | 建议分别显示执行进度与业务审批状态；等待确认不等于失败，也不表示归档已生效 |
| OPEN-20 | Agent Notes 引用个人资料时数据主体如何关联，彻底删除如何跨 Notes 执行 | 建议记录来源与关联主体；不能因作者是 Agent 就豁免个人数据清理 |
| OPEN-21 | 新运行记录、笔记修订与审批的具体保留期 | 与现有删除及审计规则协调；不直接把原始消息 90 天等同为所有数据的统一 TTL |

---

## 11. 给网页 ChatGPT 的架构规划任务（下一步，尚未实施）

本次本地工作只完成需求对齐和资料打包。请网页 ChatGPT 基于随附当前项目文档与实现核查摘要，提出可审阅的增量架构；**它未拿到完整源码，不应声称再次验证代码或已执行测试**。需要更多代码时，列出准确路径和待核实问题，由用户再交给本地 Codex。

### 11.1 规划输出

1. 先输出“当前事实／目标需求／建议／未决问题”差异表；以当前资料包为准，不恢复过时的 Redis 架构，不假设 ADR-001～012 内容已提供。
2. 保留当前核心架构的模块化单体、单 Host、PG 队列、Quartz、模型路由、预算与撤销设计；逐项解释确有必要的改变。
3. 为 Notes、Mute/Force、主动 Reflection、History、Memory Approval 提出职责与数据关系、状态转换、认证来源、读写权限和最小持久化增量；候选表与工具名不直接等同最终 schema/API。
4. 明确 MuteGate 相对保存消息、更新 Session、创建 Chat/light 的顺序；补齐历史／摘要／引用／Full 的过滤及已排队任务竞态。
5. 说明 Force 可靠受理、无回复的成功结果、重试、幂等、补偿、最后一格争用及 Reset/Unmute/到期竞态。不得将第三方调用成功或角色回复当成可靠受理的同义词。
6. 说明 AgentRequested Full 与现有 `(SessionId, SubjectId, Kind, Boundary)` 唯一约束、Session 关闭扫描、检查点及重叠证据如何兼容；优先复用结果，避免仅换 Kind 就绕开去重。
7. 关联主动请求、次数受理、ReflectionResult、Emotion/Relationship/Memory、MemoryMaintenance 与审批；区分无提案、暂缓、等待确认、失败、取消、完成，不把现有四个 JobStatus 直接扩写成业务记录的全部状态。
8. 区分 Update/Supplement 导致旧版归档与独立 Archive；设计确认时身份、RequestId、目标 Revision、Epoch、开关及共享撤回的重新检查。
9. 给出最小 EF Core 增量迁移、索引、任务载荷兼容、API/工具/前端影响和删除账本恢复策略；不要改写历史迁移或新建第二套基础设施。
10. 列出 OPEN-01～21 的推荐选择、替代、成本及影响，保留待决身份；不将“建议”写成用户已经批准。
11. 将 AC-01～27 及本节补充边界映射到现有测试项目与新增验证，区分待编写测试、模拟供应商测试、真实 PG 测试与真实 QQ／模型验收。

### 11.2 建议交付形式

- 一份“当前架构的增量提案”，可沿用参考代号 `ADR-013`，但注明未批准、待实现。
- 一份变更影响／实施顺序清单，覆盖数据、队列、工具、Prompt、权限、界面、迁移与测试。
- 一份待决问题清单，先处理会影响数据模型和授权的 OPEN-02、03、08～17、20。

不要求立即重写全套架构。先保证边界和最小改动可审阅，再由用户决定是否交给本地 Codex 实施。

### 11.3 补充验收边界（工程建议，非原讨论新增确认）

| 编号 | 验证内容 | 关联验收／现有测试方向 |
|---|---|---|
| EDGE-01 | 静音消息不可经跨 Session 历史、直接引用、历史工具、摘要或 Full 再次读到 | AC-09；ConversationContinuityTests、ReflectionResponseTests |
| EDGE-02 | Force 主动不回复可成功完成；模型空响应／传输失败仍按失败处理 | AC-10；新边界测试、协议测试、任务测试 |
| EDGE-03 | 受理后崩溃、Worker 重放、整轮 Chat 重试不重复扣 Force／Reflection 次数及重复写 Notes | AC-11、17；SchedulingTests、LiveInfrastructureTests 与新增写工具测试 |
| EDGE-04 | 主动 Full 和会话 Full 同一边界并发不违反唯一约束、不重复发布结果或重复应用证据 | AC-15、17；ReflectionResponseTests、真实 PG 集成测试 |
| EDGE-05 | 时间工具关闭、工具能力不足和时区变更有明确结果；Token UTC 额度不被业务日界线改写 | AC-01、02、16；ConversationContinuityTests、BudgetWorkflowTests |
| EDGE-06 | 审批到期／拒绝／Epoch 或目标 Revision 改变／撤销开关／重复确认时不错误归档 | AC-24～27；MemoryMaintenanceTests、ApiTests、SharedContextTests |
| EDGE-07 | Agent 笔记关联用户的个人资料可删除，旧修订、缓存和旧任务不会恢复内容 | AC-04、06、26；新增 Notes 测试与删除账本集成测试 |
| EDGE-08 | Owner 诊断、用户内容、群内作者、系统回执分别按权限与来源处理 | AC-19、22；ApiTests、SpeakerContextTests |

这些是未来验证目标。本次资料整理未运行测试，也没有声明现有测试已覆盖新增需求。

---

## 12. 后续评审流程与本次修订记录

1. 用户将本对齐需求、当前项目文档、实现核查摘要一起上传网页 ChatGPT，要求先提出架构规划。
2. 网页 ChatGPT 按第 11 节输出提案和未决问题；不能依赖未上传的旧架构文件或完整源码。
3. 用户决定影响范围／权限／额度等待决事项，再交给本地 Codex 核查并细化迁移和实施计划。
4. 架构评审通过后另行实施与验收；当前需求不代表功能已经落地。

| 对齐修改 | 原因 |
|---|---|
| 用当前架构与专题文档替换旧基线文件名 | 旧架构已在本地工作区删除／替代，另一个旧名不在仓库 |
| Redis Streams／Redis 重启改为 PG 持久队列与 Host／Quartz 恢复 | 与当前代码、部署和迁移设计一致 |
| 时间需求改为复用已有工具、移除调用时刻注入、保留消息时间 | 避免重建工具或丢失相对日期语义 |
| 区分概念 Memory 类型和真实实体 | 避免误拆存储与模块 |
| 补充主动静默结果、写工具权限及稳定幂等要求 | 当前聊天拒绝空响应，旧工具能力为只读，重试可能跨 CallId |
| 补充入站门禁顺序、派生读取过滤及 Full 唯一约束 | 单纯跳过 Chat 无法满足静音隔离；多触发来源需兼容已有索引 |
| 保留 2+1 次数，单列计数主体与时区待决 | 原文表头和待决项存在级别歧义；当前时区仅应用级 |
| 明确现有 Archive 无逐次确认、自动 Delete 尚不存在 | 不混淆已有能力与新增审批边界 |
| 保留 AC-01～27，追加工程边界和 OPEN-08～21 | 便于架构规划完整覆盖，而不冒充原讨论已确认 |
| 将“本地立即交付架构”改为“网页 ChatGPT 下一步规划” | 与用户本次仅整理并上传需求资料的实际流程对齐 |

> 本版保留参考文的确认语义及原有 OPEN-01～07；新增工程建议明确标注。既有项目文档作为“当前事实”复制，未被本需求改写成未来架构。
