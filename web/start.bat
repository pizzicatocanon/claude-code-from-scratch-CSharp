@echo off
REM ─────────────────────────────────────────
REM Mini Claude Code Web UI 启动脚本
REM ─────────────────────────────────────────

cd /d "%~dp0"

REM 检查是否已安装依赖
if not exist "node_modules" (
    echo [install] 正在安装依赖...
    call npm install
    if errorlevel 1 (
        echo [error] 安装失败！请确保已安装 Node.js
        pause
        exit /b 1
    )
)

REM 检查 TypeScript 是否已编译
if not exist "..\dist\agent.js" (
    echo [build] 正在编译 TypeScript...
    cd ..
    call npm run build
    cd web
    if errorlevel 1 (
        echo [error] TypeScript 编译失败！
        pause
        exit /b 1
    )
)

REM 设置环境变量 (可根据需要修改)
REM set OPENAI_API_KEY=
REM set OPENAI_API_BASE=https://ark.cn-beijing.volces.com/api/v3
REM set MINI_CLAUDE_MODEL=deepseek-v3-250324

echo.
echo  启动 Mini Claude Web UI...
echo.

node server.js
pause
