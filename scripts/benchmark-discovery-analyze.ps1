param(
    [string]$OutFile = "bench-discovery.json",
    [int]$Iterations = 15
)

# Baseline for the discovery / analyze surface — every tool an LLM calls
# multiple times per session. Round-trip latency measured from stdio
# WriteLine to matching response. AcademicoHomolog1 KB used as the fixture.
#
# Tools covered:
#   whoami, list_objects, query, inspect, search_source, analyze (impact +
#   callers + summary), explain, apply_pattern (diagnose), doctor.

$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"
$env:GX_MCP_STDIO = "true"
$logDir = "C:\Projetos\Genexus18MCP\.gx-smoke-futures"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()

function Send-Rpc {
    param([hashtable]$body, [int]$timeoutSec=180)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                $sw.Stop()
                return @{ resp = $obj; elapsedMs = $sw.Elapsed.TotalMilliseconds }
            }
        }
    }
    $sw.Stop()
    return @{ resp = $null; elapsedMs = $sw.Elapsed.TotalMilliseconds; timedOut = $true }
}

$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }

function Percentile {
    param([double[]]$samples, [double]$p)
    if ($samples.Count -eq 0) { return 0.0 }
    $sorted = $samples | Sort-Object
    $rank = ($p / 100.0) * ($sorted.Count - 1)
    $lo = [Math]::Floor($rank); $hi = [Math]::Ceiling($rank)
    if ($lo -eq $hi) { return [double]$sorted[[int]$lo] }
    $frac = $rank - $lo
    return [double]($sorted[[int]$lo] * (1.0 - $frac) + $sorted[[int]$hi] * $frac)
}

function Agg {
    param([double[]]$s)
    if ($s.Count -eq 0) { return @{ count=0 } }
    return @{
        count = $s.Count
        min = [math]::Round(($s | Measure-Object -Minimum).Minimum, 2)
        max = [math]::Round(($s | Measure-Object -Maximum).Maximum, 2)
        avg = [math]::Round(($s | Measure-Object -Average).Average, 2)
        p50 = [math]::Round((Percentile $s 50), 2)
        p95 = [math]::Round((Percentile $s 95), 2)
        p99 = [math]::Round((Percentile $s 99), 2)
    }
}

$Targets = @("AceitePessoa", "AbreSIP", "AceBncVerifica", "AbreFazAiBoletim", "AbreMatDidaticoPro")

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="bench-disc";version="1"}}} 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 4

    # Warm worker
    Write-Host "Warming..." -ForegroundColor Cyan
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{}}} 180 | Out-Null
    Start-Sleep -Seconds 3

    $results = @{}

    function RunOp {
        param([string]$name, [scriptblock]$build, [int]$n=$Iterations)
        Write-Host "`n$name (n=$n)..." -ForegroundColor Yellow
        $samples = @()
        for ($i=1; $i -le $n; $i++) {
            $args = & $build $i
            $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=$args} 120
            if ($r.resp) { $samples += [double]$r.elapsedMs }
        }
        $a = Agg $samples
        Write-Host ("  p50={0,7:N1}ms p95={1,7:N1}ms p99={2,7:N1}ms avg={3,7:N1}ms" -f $a.p50, $a.p95, $a.p99, $a.avg)
        return @{ aggregate = $a; samples = $samples }
    }

    $results.whoami       = RunOp "whoami"       { @{ name="genexus_whoami";       arguments=@{} } }
    $results.list_objects = RunOp "list_objects" { @{ name="genexus_list_objects"; arguments=@{ typeFilter="Procedure"; limit=10 } } }
    $results.query        = RunOp "query"        { @{ name="genexus_query";        arguments=@{ query="Aluno"; limit=10 } } }
    $results.inspect      = RunOp "inspect"      { param($i) @{ name="genexus_inspect"; arguments=@{ name=$Targets[$i % $Targets.Count] } } }
    $results.search_source= RunOp "search_source" { @{ name="genexus_search_source"; arguments=@{ query="parm"; limit=10 } } }
    $results.analyze_impact = RunOp "analyze_impact" { param($i) @{ name="genexus_analyze"; arguments=@{ name=$Targets[$i % $Targets.Count]; mode="impact" } } }
    $results.analyze_callers= RunOp "analyze_callers" { param($i) @{ name="genexus_analyze"; arguments=@{ name=$Targets[$i % $Targets.Count]; mode="callers" } } }
    $results.analyze_summary= RunOp "analyze_summary" { param($i) @{ name="genexus_analyze"; arguments=@{ name=$Targets[$i % $Targets.Count]; mode="summary" } } }
    $results.explain      = RunOp "explain"      { param($i) @{ name="genexus_explain"; arguments=@{ name=$Targets[$i % $Targets.Count] } } }
    $results.apply_pattern_diagnose = RunOp "apply_pattern_diagnose" { param($i) @{ name="genexus_apply_pattern"; arguments=@{ name=$Targets[$i % $Targets.Count]; pattern="WorkWithPlus"; mode="diagnose" } } }
    $results.doctor       = RunOp "doctor"       { @{ name="genexus_doctor"; arguments=@{} } }

    Write-Host "`n=== BENCHMARK DONE ===" -ForegroundColor Cyan
    $payload = @{ timestamp = (Get-Date).ToString("o"); ops = $results } | ConvertTo-Json -Depth 10
    $outPath = Join-Path $logDir $OutFile
    Set-Content -LiteralPath $outPath -Value $payload -Encoding UTF8
    Write-Host "Wrote $outPath"
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    $proc.Dispose()
}
