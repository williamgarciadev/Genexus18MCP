param(
  [string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-logs"
)

$ErrorActionPreference = "Stop"
$root = "C:\Projetos\Genexus18MCP"
$gateway = Join-Path $root "publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = Join-Path $root "config.json"
$env:GX_MCP_STDIO = "true"

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

# Spawn gateway as a process with redirected stdio
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$null = $proc.Start()

# Async stderr drain (gateway logs go here)
$stderrSb = New-Object System.Text.StringBuilder
$proc.add_ErrorDataReceived({
    param($s, $e)
    if ($e.Data) { [void]$stderrSb.AppendLine($e.Data) }
})
$proc.BeginErrorReadLine()

function Send-Rpc {
    param([hashtable]$body, [string]$label, [int]$timeoutSec = 60)
    $payload = $body | ConvertTo-Json -Depth 20 -Compress
    Write-Host ">>> $label" -ForegroundColor Cyan
    $proc.StandardInput.WriteLine($payload)
    $proc.StandardInput.Flush()

    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                $outPath = Join-Path $LogDir "$label.json"
                Set-Content -LiteralPath $outPath -Value $line -Encoding UTF8
                Write-Host "<<< $label -> $outPath ($($line.Length) bytes)" -ForegroundColor Green
                return $obj
            }
        }
    }
    Write-Host "!!! $label TIMEOUT after $timeoutSec sec" -ForegroundColor Red
    return $null
}

$rpcId = 0
function Next-Id { $script:rpcId += 1; return $script:rpcId }

try {
    # 1. initialize
    Send-Rpc @{
        jsonrpc = "2.0"; id = (Next-Id); method = "initialize"
        params = @{
            protocolVersion = "2024-11-05"
            capabilities = @{}
            clientInfo = @{ name = "smoke-live.ps1"; version = "1.0" }
        }
    } "01-initialize" 30 | Out-Null

    # initialized notification (no id, no response)
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress))
    $proc.StandardInput.Flush()
    Start-Sleep -Seconds 1

    # 2. tools/list
    $listResp = Send-Rpc @{ jsonrpc="2.0"; id=(Next-Id); method="tools/list" } "02-tools-list" 30
    $toolCount = if ($listResp) { $listResp.result.tools.Count } else { 0 }
    Write-Host "    tools/list returned $toolCount tools" -ForegroundColor Yellow

    # 3. Round-1 smoke calls (read-only, no KB-side effects)
    $smokeCalls = @(
        @{ label="03-whoami"; name="genexus_whoami"; args=@{} },
        @{ label="04-orient"; name="genexus_orient"; args=@{} },
        @{ label="05-recipe-list"; name="genexus_recipe"; args=@{ name="list" } },
        @{ label="06-list-objects"; name="genexus_list_objects"; args=@{ limit=5; projection="minimal" } },
        @{ label="07-query"; name="genexus_query"; args=@{ query="type:Procedure"; limit=3 } },
        @{ label="08-kb-list"; name="genexus_kb"; args=@{ action="list" } },
        @{ label="09-kb-get-startup"; name="genexus_kb"; args=@{ action="get_startup" } },
        @{ label="10-future-stub"; name="genexus_what_if"; args=@{ change=@{kind="test"} } },
        @{ label="11-future-stub-2"; name="genexus_kb_diff"; args=@{ kbA="a"; kbB="b" } }
    )

    foreach ($c in $smokeCalls) {
        Send-Rpc @{
            jsonrpc="2.0"; id=(Next-Id); method="tools/call"
            params = @{ name=$c.name; arguments=$c.args }
        } $c.label 60 | Out-Null
    }

    Write-Host "`n=== SMOKE COMPLETE ===" -ForegroundColor Cyan
    Write-Host "Logs at: $LogDir"

} finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    $stderrPath = Join-Path $LogDir "gateway.stderr.log"
    Set-Content -LiteralPath $stderrPath -Value $stderrSb.ToString() -Encoding UTF8
    Write-Host "Gateway stderr: $stderrPath"
}
