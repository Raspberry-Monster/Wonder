# 文档索引

文档按当前实现组织，主要面向个人自部署和少量可信用户。首次启动、构建和容器运行从[项目 README](../README.md)开始；准备在内部环境使用时，按[内部自部署验收](operations.md#内部自部署验收)检查实际链路。

## 部署与配置

| 文档 | 内容 |
| --- | --- |
| [运行指南](operations.md) | 配置、数据库基线初始化、密钥排错、任务重试、外部数据库备份与恢复 |
| [Quadlet 部署](../deploy/quadlet/README.md) | Podman、持久目录、Milky 连接和 PostgreSQL 管理 |
| [模型路由](model-routes.md) | 五类任务、批量应用、Tokenizer 和模型参数 |
| [DeepSeek 示例](deepseek.md) | 可选供应商的配置示例 |

## 功能与架构

| 文档 | 内容 |
| --- | --- |
| [核心架构](architecture.md) | 模块职责、数据流、任务队列和实现边界 |
| [对话延续与时间](conversation-continuity.md) | 发言者、私聊与群聊范围、历史、摘要和只读工具 |
| [交互工具与 Notes](agent-tools.md) | 用户命令、静音与 Force、主动整理、笔记授权和幂等 |
| [Reflection 与 Memory](reflection-memory.md) | 即时反思、会话结束反思、提案与下游任务 |
| [自主维护记忆](memory-maintenance.md) | 自动新增、合并、补充、更新、归档和用户偏好 |
| [Memory 生命周期](memory-lifecycle.md) | 本人纠正、归档、Forget、Erase、来源校验和在线清理 |
| [上下文与 Token 预算](context-budget.md) | 输入裁剪、后台分批、用量结算和额度 |

## 接口与验证

- [API 说明](api/README.md)：HTTP 接口及契约生成方式。
- [OpenAPI](api/openapi.v1.json)、[Outbox 事件 Schema](api/outbox.v2.schema.json)：生成的机器可读契约。
- [模型路由示例](examples/model-routes.json)、[DeepSeek 路由示例](examples/deepseek-flash-routes.json)：使用前替换地址、模型和密钥。
- [测试指南](../tests/README.md)：后端、前端和隔离 PostgreSQL 测试。

功能规则放在对应专题，运行命令与恢复步骤以运行指南为准；测试执行方式集中在测试指南。

当前数据库使用唯一 `InitialCreate` 基线，旧迁移链的开发库需要手动重建；初始化与同基线更新的区别见 [运行指南](operations.md#数据库基线与首次初始化)。Quartz 内存触发器和持久 Outbox 的职责见 [调度器与重启恢复](operations.md#调度器与重启恢复)。
