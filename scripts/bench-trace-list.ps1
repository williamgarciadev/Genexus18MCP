$ErrorActionPreference = "Stop"
$gateway = "C:\Projetos\Genexus18MCP\publish\GxMcp18.Gateway.exe"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\config.json"
$env:GX_MCP_STDIO = "true"
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
    $sw.Stop(); return @{ resp = $null; elapsedMs = $sw.Elapsed.TotalMilliseconds; timedOut = $true }
}
$rpcId=0; function Next-Id { $script:rpcId+=1; return $script:rpcId }
try {
    Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="trace";version="1"}}} 30 | Out-Null
    $proc.StandardInput.WriteLine((@{jsonrpc="2.0";method="notifications/initialized"} | ConvertTo-Json -Compress)); $proc.StandardInput.Flush()
    Start-Sleep -Seconds 3
    $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_list_objects";arguments=@{typeFilter="Procedure";limit=10}}} 180
    Write-Host ("Elapsed: {0:N1}ms" -f $r.elapsedMs)
    Write-Host "Response text (first 800 chars):"
    Write-Host $r.resp.result.content[0].text.Substring(0, [math]::Min(800, $r.resp.result.content[0].text.Length))
} finally { if (-not $proc.HasExited) { $proc.Kill() } }
