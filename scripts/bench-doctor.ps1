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

    # Drive a quick patch on AbreSIP and AceBncVerifica then call doctor
    $targets = @("AbreSIP","AceBncVerifica","AbreFazAiBoletim")
    foreach ($t in $targets) {
        $r = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_read";arguments=@{ name=$t; part="Source"; limit=0 }}} 60
        $src = ($r.resp.result.content[0].text | ConvertFrom-Json).source
        $lines = $src -split "`n"
        $firstNonEmpty = ($lines | Where-Object { $_.Trim().Length -gt 0 })[0]
        # Patch then restore
        Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_edit";arguments=@{ name=$t; part="Source"; mode="patch"; patch=@{find=$firstNonEmpty; replace=($firstNonEmpty+" ")} }}} 60 | Out-Null
        Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_edit";arguments=@{ name=$t; part="Source"; mode="full"; content=$src }}} 60 | Out-Null
    }

    # Now call doctor
    $doc = Send-Rpc @{jsonrpc="2.0";id=(Next-Id);method="tools/call";params=@{name="genexus_doctor";arguments=@{}}} 60
    $txt = $doc.resp.result.content[0].text
    Set-Content -LiteralPath (Join-Path $LogDir "bench-doctor.json") -Value $txt -Encoding UTF8
    Write-Host "doctor bytes: $($txt.Length)"
} finally { if (-not $proc.HasExited) { $proc.Kill() } }
