# 测试指南

三个项目均使用 TUnit 与原生异步断言，global.json 选择 Microsoft.Testing.Platform。

## 执行

在项目根目录运行：

~~~bash
dotnet test --solution Wonder.slnx --configuration Release
dotnet test --project tests/Wonder.IntegrationTests/Wonder.IntegrationTests.csproj
~~~

参数化测试使用 Test + Arguments，异步断言必须 await。TUnit 默认并行，每个 HTTP 测试独立创建 TestApp 和 SQLite 数据库。报告输出到 TestResults。

## 覆盖范围

| 项目 | 核心覆盖 |
| --- | --- |
| `Wonder.UnitTests` | 领域规则、提示编译、情绪限幅与记忆作用域 |
| `Wonder.ContractTests` | 兼容模型协议、文本 / 图片、JSON 与思考参数、最终回答及用量解析 |
| `Wonder.IntegrationTests` | HTTP 认证与资源授权、聊天工作流、记忆与共享、持久任务和真实基础设施 |

集成测试重点验证：

- 登录与 Owner 隔离、本人记忆权限、输入校验和密钥脱敏。
- 入站去重、排队消息上下文顺序、完整生成与不确定发送处理。
- 发言者归属、用户称呼、历史与引用及滚动摘要。
- 逐条共享、跨 Bot 隔离、空间成员资格、撤回后派生历史 / 摘要失效和待发送回复取消。
- 删除防回写、损坏来源缓存的保守失效、删除账本幂等重放。
- 记忆候选隔离、向量召回与作用域、自动合并 / 补充 / 更新 / 归档、用户维护边界、版本链删除、并发重判与 Profile 回填。
- 模型路由批量与单任务更新、独立 Embedding 路由、空回答与截断反思的拒绝。
- 查询的稳定排序，以及维护清理对待处理发送与反思的保留。

## 真实 PostgreSQL / pgvector

设置 WONDER_TEST_POSTGRES 后运行集成测试；未设置时仅跳过该基础设施用例。测试创建 / 删除随机临时数据库，PostgreSQL 账号需要 CREATEDB 权限，实例须支持 pgvector。使用专用测试服务。

该用例覆盖完整迁移、旧 real[] 向量和旧账号密码的升级兼容、共享空间授权下的向量查询与距离排序、作者关闭共享后的检索、真实 pgvector 上的自主维护更新与版本链删除，以及并发领取、任务顺序、租约续期与恢复、旧租约写入拦截、重试退避和死信。CI 配置对应基础设施服务。

## 契约与验收边界

运行测试时可通过 WONDER_EXPORT_OPENAPI 指定 OpenAPI 导出路径，用于同步 [API 契约](../docs/api/openapi.v1.json)。

日常测试通过模拟供应商和 QQ 发送适配器验证链路。真实 NapCat 的回执与断线行为、模型供应商的 JSON / 图片 / 用量兼容性、容器部署、备份恢复与目标机器负载仍需分别验收。

参考：[TUnit 安装说明](https://tunit.dev/docs/getting-started/installation/)、[运行测试](https://tunit.dev/docs/getting-started/running-your-tests/)。
