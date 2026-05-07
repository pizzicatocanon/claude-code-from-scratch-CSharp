# Mini Claude (.NET / C#)

C# 实现的 mini-claude-code,与 `src/`(TypeScript) 和 `python/`(Python) 功能等价。

## 项目结构(11 个模块,对齐 TS/Python 版)

| 文件 | 职责 |
|------|------|
| `Program.cs` | CLI 入口、参数解析、REPL、Ctrl+C 中断、`/cmd` 命令 |
| `Agent.cs` | Agent 主体 + 字段/构造函数/工具分派/Plan 模式/Sub-Agent |
| `AgentBackends.cs` | Anthropic / OpenAI 两套流式后端 + 摘要压缩 |
| `Tools.cs` | 13 个工具 + 5 模式权限系统 + mtime 防护 + 延迟激活 |
| `Memory.cs` | 4 类型记忆 + 语义召回 + 异步预取 |
| `Skills.cs` | `.claude/skills/*/SKILL.md` 加载 + inline/fork 双模式 |
| `SubAgent.cs` | 3 内置 + 自定义 Sub-Agent fork-return |
| `Mcp.cs` | JSON-RPC over stdio MCP 客户端 |
| `PromptBuilder.cs` | System Prompt + `@include` + `.claude/rules/` |
| `Ui.cs` | 终端彩色输出 + 工具图标 + Spinner |
| `Session.cs` | 会话持久化(`~/.mini-claude/sessions/`) |
| `Frontmatter.cs` | YAML frontmatter 解析器 |

## 依赖

- `Anthropic.SDK` 5.6.0 — Anthropic 官方社区 SDK
- `OpenAI` 2.1.0 — OpenAI 官方 .NET SDK
- `Microsoft.Extensions.FileSystemGlobbing` — Glob 匹配
- `Spectre.Console` — 彩色终端

## 构建 & 运行

```cmd
cd csharp
dotnet restore
dotnet build -c Release

# 运行(Anthropic)
set ANTHROPIC_API_KEY=sk-ant-xxx
dotnet run -c Release -- --help

# 运行(火山方舟 / OpenAI 兼容)
set OPENAI_API_KEY=ark-xxxx
set OPENAI_BASE_URL=https://ark.cn-beijing.volces.com/api/v3
set MINI_CLAUDE_MODEL=deepseek-v3-2-251201
dotnet run -c Release
```

## 已知差异(相对 TS 版的简化)

1. **Anthropic 流式工具早启**:依赖 `Anthropic.SDK` 暴露的 streamEvent 字段,不同 SDK 版本字段名可能略有差异。如果流式启动逻辑无效,会自动回退到"等响应完成再串行执行"模式。
2. **OpenAI 后端的对话恢复**:`--resume` 只完整恢复 Anthropic 后端的消息历史。OpenAI ChatMessage 类型不可序列化,简化后只保留元数据。
3. **OpenAI 后端的本地压缩 Tier 1-3**:`ChatMessage` 子类型不可变,仅保留 Tier 4(LLM 摘要)。Tier 1-3 仅 Anthropic 后端启用。其他场景下,工具结果在 `Tools.ExecuteAsync` 入口已经按 50000 char 截断。
4. **`run_shell` Windows 默认 PowerShell**,与 TS 版一致。
