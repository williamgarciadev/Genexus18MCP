param([string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-final")

$ErrorActionPreference = "Stop"
$root = "C:\Projetos\Genexus18MCP"
$gateway = Join-Path $root "publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = Join-Path $root "config.json"; $env:GX_MCP_STDIO = "true"

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()
$proc.add_ErrorDataReceived({ param($s,$e) }); $proc.BeginErrorReadLine()

function Send-Rpc {
    param([hashtable]$body, [string]$label, [int]$timeoutSec=120)
    $proc.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush()
    Write-Host ">>> $label" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                Set-Content -LiteralPath (Join-Path $LogDir "$label.json") -Value $line -Encoding UTF8
                $t = if($obj.result.content -and $obj.result.content[0].text) { $obj.result.content[0].text } else { "<no text>" }
                $brief = if($t.Length -gt 220) { $t.Substring(0,220)+"..." } else { $t }
                Write-Host "<<< $label ($($line.Length)b) :: $brief" -ForegroundColor Green; return $obj
            }
        }
    }
    Write-Host "!!! $label TIMEOUT" -ForegroundColor Red; return $null
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="final";version="1"}}} "01-init" 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 2

    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{}}} "10-whoami-version" 30 | Out-Null
    Start-Sleep -Seconds 2

    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_save_as";arguments=@{name="dani";newName="dani_smk";dryRun=$true}}} "11-save-as-dryrun" 60 | Out-Null
    Start-Sleep -Seconds 2

    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_undo";arguments=@{last=1;dryRun=$true}}} "12-undo-dryrun" 60 | Out-Null
    Start-Sleep -Seconds 2

    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_pr_description";arguments=@{action="generate";last=2;workingDir="C:\Projetos\Genexus18MCP"}}} "13-pr-description" 90 | Out-Null
} finally { if (-not $proc.HasExited) { $proc.Kill() } }
