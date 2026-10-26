# 发言者识别与共享功能退役

Identity 只描述 Bot 自身。当前消息携带 Conversation 范围内稳定的匿名作者标签，并按已绑定 LogicalUser 的 PreferredName 提供本人设置的称呼；未设置时使用自然的“你”，不从助手 Identity 或他人消息猜名字。没有自动采集 QQ 昵称或群名片。

群聊仅保留基础回复、当前发言者和称呼识别、@／回复／命令前缀触发。群消息可以按策略保存，但不会供后续模型读取；不读取历史、摘要、关系、Memory 或 Notes，不触发 Reflection。群策略不再含空间、Reflection 或主动调度字段。

Memory 只在同一 Bot 的本人私聊使用，以 BotId + SubjectId 表达所有权。已删除分享、分享选项、撤回和 ContextSpace API、前端残留及 UserShared 运行时例外。Scope、ScopeOwnerId、SharedFromMemoryId 已移除；SupersedesMemoryId 是私人事实的更新谱系，继续保留。

迁移先将旧群／共享记录及受影响的关联内容保存到独立只读 schema `retired_context.entries`，再隔离派生历史、摘要、Notes 和待发送结果，取消旧任务，移除在线群／共享记录及字段。只读触发器阻止修改归档；归档没有 DbSet、API 或模型工具入口，不会自动恢复为私人记忆。

`--export-retired-context` 将归档流式 age 加密导出到 Backups:RetiredContextPath，可另存异盘／异机。旧备份不删除；恢复时先迁移，再转换并核验最新独立材料，最后启动服务。旧共享事件转换为 MemoryContext 不能证明私人删除范围；范围未知时阻断恢复，不能扩大清理或删除同 FactKey 的私人事实。

普通 Memory 归档通过 ArchivedAt 表达事实退出日常使用，与功能退役的离线归档不同。Edit/Archive/Forget 保留原始消息和独立正文；只有本人确认的 Erase 清单可以处理完整私聊消息、附件及可证派生资源。版本族、源栅栏和逐项核验见 [Memory 生命周期](memory-lifecycle.md)。

Notes 保持独立授权。Memory 来源变化只隔离有明确依赖的 Notes，不顺带彻底删除独立笔记。Agent Notes 不直接向用户展示；User Notes 可由本人管理。

事务与恢复边界见 [实施附录](adr-013-implementation.md)、[运行指南](operations.md) 和 [清理方案](enum-and-compatibility-cleanup-plan.md)。
