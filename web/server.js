/**
 * Web 后端服务器 — Express + SSE
 * 直接复用 src/ 编译后的 Agent 模块，暴露 /api/chat 端点。
 * 通过 SSE 推送流式文本和工具调用事件给前端。
 */

import express from "express";
import cors from "cors";
import { fileURLToPath } from "url";
import { dirname, join, resolve } from "path";
import { Agent } from "../dist/agent.js";
import { buildSystemPrompt, loadClaudeMd, getGitContext } from "../dist/prompt.js";
import { buildMemoryPromptSection } from "../dist/memory.js";
import { buildSkillDescriptions } from "../dist/skills.js";
import { buildAgentDescriptions } from "../dist/subagent.js";
import { getActiveToolDefinitions, getDeferredToolNames } from "../dist/tools.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const app = express();
app.use(cors());
app.use(express.json());

// 静态文件：前端 HTML/CSS/JS
app.use(express.static(join(__dirname, "public")));

// ─── Agent 实例管理 ────────────────────────────

/** 每个 session 一个 Agent 实例 */
const sessions = new Map();

function getOrCreateAgent(sessionId, options = {}) {
  if (sessions.has(sessionId)) return sessions.get(sessionId);

  // 确定 API 配置 (优先使用请求中的设置，其次环境变量)
  // 注意：空字符串必须视为无效，转为 undefined
  let apiBase = (options.apiBase && options.apiBase.trim()) || (process.env.OPENAI_API_BASE && process.env.OPENAI_API_BASE.trim()) || undefined;
  const apiKey = (options.apiKey && options.apiKey.trim()) || (process.env.OPENAI_API_KEY || process.env.ANTHROPIC_API_KEY) || undefined;
  const model = (options.model && options.model.trim()) || process.env.MINI_CLAUDE_MODEL || (apiBase ? "deepseek-v3-250324" : "claude-sonnet-4-20250514");

  // URL 格式修正: 确保有协议前缀
  if (apiBase && !apiBase.startsWith("http")) {
    apiBase = "https://" + apiBase;
  }
  // 移除末尾多余的斜杠
  if (apiBase) {
    apiBase = apiBase.replace(/\/+$/, "");
  }

  console.log(`[session] Creating agent: model=${model}, apiBase=${apiBase ? apiBase : "(anthropic)"}`);

  const agentOpts = {
    permissionMode: "bypassPermissions", // Web 模式默认跳过确认
    model,
    apiBase,                             // undefined → Anthropic, 有值 → OpenAI 兼容
    apiKey,
    thinking: options.thinking || false,
  };

  const agent = new Agent(agentOpts);
  sessions.set(sessionId, agent);
  return agent;
}

// ─── 拦截 Agent 输出 ──────────────────────────

/**
 * 猴子补丁(monkey-patch): 拦截 Agent 内部的 console 输出和 ui.js 函数。
 * 因为 Agent 内部直接调用 printAssistantText 等函数，我们需要拦截这些输出。
 * 
 * 方案: 重写 Agent.chat() 的行为，捕获输出通过事件发给 SSE。
 * 实际上 Agent 已有 outputBuffer (runOnce 模式)，我们可以利用类似机制。
 * 
 * 最简方案: 使用 Agent.runOnce() 获取完整文本输出(非流式但最可靠)
 */

// ─── API 路由 ──────────────────────────────────

/**
 * POST /api/chat
 * Body: { sessionId, message, options? }
 * Response: SSE stream with events: text, tool_call, tool_result, done, error
 */
app.post("/api/chat", async (req, res) => {
  const { sessionId = "default", message, options = {} } = req.body;

  if (!message || typeof message !== "string") {
    return res.status(400).json({ error: "message is required" });
  }

  // SSE headers
  res.setHeader("Content-Type", "text/event-stream");
  res.setHeader("Cache-Control", "no-cache");
  res.setHeader("Connection", "keep-alive");
  res.setHeader("X-Accel-Buffering", "no");
  res.flushHeaders();

  const sendEvent = (event, data) => {
    res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
  };

  try {
    const agent = getOrCreateAgent(sessionId, options);

    // 用 runOnce() 模式获取完整输出
    // 同时我们拦截 console.error 来捕获工具调用信息
    const toolCalls = [];
    const originalStderr = process.stderr.write.bind(process.stderr);
    
    // 拦截 stderr (工具调用日志会输出到这里)
    let stderrBuffer = "";
    process.stderr.write = (chunk, ...args) => {
      const str = typeof chunk === "string" ? chunk : chunk.toString();
      stderrBuffer += str;
      
      // 尝试解析工具调用信息
      const toolMatch = str.match(/\[tool\] (\w+)/);
      if (toolMatch) {
        sendEvent("tool_call", { name: toolMatch[1], status: "running" });
      }
      const resultMatch = str.match(/\[result\] (.+)/);
      if (resultMatch) {
        sendEvent("tool_result", { summary: resultMatch[1] });
      }
      
      return originalStderr(chunk, ...args);
    };

    // 发送"正在思考"事件
    sendEvent("status", { text: "thinking..." });

    const result = await agent.runOnce(message);

    // 恢复 stderr
    process.stderr.write = originalStderr;

    // 发送文本结果
    if (result.text) {
      sendEvent("text", { content: result.text });
    }

    // 发送 token 使用信息
    sendEvent("usage", {
      input: result.tokens.input,
      output: result.tokens.output,
    });

    sendEvent("done", {});
  } catch (err) {
    sendEvent("error", { message: err.message || "Unknown error" });
  } finally {
    res.end();
  }
});

/**
 * POST /api/clear
 * Body: { sessionId }
 * 清除对话历史
 */
app.post("/api/clear", (req, res) => {
  const { sessionId = "default" } = req.body;
  if (sessions.has(sessionId)) {
    const agent = sessions.get(sessionId);
    agent.clearHistory();
  }
  res.json({ ok: true });
});

/**
 * DELETE /api/session/:id
 * 删除 session
 */
app.delete("/api/session/:id", (req, res) => {
  sessions.delete(req.params.id);
  res.json({ ok: true });
});

/**
 * GET /api/status
 * 服务器状态
 */
app.get("/api/status", (req, res) => {
  const apiBase = process.env.OPENAI_API_BASE;
  const defaultModel = apiBase
    ? (process.env.MINI_CLAUDE_MODEL || "deepseek-v3-250324")
    : (process.env.MINI_CLAUDE_MODEL || "claude-sonnet-4-20250514");
  res.json({
    status: "ok",
    sessions: sessions.size,
    model: defaultModel,
    apiBase: apiBase || null,
    hasApiKey: !!(process.env.OPENAI_API_KEY || process.env.ANTHROPIC_API_KEY),
  });
});

/**
 * GET /api/prompt-debug
 * 返回 System Prompt 各模块的拼装过程
 */
app.get("/api/prompt-debug", async (req, res) => {
  try {
    const cwd = process.cwd();
    const date = new Date().toISOString().split("T")[0];
    const platform = `${process.platform} ${process.arch}`;
    const shell = process.platform === "win32"
      ? (process.env.ComSpec || "cmd.exe")
      : (process.env.SHELL || "/bin/sh");

    // 分别获取各模块内容
    let gitContext = "";
    try { gitContext = getGitContext(); } catch (e) { gitContext = `[error: ${e.message}]`; }

    let claudeMd = "";
    try { claudeMd = loadClaudeMd(); } catch (e) { claudeMd = `[error: ${e.message}]`; }

    let memorySection = "";
    try { memorySection = buildMemoryPromptSection(); } catch (e) { memorySection = `[error: ${e.message}]`; }

    let skillsSection = "";
    try { skillsSection = buildSkillDescriptions(); } catch (e) { skillsSection = `[error: ${e.message}]`; }

    let agentSection = "";
    try { agentSection = buildAgentDescriptions(); } catch (e) { agentSection = `[error: ${e.message}]`; }

    let deferredTools = "";
    try {
      const names = getDeferredToolNames();
      deferredTools = names.length > 0
        ? `The following deferred tools are available via tool_search: ${names.join(", ")}.`
        : "(none)";
    } catch (e) { deferredTools = `[error: ${e.message}]`; }

    let toolList = [];
    try {
      toolList = getActiveToolDefinitions().map(t => ({ name: t.name, description: t.description }));
    } catch (e) { toolList = [{ name: "error", description: e.message }]; }

    // 完整的最终 prompt
    let fullPrompt = "";
    try { fullPrompt = buildSystemPrompt(); } catch (e) { fullPrompt = `[error: ${e.message}]`; }

    res.json({
      modules: {
        environment: {
          label: "环境信息",
          content: `Working directory: ${cwd}\nDate: ${date}\nPlatform: ${platform}\nShell: ${shell}`,
        },
        git_context: {
          label: "Git 上下文",
          content: gitContext || "(无 Git 仓库)",
        },
        claude_md: {
          label: "CLAUDE.md 项目指令",
          content: claudeMd || "(未找到 CLAUDE.md)",
        },
        memory: {
          label: "记忆系统",
          content: memorySection || "(无记忆配置)",
        },
        skills: {
          label: "技能描述",
          content: skillsSection || "(无自定义技能)",
        },
        agents: {
          label: "子 Agent 描述",
          content: agentSection || "(无自定义 Agent)",
        },
        deferred_tools: {
          label: "延迟工具",
          content: deferredTools,
        },
        tool_list: {
          label: "活跃工具列表",
          content: toolList.map(t => `- ${t.name}: ${t.description}`).join("\n"),
        },
      },
      full_prompt: fullPrompt,
      full_prompt_length: fullPrompt.length,
      estimated_tokens: Math.ceil(fullPrompt.length / 4),
    });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ─── 启动服务器 ────────────────────────────────

const PORT = process.env.PORT || 3456;
app.listen(PORT, () => {
  console.log(`\n  Mini Claude Web UI`);
  console.log(`  ─────────────────────────────`);
  console.log(`  Local:   http://localhost:${PORT}`);
  console.log(`  Model:   ${process.env.MINI_CLAUDE_MODEL || "claude-sonnet-4-20250514"}`);
  console.log(`  API Key: ${(process.env.OPENAI_API_KEY || process.env.ANTHROPIC_API_KEY) ? "configured" : "NOT SET"}`);
  console.log(`  ─────────────────────────────\n`);
});
