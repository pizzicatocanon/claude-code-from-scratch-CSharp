# ─────────────────────────────────────────
# Mini Claude Code Web UI 启动脚本 (PowerShell)
# 使用火山引擎 DeepSeek API
# ─────────────────────────────────────────

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 检查依赖
if (!(Test-Path "node_modules")) {
    Write-Host "[install] 正在安装依赖..." -ForegroundColor Cyan
    npm install
}

# 检查 TypeScript 编译
if (!(Test-Path "../dist/agent.js")) {
    Write-Host "[build] 正在编译 TypeScript..." -ForegroundColor Cyan
    Push-Location ..
    npm run build
    Pop-Location
}

# ─── 环境变量设置 ───
# 火山引擎 DeepSeek (取消注释以使用)
# $env:OPENAI_API_KEY = ""
# $env:OPENAI_API_BASE = "https://ark.cn-beijing.volces.com/api/v3"
# $env:MINI_CLAUDE_MODEL = "deepseek-v3-250324"

Write-Host ""
Write-Host "  启动 Mini Claude Web UI..." -ForegroundColor Green
Write-Host ""

node server.js
