param([string]$LogDir = "C:\Projetos\Genexus18MCP\.gx-smoke-futures")
$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"; $env:GX_MCP_STDIO = "true"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gateway
$psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi; $null = $proc.Start()
$proc.add_ErrorDataReceived({ param($s,$e) }); $proc.BeginErrorReadLine()

function Send-Rpc {
    param([hashtable]$body, [int]$timeoutSec=180)
    $proc.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { continue }
        if ($line.StartsWith("{")) {
            try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($obj.PSObject.Properties.Match("id").Count -gt 0 -and $obj.id -eq $body.id) { return $obj }
        }
    }
    return $null
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }

try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="bench";version="1"}}} 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 3
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_whoami";arguments=@{}}} 180 | Out-Null

    # Try a list of candidates and measure their Source size with limit=0 (full read).
    $candidates = @(
        "AbreviaNome","AbsPath","AceitePessoa","AbreSIP","AceBncVerifica",
        "AbreDocBox","AbreFazAiBoletim","AbreMatDidatico","Abs","AbreMatDidaticoAlu",
        "AbreMatDidaticoPro","AceFatura"
    )
    $report = @()
    foreach ($c in $candidates) {
        $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_read";arguments=@{ name=$c; part="Source"; limit=0 }}} 60
        if ($r -ne $null) {
            $txt = $r.result.content[0].text
            $bytes = $txt.Length
            $isErr = $txt -match '"status":"Error"' -or $txt -match '"error":"Object not found"'
            $sourceLen = 0
            try {
                $jo = $txt | ConvertFrom-Json
                if ($jo.source) { $sourceLen = $jo.source.Length }
            } catch {}
            $report += [pscustomobject]@{ name=$c; bytes=$bytes; sourceChars=$sourceLen; err=$isErr }
            Write-Host ("{0,-22} bytes={1,-7} sourceChars={2,-6} err={3}" -f $c,$bytes,$sourceLen,$isErr)
        } else {
            Write-Host "$c TIMEOUT"
        }
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $LogDir "sizecheck.json") -Encoding UTF8
} finally { if (-not $proc.HasExited) { $proc.Kill() } }
