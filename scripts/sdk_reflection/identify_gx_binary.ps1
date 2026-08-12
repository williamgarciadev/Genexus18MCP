<#
.SYNOPSIS
    Identifies GeneXus.exe: managed-vs-native verdict, CLR runtime, platform flags,
    referenced assemblies and the DLL inventory of the install directory.

.DESCRIPTION
    Read-only. Never writes to the GeneXus install directory.

    The hard facts (managed verdict, COR20 flags, runtime version) come from a manual
    PE/CLI header parse in _gx_common.ps1 rather than from reflection, so the script
    works on both PowerShell editions. Reflection is used only for
    GetReferencedAssemblies(), which degrades with an explicit note on PowerShell 7.

.PARAMETER Path
    Explicit path to the executable/DLL to identify. Defaults to <gxPath>\GeneXus.exe.

.PARAMETER Inventory
    Also classify every top-level DLL of the install directory as managed or native.

.PARAMETER Json
    Emit a single JSON object instead of the human-readable report.

.EXAMPLE
    .\identify_gx_binary.ps1
.EXAMPLE
    .\identify_gx_binary.ps1 -Inventory -Json | Set-Content gx-identity.json
.EXAMPLE
    # Referenced assemblies need .NET Framework reflection-only load:
    powershell.exe -File .\identify_gx_binary.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path,

    [switch]$Inventory,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_gx_common.ps1')

function Get-AssemblyReferences {
    <#
        ReflectionOnlyLoadFrom is .NET Framework only. On PowerShell 7 it throws
        PlatformNotSupportedException, so report the limitation rather than pretending
        the assembly has no references.
    #>
    param([Parameter(Mandatory)][string]$FilePath)

    try {
        $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($FilePath)
        $refs = $asm.GetReferencedAssemblies() | ForEach-Object {
            [PSCustomObject]@{
                Name    = $_.Name
                Version = $(if ($_.Version) { $_.Version.ToString() } else { $null })
                Group   = if ($_.Name -like 'Artech.*' -or $_.Name -like 'Genexus*' -or
                              $_.Name -like 'GeneXus*' -or $_.Name -eq 'Connector') { 'GeneXus' }
                          elseif ($_.Name -eq 'mscorlib' -or $_.Name -like 'System*') { 'BCL' }
                          else { 'ThirdParty' }
            }
        }
        return [PSCustomObject]@{
            ImageRuntimeVersion = $asm.ImageRuntimeVersion
            References          = @($refs | Sort-Object Group, Name)
            Unavailable         = $null
        }
    } catch {
        return [PSCustomObject]@{
            ImageRuntimeVersion = $null
            References          = @()
            Unavailable         = "$($_.Exception.GetType().Name): $($_.Exception.Message) " +
                                  "GetReferencedAssemblies() needs .NET Framework reflection-only load - " +
                                  "run under Windows PowerShell 5.1 (powershell.exe), or use ilspycmd."
        }
    }
}

function Get-AppConfigInfo {
    param([Parameter(Mandatory)][string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath)) { return $null }
    try {
        [xml]$xml = Get-Content -LiteralPath $ConfigPath -Raw
        # GetAttribute returns '' for a missing attribute; direct property access would
        # trip Set-StrictMode on an element that omits (say) sku.
        $supported = @($xml.SelectNodes('//*[local-name()="supportedRuntime"]') | ForEach-Object {
            [PSCustomObject]@{ Version = $_.GetAttribute('version'); Sku = $_.GetAttribute('sku') }
        })
        $redirects = @($xml.SelectNodes('//*[local-name()="dependentAssembly"]') | ForEach-Object {
            $id = $_.SelectSingleNode('*[local-name()="assemblyIdentity"]')
            $rd = $_.SelectSingleNode('*[local-name()="bindingRedirect"]')
            if ($id -and $rd) {
                [PSCustomObject]@{
                    Name       = $id.GetAttribute('name')
                    OldVersion = $rd.GetAttribute('oldVersion')
                    NewVersion = $rd.GetAttribute('newVersion')
                }
            }
        })
        return [PSCustomObject]@{
            ConfigPath        = $ConfigPath
            SupportedRuntimes = $supported
            BindingRedirects  = $redirects
        }
    } catch {
        Write-Verbose "app.config parse failed: $($_.Exception.Message)"
        return $null
    }
}

# --------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------
$resolved = Resolve-GxPath -ScriptRoot $PSScriptRoot
$gxPath   = $resolved.Path

if (-not $Path) { $Path = Join-Path $gxPath 'GeneXus.exe' }
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Target not found: $Path" }

$file        = Get-Item -LiteralPath $Path
$versionInfo = $file.VersionInfo
$pe          = Get-PeInfo -FilePath $Path
if (-not $pe) { throw "Not a valid PE image: $Path" }

$identity  = Get-AssemblyIdentity -FilePath $Path
$refInfo   = if ($pe.IsManaged) { Get-AssemblyReferences -FilePath $Path } else { $null }
$appConfig = Get-AppConfigInfo -ConfigPath "$Path.config"

# The Target Framework Moniker lives in a TargetFrameworkAttribute, which a reflection-only
# load cannot materialize without pre-loading its dependencies. The app.config sku carries
# the same value, so prefer it and fall back to a raw byte scan.
$tfm = $null
if ($appConfig -and $appConfig.SupportedRuntimes.Count -gt 0) {
    $withSku = @($appConfig.SupportedRuntimes | Where-Object { $_.Sku })
    if ($withSku.Count -gt 0) { $tfm = $withSku[0].Sku }
}
if (-not $tfm) {
    try {
        $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Path))
        $m = [regex]::Match($text, '\.NETFramework,Version=v[\d\.]+|\.NETCoreApp,Version=v[\d\.]+')
        if ($m.Success) { $tfm = $m.Value }
    } catch {
        Write-Verbose "TFM byte scan failed: $($_.Exception.Message)"
    }
}

$inventoryResult = $null
if ($Inventory) {
    $managed = 0; $native = 0; $artech = 0
    $nativeNames = @()
    Get-ChildItem -LiteralPath $gxPath -Filter '*.dll' -File | ForEach-Object {
        $probe = Get-PeInfo -FilePath $_.FullName -HeaderOnly
        if ($probe -and $probe.IsManaged) {
            $managed++
            if ($_.Name -like 'Artech.*') { $artech++ }
        } else {
            $native++
            $nativeNames += $_.Name
        }
    }
    $inventoryResult = [PSCustomObject]@{
        TotalDlls   = $managed + $native
        Managed     = $managed
        Native      = $native
        ArtechDlls  = $artech
        NativeNames = ($nativeNames | Sort-Object)
    }
}

$report = [PSCustomObject]@{
    InstallPath       = $gxPath
    InstallPathSource = $resolved.Source
    Target            = $Path
    FileSizeBytes     = $file.Length
    LastWriteTime     = $file.LastWriteTime
    CompanyName       = $versionInfo.CompanyName
    FileVersion       = $versionInfo.FileVersion
    ProductVersion    = $versionInfo.ProductVersion
    IsManaged         = $pe.IsManaged
    Language          = $(if ($pe.IsManaged) { 'C# / .NET (managed IL)' } else { 'native (unmanaged)' })
    Pe                = $pe
    TargetFramework   = $tfm
    Identity          = $identity
    ImageRuntime      = $(if ($refInfo) { $refInfo.ImageRuntimeVersion } else { $null })
    References        = $(if ($refInfo) { $refInfo.References } else { @() })
    ReferencesNote    = $(if ($refInfo) { $refInfo.Unavailable } else { $null })
    AppConfig         = $appConfig
    Inventory         = $inventoryResult
    HostPowerShell    = "$($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
}

if ($Json) {
    $report | ConvertTo-Json -Depth 6
    return
}

Write-Host ''
Write-Host '=== GeneXus binary identity ===' -ForegroundColor Cyan
Write-Host "Install path : $gxPath  (source: $($resolved.Source))"
Write-Host "Target       : $Path"
Write-Host "Size / mtime : $($file.Length) bytes / $($file.LastWriteTime)"
Write-Host ''

Write-Host '--- Verdict ---' -ForegroundColor Yellow
if ($pe.IsManaged) {
    Write-Host '  MANAGED - built with C# / .NET (IL image, CLI header present)' -ForegroundColor Green
} else {
    Write-Host '  NATIVE - no CLI header, not a .NET assembly' -ForegroundColor Red
}
Write-Host "  Company      : $($versionInfo.CompanyName)"
Write-Host "  FileVersion  : $($versionInfo.FileVersion)"
Write-Host "  ProductVer   : $($versionInfo.ProductVersion)"
if ($identity.FullName) {
    Write-Host "  Assembly     : $($identity.FullName)"
    Write-Host "  PublicKeyTok : $($identity.PublicKeyToken)"
    Write-Host "  ProcArch     : $($identity.ProcessorArchitecture)"
} elseif ($identity.Error) {
    Write-Host "  Assembly     : (GetAssemblyName failed: $($identity.Error))"
}

Write-Host ''
Write-Host '--- PE / CLI header ---' -ForegroundColor Yellow
Write-Host "  Machine        : $($pe.Machine) ($($pe.MachineName))"
Write-Host "  PE format      : $($pe.PeFormat) (magic $($pe.OptionalMagic))"
if ($pe.IsManaged) {
    Write-Host "  CLI dir 14     : RVA $('0x{0:X}' -f $pe.CliHeaderRva), size $($pe.CliHeaderSize)"
    Write-Host "  COR20 flags    : $($pe.Cor20Flags)"
    Write-Host "    ILONLY            : $($pe.IlOnly)"
    Write-Host "    32BITREQUIRED     : $($pe.Requires32Bit)"
    Write-Host "    32BITPREFERRED    : $($pe.Prefers32Bit)"
    Write-Host "    STRONGNAMESIGNED  : $($pe.StrongNameSigned)"
    Write-Host "  Platform       : $($pe.Platform)" -ForegroundColor Cyan
    if ($pe.PSObject.Properties.Name -contains 'RuntimeVersion') {
        Write-Host "  Runtime        : $($pe.RuntimeVersion)   (metadata $($pe.MetadataVersion), sig $($pe.MetadataSignature))"
    }
}
if ($tfm) { Write-Host "  TargetFramework: $tfm" }

if ($appConfig) {
    Write-Host ''
    Write-Host '--- app.config ---' -ForegroundColor Yellow
    foreach ($sr in $appConfig.SupportedRuntimes) {
        Write-Host "  supportedRuntime version=$($sr.Version) sku=$($sr.Sku)"
    }
    Write-Host "  bindingRedirects: $($appConfig.BindingRedirects.Count)"
    foreach ($br in $appConfig.BindingRedirects) {
        Write-Host "    $($br.Name): $($br.OldVersion) -> $($br.NewVersion)"
    }
}

Write-Host ''
Write-Host '--- Referenced assemblies ---' -ForegroundColor Yellow
if ($refInfo -and $refInfo.Unavailable) {
    Write-Host "  (unavailable) $($refInfo.Unavailable)" -ForegroundColor DarkYellow
} elseif ($refInfo) {
    Write-Host "  ImageRuntimeVersion: $($refInfo.ImageRuntimeVersion)"
    Write-Host "  Total: $($refInfo.References.Count)"
    foreach ($group in @('GeneXus', 'ThirdParty', 'BCL')) {
        $inGroup = @($refInfo.References | Where-Object { $_.Group -eq $group })
        if ($inGroup.Count -eq 0) { continue }
        Write-Host "  [$group] ($($inGroup.Count))"
        foreach ($r in $inGroup) { Write-Host "    $($r.Name), Version=$($r.Version)" }
    }
}

if ($inventoryResult) {
    Write-Host ''
    Write-Host '--- Install directory inventory (top-level *.dll) ---' -ForegroundColor Yellow
    Write-Host "  Total DLLs : $($inventoryResult.TotalDlls)"
    Write-Host "  Managed    : $($inventoryResult.Managed)   (Artech.*: $($inventoryResult.ArtechDlls))"
    Write-Host "  Native     : $($inventoryResult.Native)"
    Write-Host "  Native list: $($inventoryResult.NativeNames -join ', ')"
}

Write-Host ''
Write-Host "Host: PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))" -ForegroundColor DarkGray
Write-Host ''
