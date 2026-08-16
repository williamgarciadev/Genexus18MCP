# Real-KB benchmark for read / write / patch operations via stdio gateway.
# Drives the published gateway (publish/GxMcp18.Gateway.exe) over stdin/stdout JSON-RPC,
# round-trips N iterations per op against 5 stable procedures in AcademicoHomolog1,
# measures end-to-end latency from WriteLine -> matching JSON-RPC response, writes
# results to .gx-smoke-futures/bench-<label>.json.
#
# For write/patch, the harness reads the original Source first and writes it back
# verbatim every iteration, so the KB ends in baseline state.
#
# Usage:
#   ./scripts/benchmark-read-write-patch.ps1 -OutFile bench-baseline.json
#   ./scripts/benchmark-read-write-patch.ps1 -OutFile bench-after.json -Iterations 20

param(
    [string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-futures",
    [string]$OutFile = "bench-baseline.json",
    [int]$Iterations = 20
)

$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"; $env:GX_MCP_STDIO = "true"
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

$Targets = @(
    "AceitePessoa",          # 907 source chars
    "AbreSIP",               # 676
    "AceBncVerifica",        # 591
    "AbreFazAiBoletim",      # 1105
    "AbreMatDidaticoPro"     # 888
)

# Start gateway
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()
$proc.add_ErrorDataReceived({ param($s,$e) }); $proc.BeginErrorReadLine()

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

function Summarize {
    param([System.Collections.IList]$samples)
    $arr = @($samples | ForEach-Object { [double]$_ })
    if ($arr.Count -eq 0) { return @{ count=0 } }
    $avg = ($arr | Measure-Object -Average).Average
    return @{
        count = $arr.Count
        avg = [Math]::Round($avg, 2)
        p50 = [Math]::Round((Percentile $arr 50), 2)
        p95 = [Math]::Round((Percentile $arr 95), 2)
        p99 = [Math]::Round((Percentile $arr 99), 2)
        min = [Math]::Round(($arr | Measure-Object -Minimum).Minimum, 2)
        max = [Math]::Round(($arr | Measure-Object -Maximum).Maximum, 2)
    }
}

try {
    # Init + handshake
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="bench";version="1"}}} 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 3

    # Warm — kick worker + index, then poll until Ready (v2.6.9 fast-fail
    # returns Indexing envelopes until BulkIndex finishes).
    Write-Host "Warming worker + waiting for index Ready..." -ForegroundColor Cyan
    for ($t = 0; $t -lt 24; $t++) {
        $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{}}} 30
        $txt = $r.resp.result.content[0].text
        if ($txt -match '"status":"Ready"') { Write-Host "  Ready after $($t*5)s" -ForegroundColor Green; break }
        Start-Sleep -Seconds 5
    }

    # Capture baseline source per target (for write/patch restore)
    Start-Sleep -Seconds 2  # let any straggler enrichment land
    $baselines = @{}
    foreach ($t in $Targets) {
        $r = $null
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_read";arguments=@{ name=$t; part="Source"; limit=0 }}} 120
            if ($r.resp -and $r.resp.result -and $r.resp.result.content) { break }
            Write-Host "  retry $attempt for $t (resp null)" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 2
        }
        if (-not $r.resp -or -not $r.resp.result.content) { throw "Could not read baseline for $t (3 attempts)" }
        $txt = $r.resp.result.content[0].text
        $jo = $txt | ConvertFrom-Json
        if (-not $jo.source) { throw "Could not read baseline for $t (empty source)" }
        $baselines[$t] = $jo.source
        Write-Host ("  baseline {0} = {1} chars" -f $t, $jo.source.Length)
    }

    # Warm read-cache for each target (so subsequent read samples are realistic
    # for the "second-and-later read" pattern. Cold-first-read latency is a
    # separate, non-representative number we capture separately below.)
    Write-Host "`nWarming read cache..." -ForegroundColor Cyan
    foreach ($t in $Targets) {
        Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_read";arguments=@{ name=$t; part="Source"; limit=0 }}} 60 | Out-Null
    }

    # --- READ benchmark ---
    Write-Host "`nBenchmarking READ (n=$Iterations, round-robin over $($Targets.Count) targets)..." -ForegroundColor Yellow
    $readSamples = @{}
    foreach ($t in $Targets) { $readSamples[$t] = New-Object System.Collections.ArrayList }
    for ($i = 0; $i -lt $Iterations; $i++) {
        foreach ($t in $Targets) {
            $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_read";arguments=@{ name=$t; part="Source"; limit=0 }}} 60
            [void]$readSamples[$t].Add([double]$r.elapsedMs)
        }
    }
    foreach ($t in $Targets) {
        $s = Summarize $readSamples[$t]
        Write-Host ("  read {0,-22} n={1} p50={2,7}ms p95={3,7}ms p99={4,7}ms avg={5,7}ms" -f $t,$s.count,$s.p50,$s.p95,$s.p99,$s.avg)
    }

    # --- WRITE benchmark (mode=full, content = original source) ---
    Write-Host "`nBenchmarking WRITE (mode=full, restore baseline each iteration)..." -ForegroundColor Yellow
    $writeSamples = @{}
    foreach ($t in $Targets) { $writeSamples[$t] = New-Object System.Collections.ArrayList }
    for ($i = 0; $i -lt $Iterations; $i++) {
        foreach ($t in $Targets) {
            $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_edit";arguments=@{ name=$t; part="Source"; mode="full"; content=$baselines[$t] }}} 90
            [void]$writeSamples[$t].Add([double]$r.elapsedMs)
        }
    }
    foreach ($t in $Targets) {
        $s = Summarize $writeSamples[$t]
        Write-Host ("  write {0,-22} n={1} p50={2,7}ms p95={3,7}ms p99={4,7}ms avg={5,7}ms" -f $t,$s.count,$s.p50,$s.p95,$s.p99,$s.avg)
    }

    # --- PATCH benchmark (mode=patch, {find, replace} no-op-ish round trip) ---
    # We need real find/replace text for each target. Strategy: replace the very last
    # line with itself + " " (single trailing space) then immediately restore with
    # another patch back to the original. To keep the cost realistic and avoid
    # NoChange short-circuits, we do a one-way patch (add a marker comment),
    # capture it, then restore via mode=full at the end.
    Write-Host "`nBenchmarking PATCH (mode=patch, find/replace, single-byte mutation)..." -ForegroundColor Yellow
    $patchSamples = @{}
    foreach ($t in $Targets) { $patchSamples[$t] = New-Object System.Collections.ArrayList }
    # Pre-compute find/replace pair per target: pick the FIRST non-empty line as find,
    # use the same line with a trailing space toggle.
    $patchFinds = @{}
    foreach ($t in $Targets) {
        $src = $baselines[$t]
        $lines = $src -split "`n"
        $firstNonEmpty = $null
        foreach ($ln in $lines) { if ($ln.Trim().Length -gt 0) { $firstNonEmpty = $ln; break } }
        if (-not $firstNonEmpty) { throw "no non-empty line for $t" }
        $patchFinds[$t] = @{ find = $firstNonEmpty; trail = " " }
    }
    # Each iteration toggles: even => add space, odd => remove space
    for ($i = 0; $i -lt $Iterations; $i++) {
        foreach ($t in $Targets) {
            $find = $patchFinds[$t].find
            $trail = $patchFinds[$t].trail
            $isAdd = ($i % 2) -eq 0
            $findStr = if ($isAdd) { $find } else { $find + $trail }
            $replaceStr = if ($isAdd) { $find + $trail } else { $find }
            $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_edit";arguments=@{ name=$t; part="Source"; mode="patch"; patch=@{ find=$findStr; replace=$replaceStr } }}} 90
            [void]$patchSamples[$t].Add([double]$r.elapsedMs)
        }
    }
    foreach ($t in $Targets) {
        $s = Summarize $patchSamples[$t]
        Write-Host ("  patch {0,-22} n={1} p50={2,7}ms p95={3,7}ms p99={4,7}ms avg={5,7}ms" -f $t,$s.count,$s.p50,$s.p95,$s.p99,$s.avg)
    }

    # Restore baselines (defensive — patches should net out, but a full restore
    # guarantees the KB ends clean even on uneven iteration counts).
    Write-Host "`nRestoring baselines..." -ForegroundColor Cyan
    foreach ($t in $Targets) {
        Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_edit";arguments=@{ name=$t; part="Source"; mode="full"; content=$baselines[$t] }}} 90 | Out-Null
    }

    # Aggregate across all targets (round-robin samples concatenated)
    $allRead  = @($Targets | ForEach-Object { $readSamples[$_]  } | ForEach-Object { $_ })
    $allWrite = @($Targets | ForEach-Object { $writeSamples[$_] } | ForEach-Object { $_ })
    $allPatch = @($Targets | ForEach-Object { $patchSamples[$_] } | ForEach-Object { $_ })

    $result = @{
        timestamp = (Get-Date).ToString("o")
        iterations = $Iterations
        targets = $Targets
        baselineSourceChars = @{}
        ops = @{
            read = @{
                aggregate = (Summarize $allRead)
                perTarget = @{}
                samples = @{}
            }
            write = @{
                aggregate = (Summarize $allWrite)
                perTarget = @{}
                samples = @{}
            }
            patch = @{
                aggregate = (Summarize $allPatch)
                perTarget = @{}
                samples = @{}
            }
        }
    }
    foreach ($t in $Targets) {
        $result.baselineSourceChars[$t] = $baselines[$t].Length
        $result.ops.read.perTarget[$t]  = (Summarize $readSamples[$t])
        $result.ops.write.perTarget[$t] = (Summarize $writeSamples[$t])
        $result.ops.patch.perTarget[$t] = (Summarize $patchSamples[$t])
        $result.ops.read.samples[$t]  = @($readSamples[$t])
        $result.ops.write.samples[$t] = @($writeSamples[$t])
        $result.ops.patch.samples[$t] = @($patchSamples[$t])
    }

    $outPath = Join-Path $LogDir $OutFile
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outPath -Encoding UTF8
    Write-Host "`n=== BENCHMARK DONE ===" -ForegroundColor Cyan
    Write-Host "Aggregate read  : p50=$($result.ops.read.aggregate.p50)ms p95=$($result.ops.read.aggregate.p95)ms p99=$($result.ops.read.aggregate.p99)ms avg=$($result.ops.read.aggregate.avg)ms"
    Write-Host "Aggregate write : p50=$($result.ops.write.aggregate.p50)ms p95=$($result.ops.write.aggregate.p95)ms p99=$($result.ops.write.aggregate.p99)ms avg=$($result.ops.write.aggregate.avg)ms"
    Write-Host "Aggregate patch : p50=$($result.ops.patch.aggregate.p50)ms p95=$($result.ops.patch.aggregate.p95)ms p99=$($result.ops.patch.aggregate.p99)ms avg=$($result.ops.patch.aggregate.avg)ms"
    Write-Host "Wrote $outPath"
} finally {
    if (-not $proc.HasExited) { $proc.Kill() }
}
