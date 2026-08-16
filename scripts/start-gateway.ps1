# GeneXus MCP Gateway Startup Script
# This script starts the gateway in a persistent way, ideal for HTTP/SSE multi-session usage.

$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Executable = Join-Path $ProjectRoot "publish\GxMcp18.Gateway.exe"

if (-not (Test-Path $Executable)) {
    Write-Error "Gateway executable not found at $Executable. Please run .\build.ps1 first."
    exit 1
}

Write-Host "Starting GeneXus MCP Gateway in Background Server Mode..." -ForegroundColor Cyan
Write-Host "Clients can connect to http://localhost:5000/mcp" -ForegroundColor Green

# Use Start-Process to keep it running even if this shell closes
Start-Process -FilePath $Executable -ArgumentList "--http" -NoNewWindow
