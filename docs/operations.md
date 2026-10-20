# 运行、备份与恢复

## 首次部署

按 README 执行 Compose 构建和 --migrate，再启动 Host。迁移不会在普通启动时自动执行。PostgreSQL 实例必须能够安装 vector 扩展；首次迁移的数据库账号需要相应权限。

初始先在控制台配置 Chat 路由，再配置其余后台任务模型；未配置的模型任务会进入失败列表。补齐配置后可以在“状态与任务”重试。消息生成和发送均有持久状态；Redis 丢失时待处理 Outbox 在一分钟内重新发布，运行中的任务会等租约到期后恢复。

仅同一个 Host 进程负责此数据库的消费。不要同时启动两份应用来共享当前任务库。同一 QQ 账号只允许绑定一个逻辑 Bot。

Podman / systemd 的等价部署方式见 [Quadlet 指南](../deploy/quadlet/README.md)。两种部署方式选择其一。

## 手动备份

应用镜像内包含 PostgreSQL 17 客户端和 age。应用外使用 age-keygen 生成密钥；仅将公开 age1... 收件人填入 AGE_RECIPIENT / Backups:AgeRecipient，私钥放在异机。

将系统管理员账号 UUID 写入 SYSTEM_ADMIN_ID / SystemAdminIds。登录后 POST /api/v1/system/backups，携带 X-Wonder: 1。接口执行 pg_dump 并直接流式 age 加密，不写明文数据库转储，同时在独立目录输出加密撤销账本。

删除账本默认位于 data/deletion-ledger。Compose 为它设置独立卷，但默认仍在同一物理主机；请将这个卷挂到独立磁盘或同步到异机。启用收件人后，每次删除会排入账本导出任务；可以 POST /api/v1/system/deletion-ledger 手动再导出。关注导出任务失败和积压。

数据库备份之外还需要独立加密保存 data/keys。丢失 Data Protection 密钥会导致现有模型 Key、QQ 令牌和登录 Cookie 无法解密。恢复后可以重新输入模型 Key / QQ 令牌，但不能从密文反推出它们。

## 恢复流程

1. 停止 Host，保留最新可用的删除账本与 Data Protection 密钥。不要启动 Gateway 或消费者。
2. 使用 age 私钥解密数据库备份，在空的 PostgreSQL + pgvector 数据库中用 pg_restore 恢复。
3. 还原 Data Protection 密钥目录，并使用 --migrate 应用当前迁移。
4. 将最新的独立删除账本解密到临时私有文件，再运行下面的命令。此模式不会启动 HTTP、Gateway 或消费者。

   ~~~bash
   dotnet Wonder.Host.dll --replay-ledger /private/latest-ledger.json
   ~~~

5. 验证已撤销记忆、向量与派生内容不可检索。账本重放可重复执行；临时解密文件不应放进静态目录或版本库。
6. 使用专属、空的 Redis 实例 / 逻辑数据库，启动 Host。未完成的 Outbox 会重新发布；过期运行租约会恢复。外部发送为 Sending 的分段会转 DeliveryUnknown，先到 QQ 核对再人工确认或允许重发。
7. 检查 /health/ready、任务列表和本人记忆接口，再恢复外部流量。

不要重放旧账本来代替最新删除记录。恢复点之后新增的删除，只有在独立账本成功保存且仍可取得时才能重放；数据库和账本一起丢失时没有相应保证。RPO 取决于最近一次成功备份。

## 发送与保留

不确定发送先在控制台处理分段，再重试对应 outgoing 任务。确认已发送可补充平台消息 ID；允许重发可能重复。不要直接把 DeliveryUnknown 当成普通网络错误清空重试。

原始消息默认 90 天，按创建时配置保存 ExpiresAt；后台分批清理。图片缓存七天。已确认完成的发送正文缓存三十天清理。Memory / Relationship 不随原始消息过期；删除记忆将清理其保守关联的派生上下文。

控制台展示任务类型、状态、错误类型、重试次数、用量和模拟情绪版本；普通日志不写入聊天正文、Prompt、反思结果或密钥。

## 查看任务详细日志

任务失败、Outbox 发布、Redis 消费和维护扫描的 Warning 日志包含异常消息与堆栈，默认 Information 级别即可看到。数据库任务列表仍只记录错误类型。

- dotnet run / IDE：查看运行终端或调试输出。
- Podman Quadlet：journalctl --user -u wonder-app.service -n 200 -f。
- Compose（项目根目录）：docker compose --env-file deploy/.env -f deploy/compose.yaml logs --tail 200 -f app。

按任务 UUID 查找对应失败记录。ModelConfigurationException 的消息可区分缺少模型路由、Token 预算耗尽和供应商拒绝请求的 HTTP 状态码；供应商错误响应正文目前没有写入异常。修正配置后，在控制台“状态与任务”重试。

Redis 消费遇到 NOGROUP 时会重新初始化 Consumer Group；并发创建产生的 BUSYGROUP 可正常处理。

反思生成遇到空最终回答或截断的结构化回答时，记录 ModelResponseException 并沿用任务重试，不把空内容当成有效提案。日志包含 Task、Model、FinishReason、MaxOutputTokens、OutputTokens 和 ThinkingEnabled；失败生成也记录用量。FinishReason=length 时应增加输出上限或减少输入 / 思考消耗，再到任务列表重试。请按模型能力为反思路由开启 JSON 输出格式。

## 更新应用

停止当前 Host（包括 IDE 调试进程），执行 README 或对应容器指南中的 --migrate，再启动新版应用。不要在旧版本仍运行时切换数据库结构。

用户称呼、共享空间与记忆授权的操作见 [跨群记忆与用户称呼](shared-context.md)。核心状态、权限和派生上下文处理见 [核心架构](Wonder-Architecture-v0.1.md)。
