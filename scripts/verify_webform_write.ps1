# End-to-end empirical proof that the WebForm composition-repair fix persists writes.
# Drives the Worker as a child process with stdin piped from us, polling for the response.

[CmdletBinding()]
param(
    [string]$KbPath = "C:\KBs\AcademicoHomolog1",
    [string]$ObjectName = "ListaAtiCPAlunoUniGra",
    [string]$Control = "TextBlockSaldoHoras",
    [string]$NewValue = "Saldo TESTE8:"
)

$ErrorActionPreference = 'Stop'
$worker = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\publish\worker\GxMcp18.Worker.exe"))
if (-not (Test-Path $worker)) { throw "Worker not found at $worker" }

function Wait-File-Contains {
    param([string]$Path, [string[]]$Patterns, [int]$TimeoutSec)
    $started = [DateTime]::UtcNow
    while (([DateTime]::UtcNow - $started).TotalSeconds -lt $TimeoutSec) {
        $txt = try { Get-Content -LiteralPath $Path -Raw -ErrorAction Stop } catch { '' }
        foreach ($p in $Patterns) { if ($txt -match $p) { return $txt } }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-Worker {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [string]$JsonRequest,
        [string]$ExpectIdPattern,   # regex looking for the response line for this request
        [int]$SdkReadyTimeoutSec = 120,
        [int]$ResponseTimeoutSec = 240
    )

    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan

    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $worker
    $psi.Arguments = "--kb `"$KbPath`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $false   # Worker writes to stdout; we redirect via cmd
    $psi.RedirectStandardError  = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = Split-Path $worker -Parent
    $psi.Environment["GX_PROGRAM_DIR"] = "C:\Program Files (x86)\GeneXus\GeneXus18"
    $psi.Environment["GX_KB_PATH"] = $KbPath

    # We need stdin open AND stdout/stderr redirected to files. Use a small launcher: tee both
    # streams via cmd redirect, but stdin must come from us. Workaround: start worker directly
    # with RedirectStandardOutput=$true and copy stdout in a background runspace.
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::Start($psi)

    # Background copy stdout/stderr to files using runspaces (no event handlers - scope works in runspace).
    $outRs = [PowerShell]::Create()
    $outRs.AddScript({
        param($stream, $path)
        $sw = New-Object System.IO.StreamWriter($path, $true, [System.Text.Encoding]::UTF8)
        $sw.AutoFlush = $true
        $reader = $stream
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ($null -ne $line) { $sw.WriteLine($line) }
        }
        $sw.Close()
    }) | Out-Null
    $outRs.AddArgument($proc.StandardOutput) | Out-Null
    $outRs.AddArgument($outFile) | Out-Null
    $outHandle = $outRs.BeginInvoke()

    $errRs = [PowerShell]::Create()
    $errRs.AddScript({
        param($stream, $path)
        $sw = New-Object System.IO.StreamWriter($path, $true, [System.Text.Encoding]::UTF8)
        $sw.AutoFlush = $true
        $reader = $stream
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ($null -ne $line) { $sw.WriteLine($line) }
        }
        $sw.Close()
    }) | Out-Null
    $errRs.AddArgument($proc.StandardError) | Out-Null
    $errRs.AddArgument($errFile) | Out-Null
    $errHandle = $errRs.BeginInvoke()

    # Wait for SDK ready (in stderr — Logger.Info writes there)
    $started = [DateTime]::UtcNow
    $ready = Wait-File-Contains -Path $errFile -Patterns @('Worker SDK ready') -TimeoutSec $SdkReadyTimeoutSec
    if (-not $ready) {
        try { Stop-Process -Id $proc.Id -Force } catch { }
        $errTxt = try { Get-Content -LiteralPath $errFile -Raw } catch { '' }
        throw "SDK init timeout. Stderr tail:`n$($errTxt.Substring([Math]::Max(0,$errTxt.Length-2000)))"
    }
    Write-Host "  SDK ready at $([math]::Round(([DateTime]::UtcNow - $started).TotalSeconds, 1))s" -ForegroundColor Gray

    # Send the request
    Write-Host "  -> $JsonRequest" -ForegroundColor DarkGray
    $proc.StandardInput.WriteLine($JsonRequest)
    $proc.StandardInput.Flush()

    # Wait for a response with the expected id, OR a generic JSON response line
    $pattern = if ($ExpectIdPattern) { $ExpectIdPattern } else { '"jsonrpc"\s*:\s*"2\.0"' }
    $started2 = [DateTime]::UtcNow
    $resp = $null
    while (([DateTime]::UtcNow - $started2).TotalSeconds -lt $ResponseTimeoutSec) {
        $outTxt = try { Get-Content -LiteralPath $outFile -Raw -ErrorAction Stop } catch { '' }
        if ($outTxt -match $pattern) { $resp = $outTxt; break }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 400
    }
    Write-Host "  response received at +$([math]::Round(([DateTime]::UtcNow - $started2).TotalSeconds, 1))s (after request)" -ForegroundColor Gray

    # Give it a moment to flush trailing diagnostics, then close stdin and terminate
    Start-Sleep -Milliseconds 1500
    try { $proc.StandardInput.Close() } catch { }
    Start-Sleep -Milliseconds 1500
    if (-not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force } catch { }
    }
    try { $proc.WaitForExit(5000) | Out-Null } catch { }
    try { $outRs.EndInvoke($outHandle) } catch { }
    try { $errRs.EndInvoke($errHandle) } catch { }
    $outRs.Dispose(); $errRs.Dispose()

    $outAll = try { Get-Content -LiteralPath $outFile -Raw } catch { '' }
    $errAll = try { Get-Content -LiteralPath $errFile -Raw } catch { '' }
    Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue

    return [pscustomobject]@{
        Out = $outAll -split "`r?`n"
        Err = $errAll -split "`r?`n"
    }
}

# Step 1: WRITE
$writeReq = "{`"method`":`"layout`",`"action`":`"SetProperty`",`"target`":`"$ObjectName`",`"params`":{`"control`":`"$Control`",`"propertyName`":`"Caption`",`"value`":`"$NewValue`"},`"id`":`"w1`"}"
$writeResult = Invoke-Worker -Label "WRITE phase (Worker A)" -JsonRequest $writeReq -ExpectIdPattern '"id"\s*:\s*"w1"'

$writeResponses = @()
foreach ($line in $writeResult.Out) {
    if ($null -eq $line) { continue }
    $trim = $line.Trim()
    if ($trim.StartsWith('{') -and $trim.Contains('"jsonrpc"')) {
        try { $writeResponses += ($trim | ConvertFrom-Json -Depth 50) } catch { }
    }
}
Write-Host ""
Write-Host "Write responses: $($writeResponses.Count)" -ForegroundColor Yellow
foreach ($r in $writeResponses) {
    $txt = ($r | ConvertTo-Json -Depth 12 -Compress)
    if ($txt.Length -gt 800) { $txt = $txt.Substring(0, 800) + "..." }
    Write-Host "  $txt" -ForegroundColor Gray
}

$diagPatterns = 'CompositionRepair|TypedWriter|VisualWrite|SetProperty:|PersistVisualXml|PerformSave|verification failed|m_Document'
$diagLines = @($writeResult.Out + $writeResult.Err) | Where-Object { $_ -and ($_ -match $diagPatterns) }
Write-Host ""
Write-Host "Write-side diagnostics ($($diagLines.Count) lines):" -ForegroundColor Yellow
$diagLines | Select-Object -Last 60 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

# Step 2: READ
$readReq = "{`"method`":`"read`",`"action`":`"ExtractSource`",`"target`":`"$ObjectName`",`"params`":{`"part`":`"WebForm`"},`"id`":`"r1`"}"
$readResult = Invoke-Worker -Label "READ phase (Worker B — fresh process)" -JsonRequest $readReq -ExpectIdPattern '"id"\s*:\s*"r1"'

$readResponses = @()
foreach ($line in $readResult.Out) {
    if ($null -eq $line) { continue }
    $trim = $line.Trim()
    if ($trim.StartsWith('{') -and $trim.Contains('"jsonrpc"')) {
        try { $readResponses += ($trim | ConvertFrom-Json -Depth 50) } catch { }
    }
}

$persistedXml = $null
foreach ($r in $readResponses) {
    if ($null -ne $r.result) {
        $txt = if ($r.result -is [string]) { $r.result } else { $r.result | ConvertTo-Json -Depth 50 -Compress }
        if ($txt -match 'WebForm|Form|gxTextBlock|CaptionExpression|Tokens') { $persistedXml = $txt; break }
    }
}

Write-Host ""
Write-Host "=== VERDICT ===" -ForegroundColor Cyan
if (-not $persistedXml) {
    Write-Host "Read response empty / unrecognized. Last 40 read-phase lines:" -ForegroundColor Red
    $readResult.Out | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    exit 2
}

if ($persistedXml.Contains($NewValue)) {
    Write-Host "PASS — persisted XML contains '$NewValue'." -ForegroundColor Green
    exit 0
} else {
    Write-Host "FAIL — persisted XML does NOT contain '$NewValue'." -ForegroundColor Red
    $snippet = if ($persistedXml.Length -gt 2500) { $persistedXml.Substring(0, 2500) + "..." } else { $persistedXml }
    Write-Host "Read snippet: $snippet" -ForegroundColor DarkGray
    exit 1
}
