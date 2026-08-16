param([string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-futures")

$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"; $env:GX_MCP_STDIO = "true"
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()
$proc.add_ErrorDataReceived({ param($s,$e) }); $proc.BeginErrorReadLine()

function Send-Rpc {
    param([hashtable]$body, [string]$label, [int]$timeoutSec=60)
    $proc.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                Set-Content -LiteralPath (Join-Path $LogDir "$label.json") -Value $line -Encoding UTF8
                return $obj
            }
        }
    }
    return $null
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="bench";version="1"}}} "00-init" 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 3
    # Warm: do a tiny call first
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{ }}} "warm" 180 | Out-Null

    # List Procedures — async; if it returns Running, poll lifecycle
    $listResp = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_list_objects";arguments=@{ typeFilter="Procedure"; limit=200 }}} "list-procs" 180
    $txt = $listResp.result.content[0].text
    if ($txt -match '"operationId":"([0-9a-f]+)"') {
        $opId = $matches[1]
        Write-Host "Operation $opId — polling lifecycle..."
        for ($i=0; $i -lt 30; $i++) {
            Start-Sleep -Seconds 10
            $statusResp = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_lifecycle";arguments=@{ action="result"; target=("op:"+$opId); wait=10 }}} ("poll-$i") 60
            $stxt = $statusResp.result.content[0].text
            Write-Host "poll $i first 160: $($stxt.Substring(0,[Math]::Min(160,$stxt.Length)))"
            if ($stxt -notmatch '"status":"Running"') {
                Set-Content -LiteralPath (Join-Path $LogDir "explore-procs.json") -Value $stxt -Encoding UTF8
                Write-Host "list-procs bytes: $($stxt.Length)"
                break
            }
        }
    } else {
        Set-Content -LiteralPath (Join-Path $LogDir "explore-procs.json") -Value $txt -Encoding UTF8
        Write-Host "list-procs bytes: $($txt.Length)"
    }
} finally { if (-not $proc.HasExited) { $proc.Kill() } }
