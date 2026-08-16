param([string]$OutFile = "bench-steady.json", [int]$Iterations = 15)

# Wait for worker to be Ready before measuring; numbers reflect post-BulkIndex
# steady-state, NOT cold-start. Pair with bench-discovery-analyze.ps1 to see
# both axes.

$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"
$env:GX_MCP_STDIO = "true"
$logDir = "C:\Projetos\Genexus18MCP\.gx-smoke-futures"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()

function Send-Rpc {
    param([hashtable]$body, [int]$timeoutSec=120)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                $sw.Stop(); return @{ resp = $obj; elapsedMs = $sw.Elapsed.TotalMilliseconds }
            }
        }
    }
    $sw.Stop(); return @{ resp=$null; elapsedMs=$sw.Elapsed.TotalMilliseconds; timedOut=$true }
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }
function Percentile { param([double[]]$s,[double]$p) if($s.Count -eq 0){return 0.0} $r=$s|Sort-Object; $k=($p/100.0)*($r.Count-1); $lo=[Math]::Floor($k); $hi=[Math]::Ceiling($k); if($lo -eq $hi){return [double]$r[[int]$lo]} return [double]($r[[int]$lo]*(1-($k-$lo)) + $r[[int]$hi]*($k-$lo)) }
function Agg { param([double[]]$s) if($s.Count -eq 0){return @{count=0}} return @{count=$s.Count; min=[math]::Round(($s|Measure-Object -Minimum).Minimum,2); max=[math]::Round(($s|Measure-Object -Maximum).Maximum,2); avg=[math]::Round(($s|Measure-Object -Average).Average,2); p50=[math]::Round((Percentile $s 50),2); p95=[math]::Round((Percentile $s 95),2); p99=[math]::Round((Percentile $s 99),2)} }

$Targets = @("AceitePessoa", "AbreSIP", "AceBncVerifica", "AbreFazAiBoletim", "AbreMatDidaticoPro")

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="bench";version="1"}}} 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 3

    # Wait for index Ready (poll whoami every 5s, up to 120s)
    Write-Host "Waiting for worker index to be Ready..." -ForegroundColor Cyan
    $ready = $false
    for ($t = 0; $t -lt 24; $t++) {
        $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{}}} 30
        $txt = $r.resp.result.content[0].text
        if ($txt -match '"status":"Ready"') { $ready = $true; Write-Host "  Ready after $($t*5)s" -ForegroundColor Green; break }
        Start-Sleep -Seconds 5
    }
    if (-not $ready) { Write-Host "WARN: index never reached Ready within 120s; numbers may include cold path" -ForegroundColor Yellow }

    $results = @{}
    function RunOp {
        param([string]$name, [scriptblock]$build, [int]$n=$Iterations)
        Write-Host "`n$name (n=$n)..." -ForegroundColor Yellow
        $samples = @()
        for ($i=1; $i -le $n; $i++) {
            $args = & $build $i
            $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=$args} 60
            if ($r.resp) { $samples += [double]$r.elapsedMs }
        }
        $a = Agg $samples
        Write-Host ("  p50={0,7:N1}ms p95={1,7:N1}ms p99={2,7:N1}ms avg={3,7:N1}ms" -f $a.p50, $a.p95, $a.p99, $a.avg)
        return @{ aggregate = $a; samples = $samples }
    }

    $results.whoami       = RunOp "whoami"       { @{ name="genexus_whoami"; arguments=@{} } }
    $results.list_objects = RunOp "list_objects" { @{ name="genexus_list_objects"; arguments=@{ typeFilter="Procedure"; limit=10 } } }
    $results.query        = RunOp "query"        { @{ name="genexus_query"; arguments=@{ query="Aluno"; limit=10 } } }
    $results.inspect      = RunOp "inspect"      { param($i) @{ name="genexus_inspect"; arguments=@{ name=$Targets[$i % $Targets.Count] } } }
    $results.search_source= RunOp "search_source" { @{ name="genexus_search_source"; arguments=@{ query="parm"; limit=10 } } }
    $results.analyze_impact = RunOp "analyze_impact" { param($i) @{ name="genexus_analyze"; arguments=@{ name=$Targets[$i % $Targets.Count]; mode="impact" } } }
    $results.explain      = RunOp "explain"      { param($i) @{ name="genexus_explain"; arguments=@{ name=$Targets[$i % $Targets.Count] } } }
    $results.doctor       = RunOp "doctor"       { @{ name="genexus_doctor"; arguments=@{} } }

    $payload = @{ timestamp=(Get-Date).ToString("o"); steadyState=$true; ops=$results } | ConvertTo-Json -Depth 10
    Set-Content -LiteralPath (Join-Path $logDir $OutFile) -Value $payload -Encoding UTF8
}
finally { if (-not $proc.HasExited) { $proc.Kill() }; $proc.Dispose() }
