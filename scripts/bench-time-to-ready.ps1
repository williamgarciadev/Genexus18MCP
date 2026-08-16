$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"
$env:GX_MCP_STDIO = "true"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi
$swBoot = [Diagnostics.Stopwatch]::StartNew()
$null = $proc.Start()

function Send-Rpc {
    param([hashtable]$body, [int]$timeoutSec=30)
    $proc.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) {
                return $obj
            }
        }
    }
    return $null
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }
try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="bench-ttr";version="1"}}} 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()

    $firstLiteReady = $null
    $firstReady = $null
    $firstListWorks = $null
    while ($swBoot.Elapsed.TotalSeconds -lt 120) {
        $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{}}} 30
        if ($r -and $r.result.content) {
            $txt = $r.result.content[0].text
            if (-not $firstLiteReady -and ($txt -match '"status":"LiteReady"' -or $txt -match '"status":"Enriching"' -or $txt -match '"status":"Ready"')) {
                $firstLiteReady = $swBoot.Elapsed.TotalSeconds
                Write-Host ("  LiteReady at {0:N1}s" -f $firstLiteReady) -ForegroundColor Yellow
            }
            if (-not $firstReady -and $txt -match '"status":"Ready"') {
                $firstReady = $swBoot.Elapsed.TotalSeconds
                Write-Host ("  Ready at {0:N1}s" -f $firstReady) -ForegroundColor Green
            }
        }
        # try list_objects to see if it succeeds (non-Indexing)
        if (-not $firstListWorks) {
            $lr = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_list_objects";arguments=@{typeFilter="Procedure";limit=5}}} 10
            if ($lr -and $lr.result.content) {
                $ltxt = $lr.result.content[0].text
                if ($ltxt -notmatch '"status":"Indexing"' -and $ltxt -match '"results"') {
                    $firstListWorks = $swBoot.Elapsed.TotalSeconds
                    Write-Host ("  list_objects accepted at {0:N1}s" -f $firstListWorks) -ForegroundColor Cyan
                }
            }
        }
        if ($firstReady -and $firstListWorks) { break }
        Start-Sleep -Milliseconds 500
    }
    Write-Host ""
    Write-Host ("LiteReady : {0:N1}s" -f $firstLiteReady)
    Write-Host ("list works: {0:N1}s" -f $firstListWorks)
    Write-Host ("Ready     : {0:N1}s" -f $firstReady)
}
finally { if (-not $proc.HasExited) { $proc.Kill() }; $proc.Dispose() }
