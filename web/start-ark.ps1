# ─────────────────────────────────────────
# 火山引擎 DeepSeek API 启动脚本
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

# ─── 火山引擎 API 配置 ───
$env:OPENAI_API_KEY = ""  # 替换为你的 key
$env:OPENAI_API_BASE = "https://ark.cn-beijing.volces.com/api/v3"
$env:MINI_CLAUDE_MODEL = "deepseek-v3-250324"
$env:PORT = "3456"

Write-Host ""
Write-Host "  Mini Claude Web UI (DeepSeek via Volcengine)" -ForegroundColor Green
Write-Host "  Model: $env:MINI_CLAUDE_MODEL" -ForegroundColor DarkGray
Write-Host "  URL:   http://localhost:$env:PORT" -ForegroundColor DarkGray
Write-Host ""

node server.js
