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

程序在首次使用时扫描此目录并加载清单；匹配模型名称时不区分大小写，目录名也可作为模型名称匹配。别名应保持唯一。

- `auto`：默认按模型名称匹配目录名或别名，未匹配时使用估算。
- `estimate`：明确使用 UTF-8 字节数估算。
- 其他 ID：明确使用对应子目录中的词表。

`auto` 和 `estimate` 是保留名称，不用于词表目录。模型配置页自动列出已发现的词表，无需编辑程序代码或配置文件中的路径、哈希。

本地开发直接使用 `Wonder.Host/Tokenizer`。构建与发布会把目录复制到输出中的 `Tokenizer`，容器内为 `/app/Tokenizer`
。新增或替换文件后重启应用；开发目录中的文件需要随新版发布到服务器。

已有模型路由保留原先选择；要启用自动匹配，在模型配置页将 Tokenizer 改为“自动匹配”。此次无需数据库迁移。
