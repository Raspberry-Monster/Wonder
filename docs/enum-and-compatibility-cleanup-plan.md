# Enum 与群聊／共享记忆兼容清理方案

日期：2026-10-25。审阅基线：`68195c7`。类型改造和群／共享运行时清理已落地到工作区，新增前向迁移与测试；运行库尚未迁移或部署。下文保留检查依据，并以实际落地说明覆盖早期建议。

## 1. 结论与范围

1. 封闭的业务类别、状态和操作改成 C# Enum；取消业务代码中的裸字符串和魔法数字判断。
2. 移除群聊高级上下文和全部共享记忆的运行时兼容，包括目前仍保留的私人来源 `UserShared`、`SpaceShared`、ContextSpace API 和前端残留。保留基础群聊回复、触发策略、当前发言者和称呼识别。
3. Reflection 本轮保留旧 Full 接管和旧结果防重；有效来源去重、授权复查和一次性下游提交也保留。前两者有实际旧任务／备份语义，旧结果没有可核验来源签名，不能直接猜测迁成规范运行。
4. 旧备份的删除账本转换放到独立离线升级／恢复步骤，不能通过删除在线兼容代码让已删除内容在恢复后重新可用。

本次用户“移除群聊／共享记忆兼容代码”的要求覆盖原实施附录中“保留清理兼容”的范围。这里的群聊移除指高级上下文能力，基础聊天仍保留；不把群聊事实迁成私人事实，不销毁旧备份。

后续明确授权：旧群／共享记录及迁移受影响的关联内容完整归档后退出在线表。实际采用独立只读 `retired_context.entries`，无 DbSet／业务读取入口；`--export-retired-context` 支持流式 age 加密导出到独立目录。迁移在同一事务内先复制完整行，再失效派生内容、取消旧任务并移除在线数据与字段。独立 Notes 和私人事实不批量清除。

落地迁移依次为 TypedBusinessTags、ArchiveRetiredContext、PrivateMemoryOnly；历史迁移不修改。删除账本的旧作用域只存在于离线转换器：私人事件转换为 Memory，旧群／共享事件转换为 MemoryContext（推进栅栏并清理派生上下文，不匹配私人事实）。Note 的真实修订号保留，重复编码资源类型的 RecordVersion 已移除。

## 2. Enum 方案

### 2.1 原则

Enum 解决的是业务代码的类型约束。数据库和 JSON 最终仍要存为整数或字符串，这本身没有问题；问题是代码可以任意写入、拼接和比较未经约束的标签。

采用最小必要改造：现有整数枚举固定显式值；当前字符串列先保留原存储值，通过显式 EF 映射转换为领域 Enum，并加数据库 CHECK。不为“统一成数字”重写全部表，也不同时引入 PostgreSQL 原生枚举。新枚举成员使用显式编号，编号一经持久化不重排、不复用。

### 2.2 已定位的改造点

| 当前字段／代码 | 建议类型或处理 | 关键约束 |
|---|---|---|
| `Note.AuthorKind` | `NoteAuthorKind`：Agent / User | Agent 工具不能伪造 User 身份 |
| `Note.ClaimKind` | `NoteClaimKind?`：Observation / Guess / Uncertain | 保留现有小写协议值及 null 语义 |
| `NoteDependency.TargetKind` | `NoteDependencyTargetKind`：Chat / Message / Summary / Note | 依赖隔离流程只接受定义的目标类别 |
| `AgentReflectionRequest.Priority` | `ReflectionPriority`：Normal / Important | 不改变 Bot 共用的 2+1 配额 |
| `ReflectionJob.Kind`、`ReflectionResult.Kind` | `ReflectionKind`：Light / Full | 保留 `light`、`full` 的载荷值及结果唯一键 |
| `ReflectionGeneration.Status` | `ReflectionGenerationStatus` | Queued / Running / Completed / Cancelled |
| `ReflectionGeneration.CancellationReason` | `ReflectionCancellationReason?` | 保留 null；原因类别与自由说明分开 |
| `ReflectionRunHistory` 的展示状态 | `ReflectionRunDisplayStatus` | 与队列状态、MemoryMaintenance 状态分别表达 |
| `ReflectionRuns.AdmitAsync` 返回的状态 | `ReflectionAdmissionOutcome` | Accepted、AlreadyScheduled 等变为类型化受理结果 |
| `ForceReceipt.CompensationStatus` | `ForceCompensationOutcome?` | null 表示尚未判定；保留 Reset 代际规则 |
| `AgentOperation.Action` | `AgentAction` | 工具注册表显式映射函数名、动作和能力，不靠名称前缀授权 |
| `MemoryRevocation.ResourceKind` | `DeletionResourceKind`：Memory / Note | 与格式版本、Note 修订号区分 |
| `OutboxMessage.Type`、固定消费者标识 | `JobKind` 及显式标识映射 | 保留 `memory-maintenance` 等现有名称和 Inbox 去重身份 |
| `ChatMessage.Role`、上下文内部角色 | `MessageRole` | 在模型边界映射 SDK 的 ChatRole |
| `DeliveryResolution.Action` | `DeliveryResolutionAction` | ConfirmSent / Retry；不允许任意字符串进入控制流 |
| 工具结果中的封闭状态、来源类型 | 类型化结果 DTO + 对应 Enum | 结果正文、错误说明仍为字符串，不使用一个包罗所有模块的大枚举 |
| `ContextBuilder` 的 40/60/70/90/100 | `ContextItemKind` + 集中排序权重 | 当前数字同时承担历史类别识别和排序，拆开这两个职责；保留预算与裁剪顺序 |

现有 `TaskKind` 是六类模型路由，`JobKind` 是后台任务类别，两者保持独立。已有 ConversationKind、SessionStatus、MemoryStatus、JobStatus、DeliveryStatus、MemoryMaintenanceAction/Status 等无需重新发明，只补显式值和边界验证。

`ChatDisposition.Outcome` 目前只有 `NoReply` 一个值：建议移除冗余常量列，由终态记录本身表达 NoReply。不要为凑枚举添加当前没有持久化的“已回复”状态。

### 2.3 不应改成 Enum 的内容

- `Epoch`、`ResetGeneration`、`Sequence`、Note 的 `Version`、Revision、次数、Token 数和实际排序权重都是数值数据。
- FactKey、正文、原因正文、模型名称、URL、时区和哈希都是数据或外部标识。
- 模型 `ReasoningEffort` 当前按供应商开放参数处理，不能自行收紧为固定 low/medium/high。Platform、AdapterType 等可扩展标识通过注册表校验，不因只有一个当前实现就枚举化。
- `RecordVersion=1/2` 当前把 Memory/Note 类型与账本版本混用了。新内部记录只用 `DeletionResourceKind` 表达资源类型；旧格式由离线导入器明确转换。若外部文件确需格式版本，只表示文件结构，不再表示产品的“Memory v1 / Note v2”。

### 2.4 序列化与迁移合同

- API、工具及前端使用稳定的可读字符串值；通过显式映射保留目前大小写和连字符。前端类型从 OpenAPI 同步为受约束的枚举值集合，展示名称独立于协议值。
- 关闭枚举 JSON 的整数输入，拒绝未定义的文本和值；不能将未知值静默转为默认成员。现有 `JsonStringEnumConverter()` 默认允许数字，需要按边界收紧，并审核旧持久载荷是否使用数字。
- 保持工具函数名称、参数规范化、ArgumentsHash、来源签名输入和 Inbox 标识不变。类型改造不能使同一已提交操作变成 OperationConflict，或使旧任务再次被消费。
- 迁移前扫描所有 distinct 标签。无法映射的值列入报告并阻止相关迁移；不得兜底写成 Normal、Private 或 Completed。
- EF 配置用命名枚举成员生成约束和过滤条件。历史迁移保留原数字常量；不编辑已应用迁移，不重排已有整数值。

## 3. 群聊／共享记忆清理

### 3.1 删除的运行时入口

| 层次 | 具体范围 |
|---|---|
| API／合同 | ContextSpacesController；分享选项、分享、撤回接口；ShareMemoryInput、ContextSpaceInput；设置中的 AllowCrossGroupMemory；群策略中的 ContextSpaceId、旧 Reflection 控制字段 |
| 业务 | MemoryService.ShareAsync/WithdrawAsync 和共享空间解析；MemoryQueries 中 UserShared/SpaceShared 与旧群来源分支；候选维护、Embedding、关系更新中的群作用域和共享副本分支 |
| 上下文 | 历史、摘要、出站源验证中的旧群／共享来源兼容查询；先完成旧派生内容隔离，再删除这些查询。群聊继续在入口明确禁止个人记忆及历史工具，不能把访问门禁当作兼容代码删除 |
| 前端 | PersonalPage 中 sharing 状态、share 函数、共享副本列表、共享弹窗及请求；遗留共享设置与类型。保留基本群策略配置 |
| 数据模型 | ContextSpace 表／DbSet／FK；共享副本 SharedFromMemoryId；无用途的群关系和反思 GroupId 字段；Memory、候选上的冗余 Scope/ScopeOwnerId |

移除共享后，Memory 以 BotId + SubjectId 表达私人所有权，不保留只有 Private 一个成员的 MemoryScope。SupersedesMemoryId 是私人记忆的更新谱系，必须保留；不能误当成共享关系删除。

群聊仍需要 ConversationKind.Group、平台群号、基本 GroupPolicy、触发前缀、Mention／ReplyReference 和 SpeakerTags／PreferredName。保留这些不代表继续支持群记忆。

### 3.2 旧数据与事务边界

采用一次维护窗口切换：停止 Worker／入站 → 备份并导出最新独立删除账本 → 重新清点任务和来源 → 转换／隔离数据 → 前向 schema 迁移 → 重放账本并验证 → 启动新版本。不运行混合版本 Worker。

先使旧群／共享来源及其派生历史、摘要、Notes 依赖和待发送结果不可用，才删除共享引用、列和表。仅依赖失效不等于彻底删除所有独立 Notes；本人主动清除 Notes 的入口和范围不变。已在发送或回执未知的内容沿现有 DeliveryUnknown 规则处理。

旧的非私人记录从在线业务表退出；不把它们改写成 Private。迁移需要的旧作用域／父子引用保留在离线转换材料中，供恢复旧备份时重放删除。新服务只启动在已完成转换的 schema 上；旧格式备份通过离线工具升级，不在正常请求路径永久携带兼容分支。

保留旧备份不表示保留可绕过撤销的读取路径。删除账本需要独立可取得；不能只存进同一个可能被回滚的数据库。私人 Memory 删除继续覆盖更新谱系并清除向量／已知派生内容，不借清理之机缩小隐私删除范围。

## 4. Reflection 检查与判断

### 4.1 本地只读清点

检查对象是本地 `appsettings.Local.json` 指向的 PostgreSQL，采用只读事务，只读取数量与状态。以下是本次检查时的快照，不代表所有部署与备份。

| 项目 | 检查结果 |
|---|---|
| 最后已应用迁移 | `20261023145954_RemoveUnusedModelProvider`，尚无 ReflectionGenerations 表 |
| 旧 ReflectionResult | 私聊 light 602 / full 51；群聊 light 11 / full 3 |
| Reflection 任务 | Completed 667，DeadLetter 3；没有 Pending / Running |
| Session | 56，均为 Closed |
| Memory | Private Active 23、Revoked 43；UserShared Revoked 2；无有效群／共享 Memory |
| ContextSpace／群关系 | 均为 0 |
| 共享删除账本 | 仍有 2 条，不能随共享模型删除而丢失 |

3 条旧 Reflection 死信分别来自模型响应、加密、模型配置错误；当前均已 Epoch 失配、有效来源为 0、没有结果。推荐在切换时标记为取消／退役，拒绝人工重试，不再调用模型，也不伪造成功结果。

队列现有 JobStatus 只有 Pending=0、Running=1、Completed=2、DeadLetter=3。建议追加 Cancelled=4，作为不可重试的真实终态，并更新队列、Session 收尾、诊断和前端判断。当前 RetryJob 允许所有 DeadLetter 返回 Pending，单纯“留下旧死信不管”不足以防止再次执行。

### 4.2 分支判定

| 分支／机制 | 判断 |
|---|---|
| ReflectionRuns.AdmitAsync 的 `legacy` 参数、旧 Full 执行时临时创建 Generation | 真正的旧任务接管兼容。清空／退役旧任务并验证后移除；新 Full 必须在受理事务中同时创建规范运行和 Outbox |
| 旧 Full 结果同 Boundary 检查、`legacyBoundary` 检查 | 旧结果防重兼容。本批暂留；51 条私人旧 Full 没有可核验的历史签名，尚不能证明直接删除这些检查不会重复处理 |
| 旧群结果、群下游和共享 Embedding 分支 | 随群／共享迁移退休。保留私聊限定的访问校验，防止异常输入重新开启群分析 |
| 有效本人来源签名和唯一约束 | 当前业务可靠性，保留；用户 100 与助手 101 不应生成两次 Full |
| Epoch／来源复查、Lease、取消、模型进度恢复 | 当前授权和任务恢复机制，保留 |
| Result + Emotion/Relationship/Memory 三个下游原子提交、Inbox 去重 | 当前事务与幂等机制，保留 |
| 六类模型路由、统一预算、先 Light 后必要时 Strong | 当前架构，保留 |

不使用当前消息重新计算签名并冒充 51 条旧结果的原始来源签名；原消息可能已经修订、撤销或清除。旧提案中的 Evidence 也不能证明当时完整输入集合。

旧结果防重分支的最终退出条件：历史 Session 不再进入新的受理路径；旧任务不能从诊断重试；旧结果与当前规范结果的唯一键冲突已有明确处理；恢复旧备份能得到同样保证。证明并测试这些条件后，再迁出防重分支，而非本轮直接删掉。

## 5. 实施批次与验收

### A. 类型收紧

建立领域 Enum、协议映射及类型化 DTO；固定现有编号；转换业务比较、工具注册和前端类型；增加 distinct 值检查和 CHECK。这批不改变授权、保留期和 Reflection／Notes 的业务语义。

验收：枚举读写及数据库 round-trip；未知字符串／数字输入拒绝；历史载荷和幂等哈希稳定；ContextBuilder 预算及裁剪顺序不变；NoReply 重试零额外模型调用；Reset 跨代不返额。

### B. 群／共享清理与切换

实现独立旧数据转换与恢复步骤，再删除 API、UI、业务分支和冗余模型。前向迁移完成后更新架构与恢复文档；保留历史迁移文件。基础群聊与称呼识别作为明确保留项验证。

验收：真实 PostgreSQL 升级和约束；旧共享接口不可用；群聊不读取 Memory、Notes、历史、关系和摘要；私人更正／版本谱系删除不回归；出站前撤销隔离；恢复旧库并重放账本后共享副本和旧派生内容不能复活。不得用 SQLite 替代并发与迁移验证。

### C. Reflection 兼容退出

本轮已有 Cancelled 队列终态；退役群／共享来源的相关任务取消后不能从 DeadLetter 重试入口复活，工作流直接调用也不再执行。保留 Full 的旧任务接管和旧结果防重；本地查到的 3 条私人旧死信未在运行库中变更，原有执行前授权／来源检查继续阻止无效来源调用模型。后续满足上述退出条件，再独立处理私人 Reflection 兼容退出。

后续退出所需验收（本轮未实施私人旧死信批处理）：3 类无效旧死信取消且模型调用为 0；旧结果不重复下游应用；自动／主动 Full 并发仍只有一个规范生成；授权撤销与来源改变取消；模型失败重试原任务不重复扣每日次数；恢复后不绕过去重。前端分别验证运行阶段、Job 终态和记忆维护状态。

本轮验证记录见下方最终验收。真实 PostgreSQL 迁移在随机临时库执行，未对运行库执行迁移、归档或任务取消。源码运行环境尚无 age 可执行程序，因此独立加密导出未做真实 age 往返；已验证导出所用 SQL 的强类型映射。归档中的媒体仅保存元数据，媒体文件仍按原运维备份流程另行保存。

## 6. 最终验收与交付状态

| 检查 | 结果 |
|---|---|
| .NET 解决方案构建 | 通过，零警告 |
| EF 模型与迁移快照一致性 | 无待生成模型变更 |
| 单元测试 | 30 通过 |
| 契约测试 | 15 通过 |
| 集成测试（启用真实 PostgreSQL） | 112 通过，0 跳过 |
| 前端类型检查、生产构建 | 通过；构建有既有的大 chunk 提示 |
| 前端单元测试 | 2 通过 |
| 生产构建浏览器／开发模式浏览器 | 5／1 通过 |
| 离线转换 CLI | 合成旧共享账本转换为 MemoryContext，原文件保留 |
| git diff --check | 通过 |

合计 165 项测试。最新报告分别在 `TestResults/cleanup-unit`、`cleanup-contract`、`cleanup-all-pass`；前端沿 Playwright／Vitest 既有结果目录。PG 用例限制为 6 个独立用例并行，避免本地管理连接池耗尽；用例内部的真实并发不降低。

归档测试验证完整原文、相关元数据及只读保护；缺失来源的旧回复／摘要隔离，已证明无关的私人缓存和独立 Notes 保留；取消任务不调用模型、不被队列重新领取。旧账本转换与重放验证退役共享事件不会删除同 FactKey 的私人事实；未知旧标签导致迁移回滚，不能静默丢弃。

并发回归暴露了原有预算事务紧循环重试的争用，已增加短暂随机退避及有限重试；日／月额度、预留和结算规则不变。三个 Agent 能力开关仍默认关闭。本批按独立 Commit 交付，未迁移、重启或部署运行服务。
