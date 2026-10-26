# Tokenizer 目录

每个词表放在一个子目录中，子目录名就是 Tokenizer ID：

```text
Tokenizer/
  my-vocabulary/
    tokenizer.json
    models.json
```

`tokenizer.json` 使用 Hugging Face 格式。可选的 `models.json` 是模型别名数组：

```json
["my-model", "provider/my-model"]
```

TokenCounter 实例初始化时扫描目录和别名清单，词表在首次计数时按需加载并复用。模型名称匹配不区分大小写，目录名也可作为模型名称匹配。别名应保持唯一。

- `auto`：默认按模型名称匹配目录名或别名，未匹配时使用估算。
- `estimate`：明确使用 UTF-8 字节数估算。
- 其他 ID：明确使用对应子目录中的词表。

`auto` 和 `estimate` 是保留名称，不用于词表目录。模型配置页自动列出已发现的词表，无需编辑程序代码或配置文件中的路径、哈希。

本地开发使用 `src/Wonder.Host/Tokenizer`，运行时按 Host ContentRoot 定位。构建与发布会把目录复制到输出中的 `Tokenizer`
，容器内为 `/app/Tokenizer`。新增或替换文件后重启应用，词表文件需要随发布包部署。

每个模型路由分别保存选择。词表只计算文本，消息、工具和图片还有协议或估算开销，计数及额度规则见 [统一上下文预算](../../../docs/context-budget.md)。
