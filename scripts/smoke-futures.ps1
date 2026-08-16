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
                $t = if($obj.result.content -and $obj.result.content[0].text) { $obj.result.content[0].text } else { "<no text>" }
                $brief = if($t.Length -gt 180) { $t.Substring(0,180)+"..." } else { $t }
                Write-Host "<<< $label :: $brief" -ForegroundColor Green; return $obj
            }
        }
    }
    Write-Host "!!! $label TIMEOUT" -ForegroundColor Red; return $null
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="futures";version="1"}}} "00-init" 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 2

    $calls = @(
        @{ label="01-tutorial"; name="genexus_tutorial"; args=@{ step=1 }; t=30 }
        @{ label="02-watch-event"; name="genexus_watch_event"; args=@{ target="dani"; event="Start" }; t=30 }
        @{ label="03-learning"; name="genexus_learning"; args=@{ action="report" }; t=30 }
        @{ label="04-sd-panel-inspect"; name="genexus_sd_panel"; args=@{ action="inspect"; name="nonexistent" }; t=30 }
        @{ label="05-multi-agent-lock-status"; name="genexus_multi_agent_lock"; args=@{ action="status"; target="dani"; part="Events" }; t=30 }
        @{ label="06-what-if"; name="genexus_what_if"; args=@{ change=@{ kind="attribute_type"; attribute="AluCod"; newType="Numeric(8)" } }; t=60 }
        @{ label="07-voice"; name="genexus_voice"; args=@{ transcript="add button called Confirmar" }; t=30 }
        @{ label="08-ai-complete-unset"; name="genexus_ai_complete"; args=@{ context="for each Aluno" }; t=30 }
        @{ label="09-time-travel"; name="genexus_time_travel"; args=@{ name="dani"; at="2026-05-20T00:00:00Z" }; t=60 }
        @{ label="10-auto-test-missing-path"; name="genexus_auto_test"; args=@{ action="generate_from_prod_log"; path="C:\\nonexistent.jsonl" }; t=30 }
        @{ label="11-reverse-pattern"; name="genexus_reverse_pattern"; args=@{ action="infer"; source=@("dani","nonexistent") }; t=30 }
        @{ label="12-cross-browser-stub"; name="genexus_cross_browser"; args=@{ target="dani"; browsers=@("firefox","safari") }; t=60 }
        @{ label="13-rename-across-kb"; name="genexus_rename_across_kb"; args=@{ from="nonexistent"; to="nonexistent2" }; t=30 }
        @{ label="14-kb-diff"; name="genexus_kb_diff"; args=@{ kbA="C:\KBs\AcademicoHomolog1"; kbB="C:\KBs\AcademicoHomolog1" }; t=30 }
        @{ label="15-worker-pool"; name="genexus_worker_pool"; args=@{ action="warm_spares"; spareCount=0 }; t=30 }
        @{ label="16-sandbox-remove"; name="genexus_sandbox"; args=@{ action="remove"; name="nonexistent_sandbox" }; t=30 }
        @{ label="17-github-create-pr"; name="genexus_github"; args=@{ action="create_pr"; title="(smoke test - will fail without gh)" }; t=30 }
        @{ label="18-kb-import-stub"; name="genexus_kb_import"; args=@{ from="C:\\nope"; name="X"; type="Procedure" }; t=30 }
    )
    foreach ($c in $calls) {
        Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name=$c.name;arguments=$c.args}} $c.label $c.t | Out-Null
        Start-Sleep -Milliseconds 1500
    }
    Write-Host "`n=== FUTURES SMOKE DONE ===" -ForegroundColor Cyan
} finally { if (-not $proc.HasExited) { $proc.Kill() } }
