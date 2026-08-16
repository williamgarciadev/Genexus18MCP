param(
  [string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-logs-2",
  [string]$ProbeObject = "dani"
)

$ErrorActionPreference = "Stop"
$root = "C:\Projetos\Genexus18MCP"
$gateway = Join-Path $root "publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = Join-Path $root "config.json"
$env:GX_MCP_STDIO = "true"

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

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
                $brief = if ($obj.result.content -and $obj.result.content[0].text) {
                    $t = $obj.result.content[0].text
                    if ($t.Length -gt 140) { $t.Substring(0,140) + "..." } else { $t }
                } else { ($obj | ConvertTo-Json -Compress -Depth 5).Substring(0, [Math]::Min(140, ($obj | ConvertTo-Json -Compress -Depth 5).Length)) }
                Write-Host "<<< $label ($($line.Length)b) :: $brief" -ForegroundColor Green
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
    Send-Rpc @{ jsonrpc="2.0"; id=(Next-Id); method="initialize"; params=@{ protocolVersion="2024-11-05"; capabilities=@{}; clientInfo=@{name="smoke-live-2";version="1.0"} } } "01-init" 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress))
    $proc.StandardInput.Flush()
    Start-Sleep -Seconds 1

    # Round 2: read-only against real KB
    $calls = @(
        @{ label="20-whoami-v"; name="genexus_whoami"; args=@{}; t=30 }
        @{ label="21-inspect"; name="genexus_inspect"; args=@{ name=$ProbeObject; include=@("metadata","variables","parts") }; t=60 }
        @{ label="22-inspect-runtime-ids"; name="genexus_inspect"; args=@{ name=$ProbeObject; include=@("runtimeIds") }; t=60 }
        @{ label="23-inspect-minimal"; name="genexus_inspect"; args=@{ name=$ProbeObject; projection="minimal" }; t=30 }
        @{ label="24-read"; name="genexus_read"; args=@{ name=$ProbeObject; part="Events"; limit=50 }; t=60 }
        @{ label="25-analyze-callers"; name="genexus_analyze"; args=@{ name=$ProbeObject; mode="callers" }; t=60 }
        @{ label="26-analyze-event-flow"; name="genexus_analyze"; args=@{ name=$ProbeObject; mode="event_flow" }; t=60 }
        @{ label="27-analyze-heatmap"; name="genexus_analyze"; args=@{ name="*"; mode="dependency_heatmap"; format="ascii" }; t=120 }
        @{ label="28-explain"; name="genexus_explain"; args=@{ name=$ProbeObject; depth="shallow" }; t=60 }
        @{ label="29-blame"; name="genexus_blame"; args=@{ name=$ProbeObject; part="Events" }; t=60 }
        @{ label="30-kb-explorer"; name="genexus_kb_explorer"; args=@{ action="locate"; name=$ProbeObject }; t=30 }
        @{ label="31-search"; name="genexus_search_source"; args=@{ pattern="For each"; maxResults=3; fields=@("source","caption") }; t=60 }
        @{ label="32-history"; name="genexus_history"; args=@{ action="list"; name=$ProbeObject }; t=30 }
        @{ label="33-undo-dryrun"; name="genexus_undo"; args=@{ last=1; dryRun=$true }; t=30 }
        @{ label="34-security-audit"; name="genexus_security"; args=@{ action="audit_gam" }; t=60 }
        @{ label="35-security-scan-secrets"; name="genexus_security"; args=@{ action="scan_secrets" }; t=120 }
        @{ label="36-recipe-popup"; name="genexus_recipe"; args=@{ name="popup_blocking_with_reload" }; t=30 }
        @{ label="37-recipe-versioned"; name="genexus_recipe"; args=@{ name="popup_blocking_with_reload@v1" }; t=30 }
        @{ label="38-build-plan"; name="genexus_build_plan"; args=@{ target=$ProbeObject; format="ascii" }; t=60 }
        @{ label="39-execution-history"; name="genexus_execution_history"; args=@{ target=$ProbeObject; last=10 }; t=30 }
        @{ label="40-reorg-preview"; name="genexus_lifecycle"; args=@{ action="reorg_preview" }; t=60 }
        @{ label="41-orient"; name="genexus_orient"; args=@{}; t=30 }
        @{ label="42-logs"; name="genexus_logs"; args=@{ tail=20 }; t=30 }
        @{ label="43-sample-data"; name="genexus_generate_sample_data"; args=@{ trn="Aluno2"; rows=3 }; t=60 }
        @{ label="44-sdk-probe"; name="genexus_sdk_probe"; args=@{}; t=120 }
        @{ label="45-stub-watch-event"; name="genexus_watch_event"; args=@{ target=$ProbeObject }; t=30 }
        @{ label="46-stub-rename-kb"; name="genexus_rename_across_kb"; args=@{ from="A"; to="B" }; t=30 }
        @{ label="47-stub-time-travel"; name="genexus_time_travel"; args=@{ name=$ProbeObject; at="2026-05-23T00:00:00Z" }; t=30 }
        @{ label="48-pr-description"; name="genexus_pr_description"; args=@{ action="generate"; last=3 }; t=30 }
        @{ label="49-friction-tail"; name="genexus_friction_log"; args=@{ action="tail"; n=5 }; t=30 }
        @{ label="50-wcag-check"; name="genexus_wcag_check"; args=@{ target=$ProbeObject }; t=60 }
        @{ label="51-ocr-stub"; name="genexus_ocr_screenshot"; args=@{ path="C:\\nonexistent.png" }; t=30 }
    )

    foreach ($c in $calls) {
        Send-Rpc @{ jsonrpc="2.0"; id=(Next-Id); method="tools/call"; params=@{ name=$c.name; arguments=$c.args } } $c.label $c.t | Out-Null
    }

    Write-Host "`n=== ROUND 2 COMPLETE ===" -ForegroundColor Cyan

} finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    $stderrPath = Join-Path $LogDir "gateway.stderr.log"
    Set-Content -LiteralPath $stderrPath -Value $stderrSb.ToString() -Encoding UTF8
}
