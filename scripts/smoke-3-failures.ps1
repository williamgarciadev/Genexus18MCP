param([string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-futures")

$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"
$env:GX_MCP_STDIO = "true"
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()

function Send-Rpc {
    param([hashtable]$body, [string]$label, [int]$timeoutSec=120)
    $proc.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                Set-Content -LiteralPath (Join-Path $LogDir "$label.json") -Value $line -Encoding UTF8
                Write-Host "<<< $label" -ForegroundColor Green
                Write-Host ("    isError=" + $obj.result.isError) -ForegroundColor Cyan
                if ($obj.result.content) {
                    $t = $obj.result.content[0].text
                    if ($t -and $t.Length -gt 500) { Write-Host "    text=$($t.Substring(0,500))..." } elseif ($t) { Write-Host "    text=$t" }
                }
                return $obj
            }
        }
    }
    Write-Host "!!! $label TIMEOUT" -ForegroundColor Red; return $null
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="diag";version="1"}}} "00-init" 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 2

    # Warm-up: prod the gateway to spawn+ready the worker for AcademicoHomolog1.
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{}}} "00-whoami" 90 | Out-Null
    Start-Sleep -Seconds 3

    Write-Host "`n=== TEST 1: AnalyzeExplain envelope shape ===" -ForegroundColor Yellow
    $list = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_list_objects";arguments=@{typeFilter="Procedure";limit=1}}} "diag-list-proc" 60
    $listText = $list.result.content[0].text | ConvertFrom-Json
    $proc1 = $listText.results[0].name
    Write-Host "First procedure: $proc1"
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_analyze";arguments=@{name=$proc1;mode="explain";code="for each`nendfor"}}} "diag-analyze-explain" 60 | Out-Null

    Write-Host "`n=== TEST 2: ApplyPattern on Procedure reject envelope ===" -ForegroundColor Yellow
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_apply_pattern";arguments=@{name=$proc1;pattern="WorkWithPlus"}}} "diag-apply-procreject" 30 | Out-Null

    Write-Host "`n=== TEST 3: create_object WebPanel envelope ===" -ForegroundColor Yellow
    $stamp = ([long](Get-Date -UFormat "%s")).ToString("X").Substring(0,6).ToLowerInvariant()
    $wp = "TestVldWp$stamp"
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_create_object";arguments=@{type="WebPanel";name=$wp}}} "diag-create-wp" 90 | Out-Null

    Write-Host "`n=== DIAG DONE — logs at $LogDir\diag-*.json ===" -ForegroundColor Cyan
} finally { if (-not $proc.HasExited) { $proc.Kill() } }
