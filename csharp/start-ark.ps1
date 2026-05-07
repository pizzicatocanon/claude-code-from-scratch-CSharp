# 火山方舟 / DeepSeek 一键启动脚本
# 用法:
#   ./start-ark.ps1                    启动交互式 REPL
#   ./start-ark.ps1 "你的问题"          一次性提问(自动加 --yolo 跳过确认)
#   ./start-ark.ps1 --plan             用 plan 模式启动 REPL
#   ./start-ark.ps1 --help             查看所有 mini-claude 选项

$env:OPENAI_API_KEY = ""  #这里填写你的api
$env:OPENAI_BASE_URL = "https://ark.cn-beijing.volces.com/api/v3"
$env:MINI_CLAUDE_MODEL = "deepseek-v3-2-251201"

# 强制 PowerShell 控制台用 UTF-8(避免中文乱码)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# 切到本脚本所在目录(支持任意位置调用)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# 启动:把所有传入参数原样转发
$dll = Join-Path $scriptDir "bin\Release\net8.0\mini-claude.dll"
if (-not (Test-Path $dll)) {
    Write-Host "未找到编译产物,正在构建..." -ForegroundColor Yellow
    dotnet build -c Release | Out-Host
}

# 如果第一个参数不是以 -- 开头(纯文本提问),自动加 --yolo 方便测试
if ($args.Count -gt 0 -and -not $args[0].StartsWith("--") -and -not $args[0].StartsWith("-")) {
    dotnet $dll --yolo @args
} else {
    dotnet $dll @args
}
