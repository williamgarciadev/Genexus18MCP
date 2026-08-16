param(
  [string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-paced",
  [string]$ProbeObject = "dani",
  [int]$PauseMs = 2000
)

$ErrorActionPreference = "Stop"
$root = "C:\Projetos\Genexus18MCP"
$gateway = Join-Path $root "publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = Join-Path $root "config.json"
$env:GX_MCP_STDIO = "true"

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()

$stderrSb = New-Object System.Text.StringBuilder
$proc.add_ErrorDataReceived({ param($s,$e); if($e.Data){[void]$stderrSb.AppendLine($e.Data)} })
$proc.BeginErrorReadLine()

function Send-Rpc {
    param([hashtable]$body, [string]$label, [int]$timeoutSec=120)
    $payload = $body | ConvertTo-Json -Depth 20 -Compress
    Write-Host ">>> $label" -ForegroundColor Cyan
    $proc.StandardInput.WriteLine($payload); $proc.StandardInput.Flush()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                Set-Content -LiteralPath (Join-Path $LogDir "$label.json") -Value $line -Encoding UTF8
                $brief = if($obj.result.content -and $obj.result.content[0].text){ $t=$obj.result.content[0].text; if($t.Length -gt 200){$t.Substring(0,200)+"..."} else {$t} } else { "<no text>" }
                Write-Host "<<< $label ($($line.Length)b) :: $brief" -ForegroundColor Green
                return $obj
            }
        }
    }
    Write-Host "!!! $label TIMEOUT after $timeoutSec sec" -ForegroundColor Red; return $null
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="paced";version="1"}}} "01-init" 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 2

    $calls = @(
        @{ label="80-ocr-stub"; name="genexus_ocr_screenshot"; args=@{ path="C:\\tmp\\x.png" }; t=30 }
        @{ label="81-friction-append"; name="genexus_friction_log"; args=@{ action="append"; tool="smoke"; message="paced run"; severity="info" }; t=30 }
        @{ label="82-friction-tail"; name="genexus_friction_log"; args=@{ action="tail"; n=3 }; t=30 }
        @{ label="83-screenshot-publish"; name="genexus_screenshot_publish"; args=@{ path="C:\\Projetos\\Genexus18MCP\\publish\\start_mcp.bat" }; t=30 }
        @{ label="84-pr-description"; name="genexus_pr_description"; args=@{ action="generate"; last=3; workingDir="C:\\Projetos\\Genexus18MCP" }; t=120 }
        @{ label="85-undo-list-zero"; name="genexus_undo"; args=@{ last=0 }; t=30 }
        @{ label="86-db-drift-check"; name="genexus_db_drift"; args=@{ action="check" }; t=60 }
        @{ label="87-edit-form-dryrun"; name="genexus_edit_form"; args=@{ action="add_textblock"; name=$ProbeObject; caption="HELLO"; format="Text"; dryRun=$true }; t=60 }
        @{ label="88-translations"; name="genexus_translations"; args=@{ action="import"; inputPath="C:\\nope.csv" }; t=30 }
        @{ label="89-run-object"; name="genexus_run_object"; args=@{ name=$ProbeObject; args=@(1,2) }; t=30 }
        @{ label="90-save-as-dryrun"; name="genexus_save_as"; args=@{ name=$ProbeObject; newName="dani_copy_smk"; dryRun=$true }; t=60 }
        @{ label="91-security-audit-gam"; name="genexus_security"; args=@{ action="audit_gam" }; t=120 }
        @{ label="92-wcag-check"; name="genexus_wcag_check"; args=@{ target=$ProbeObject }; t=60 }
    )

    foreach ($c in $calls) {
        Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name=$c.name;arguments=$c.args}} $c.label $c.t | Out-Null
        Start-Sleep -Milliseconds $PauseMs
    }

    Write-Host "`n=== PACED DONE ===" -ForegroundColor Cyan
} finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    Set-Content -LiteralPath (Join-Path $LogDir "gateway.stderr.log") -Value $stderrSb.ToString() -Encoding UTF8
}
