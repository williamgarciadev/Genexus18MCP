<#
.SYNOPSIS
    Maps which GeneXus SDK assemblies the MCP already exploits and which are still
    untouched, ranked by how many service interfaces they expose -- i.e. the shortlist
    of candidates for new tools.

.DESCRIPTION
    Read-only. Never writes to the GeneXus install directory.

    Why this exists: SdkSurfaceProbe (src/GxMcp.Worker/Services/SdkSurfaceProbe.cs:78)
    enumerates AppDomain.CurrentDomain.GetAssemblies() -- it can only describe assemblies
    the worker has ALREADY LOADED. An assembly that is never referenced, and never pulled
    in transitively, is structurally invisible to it. So the probe (and the backlog derived
    from it, docs/sdk_uncovered_endpoints_*.md) systematically under-reports the SDK
    surface. This script closes that blind spot by starting from the FILESYSTEM instead.

    Three sources are cross-referenced per assembly:
      1. on disk      -- every managed GeneXus-family DLL in the install root and Packages\
      2. referenced   -- <Reference> HintPaths in GxMcp.Worker.csproj (what the build binds)
      3. probe-seen   -- the assembly table in docs/sdk-probe/INDEX.md (what got loaded)

    Coverage buckets:
      Referenced  -- the build binds it; its types are reachable today
      ProbeOnly   -- loaded transitively at runtime but never referenced explicitly
      Untouched   -- never referenced, never loaded => never inspected. The frontier.

.PARAMETER ServicesOnly
    Only list assemblies that expose at least one I*Service interface.

.PARAMETER Top
    How many untouched assemblies to print in the ranking. Default 25, 0 = all.

.PARAMETER Json
    Emit a single JSON object instead of the human-readable report.

.EXAMPLE
    powershell.exe -File .\map_sdk_coverage.ps1
.EXAMPLE
    powershell.exe -File .\map_sdk_coverage.ps1 -ServicesOnly -Top 0 -Json > coverage.json

.NOTES
    Counting public types requires reflection-only load, which is .NET Framework only.
    Run under Windows PowerShell 5.1 (powershell.exe). On PowerShell 7 the coverage
    buckets still work; the type/service counts report as unavailable.
#>
[CmdletBinding()]
param(
    [string]$CsprojPath,
    [string]$ProbeIndexPath,
    [switch]$ServicesOnly,
    [int]$Top = 25,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_gx_common.ps1')

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $CsprojPath)     { $CsprojPath     = Join-Path $repoRoot 'src\GxMcp.Worker\GxMcp.Worker.csproj' }
if (-not $ProbeIndexPath) { $ProbeIndexPath = Join-Path $repoRoot 'docs\sdk-probe\INDEX.md' }

# GeneXus-family assemblies. Same prefixes SdkSurfaceProbe filters on, plus Connector
# (the bootstrap assembly, which carries no prefix).
$familyPrefixes = @('Artech.', 'Genexus.', 'GeneXus.', 'DVelop.')

function Test-IsFamilyAssembly {
    param([string]$Name)
    if ($Name -eq 'Connector') { return $true }
    foreach ($p in $familyPrefixes) {
        if ($Name.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# --------------------------------------------------------------------------------------
# Source 2: what the Worker build references
# --------------------------------------------------------------------------------------
function Get-CsprojReferences {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Warning "csproj not found: $Path"
        return @{}
    }
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $map = @{}
    foreach ($ref in $xml.SelectNodes('//*[local-name()="Reference"]')) {
        $include = $ref.GetAttribute('Include')
        if (-not $include) { continue }
        # Strip any strong-name qualifiers: Include="Foo, Version=1.0.0.0, ..."
        $simple = ($include -split ',')[0].Trim()
        $hint = $ref.SelectSingleNode('*[local-name()="HintPath"]')
        $cond = $ref.GetAttribute('Condition')
        $map[$simple] = [PSCustomObject]@{
            Include    = $simple
            HintPath   = $(if ($hint) { $hint.InnerText } else { $null })
            IsOptional = [bool]$cond
            Condition  = $cond
        }
    }
    return $map
}

# --------------------------------------------------------------------------------------
# Source 3: what the runtime probe actually saw
# --------------------------------------------------------------------------------------
function Get-ProbeSeenAssemblies {
    param([Parameter(Mandatory)][string]$Path)

    $seen = @{}
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Warning "probe index not found: $Path (run genexus_sdk_probe to generate it)"
        return $seen
    }
    # Table rows look like:  | `Artech.Genexus.Common` | 11.0.0.0 | 6826 / 7840 | `C:\...` |
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line -notmatch '^\|\s*`([^`]+)`\s*\|') { continue }
        $name = $Matches[1]
        if ($line -match '\|\s*(\d+)\s*/\s*(\d+)\s*\|') {
            $seen[$name] = [PSCustomObject]@{ PublicTypes = [int]$Matches[1]; TotalTypes = [int]$Matches[2] }
        } else {
            $seen[$name] = [PSCustomObject]@{ PublicTypes = $null; TotalTypes = $null }
        }
    }
    return $seen
}

# --------------------------------------------------------------------------------------
# Service-interface census (the actual "is there a tool in here?" signal)
# --------------------------------------------------------------------------------------
function Get-ServiceCensus {
    <#
        Counts public service interfaces (I*Service) and concrete *Service classes.
        These are the shapes the worker already knows how to consume through
        SdkServiceResolver (IGxService) / SdkServiceLocator (everything else), so they
        are the cheapest path from "assembly exists" to "tool exists".
    #>
    param([Parameter(Mandatory)][string]$FilePath)

    $unavailable = [PSCustomObject]@{
        PublicTypes = $null; ServiceInterfaces = @(); ServiceClasses = 0; Note = 'reflection-only load unavailable'
    }
    if (-not $script:ReflectionOnlyReady) { return $unavailable }

    try {
        $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($FilePath)
        $types = $null
        try {
            $types = $asm.GetTypes()
        } catch [System.Reflection.ReflectionTypeLoadException] {
            $types = $_.Exception.Types | Where-Object { $_ -ne $null }
        }
        if (-not $types) { return $unavailable }

        $public = @($types | Where-Object { $_.IsPublic })
        $ifaces = @($public | Where-Object {
            $_.IsInterface -and $_.Name -match '^I.*Service$'
        } | ForEach-Object { $_.Name } | Sort-Object -Unique)
        $classes = @($public | Where-Object {
            -not $_.IsInterface -and -not $_.IsAbstract -and $_.Name -match 'Service$'
        }).Count

        return [PSCustomObject]@{
            PublicTypes       = $public.Count
            ServiceInterfaces = $ifaces
            ServiceClasses    = $classes
            Note              = $null
        }
    } catch {
        return [PSCustomObject]@{
            PublicTypes = $null; ServiceInterfaces = @(); ServiceClasses = 0
            Note = "$($_.Exception.GetType().Name)"
        }
    }
}

# --------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------
$resolved = Resolve-GxPath -ScriptRoot $PSScriptRoot
$gxPath   = $resolved.Path

$references = Get-CsprojReferences -Path $CsprojPath
$probeSeen  = Get-ProbeSeenAssemblies -Path $ProbeIndexPath
Initialize-ReflectionOnly -GxPath $gxPath

Write-Verbose "Scanning $gxPath (+ Packages) for managed GeneXus-family assemblies..."

$searchDirs = @($gxPath)
$packagesDir = Join-Path $gxPath 'Packages'
if (Test-Path -LiteralPath $packagesDir) { $searchDirs += $packagesDir }

$assemblies = @()
foreach ($dir in $searchDirs) {
    $location = if ($dir -eq $gxPath) { 'root' } else { 'Packages' }
    Get-ChildItem -LiteralPath $dir -Filter '*.dll' -File | ForEach-Object {
        $simple = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
        if (-not (Test-IsFamilyAssembly $simple)) { return }

        $pe = Get-PeInfo -FilePath $_.FullName -HeaderOnly
        if (-not $pe -or -not $pe.IsManaged) { return }

        $isReferenced = $references.ContainsKey($simple)
        $isProbeSeen  = $probeSeen.ContainsKey($simple)
        $coverage = if ($isReferenced) { 'Referenced' }
                    elseif ($isProbeSeen) { 'ProbeOnly' }
                    else { 'Untouched' }

        $assemblies += [PSCustomObject]@{
            Name         = $simple
            Location     = $location
            FilePath     = $_.FullName
            SizeBytes    = $_.Length
            Coverage     = $coverage
            IsReferenced = $isReferenced
            IsProbeSeen  = $isProbeSeen
            IsOptionalRef= $(if ($isReferenced) { $references[$simple].IsOptional } else { $false })
        }
    }
}

# Census only where it pays: untouched assemblies are the ones we know nothing about.
foreach ($a in $assemblies) {
    if ($a.Coverage -eq 'Untouched' -or $a.Coverage -eq 'ProbeOnly') {
        $census = Get-ServiceCensus -FilePath $a.FilePath
        Add-Member -InputObject $a -NotePropertyName 'PublicTypes'       -NotePropertyValue $census.PublicTypes
        Add-Member -InputObject $a -NotePropertyName 'ServiceInterfaces' -NotePropertyValue $census.ServiceInterfaces
        Add-Member -InputObject $a -NotePropertyName 'ServiceClasses'    -NotePropertyValue $census.ServiceClasses
        Add-Member -InputObject $a -NotePropertyName 'CensusNote'        -NotePropertyValue $census.Note
    } else {
        Add-Member -InputObject $a -NotePropertyName 'PublicTypes'       -NotePropertyValue $(if ($probeSeen.ContainsKey($a.Name)) { $probeSeen[$a.Name].PublicTypes } else { $null })
        Add-Member -InputObject $a -NotePropertyName 'ServiceInterfaces' -NotePropertyValue @()
        Add-Member -InputObject $a -NotePropertyName 'ServiceClasses'    -NotePropertyValue 0
        Add-Member -InputObject $a -NotePropertyName 'CensusNote'        -NotePropertyValue 'skipped (already referenced)'
    }
}

# Missing references: declared in the csproj but absent on disk => the build will break
# (or, for Condition="Exists(...)" refs, silently drop a feature such as HAS_SECURITY_SCANNER).
$onDisk = @{}
foreach ($a in $assemblies) { $onDisk[$a.Name] = $true }
$missingRefs = @()
foreach ($key in $references.Keys) {
    $r = $references[$key]
    if (-not $r.HintPath) { continue }          # BCL / SDK-resolved reference, not ours to check
    $expanded = $r.HintPath -replace '\$\(GX_PATH\)', $gxPath
    if (-not (Test-Path -LiteralPath $expanded)) {
        $missingRefs += [PSCustomObject]@{
            Name       = $r.Include
            HintPath   = $expanded
            IsOptional = $r.IsOptional
            Impact     = $(if ($r.IsOptional) { 'feature silently disabled (Condition=Exists)' } else { 'BUILD WILL FAIL' })
        }
    }
}

$untouched = @($assemblies | Where-Object { $_.Coverage -eq 'Untouched' })
$probeOnly = @($assemblies | Where-Object { $_.Coverage -eq 'ProbeOnly' })
$referenced = @($assemblies | Where-Object { $_.Coverage -eq 'Referenced' })

$ranked = $untouched
if ($ServicesOnly) { $ranked = @($ranked | Where-Object { $_.ServiceInterfaces.Count -gt 0 }) }
$ranked = @($ranked | Sort-Object -Property @{ Expression = { $_.ServiceInterfaces.Count }; Descending = $true },
                                            @{ Expression = { $_.PublicTypes }; Descending = $true })

$report = [PSCustomObject]@{
    InstallPath       = $gxPath
    InstallPathSource = $resolved.Source
    Csproj            = $CsprojPath
    ProbeIndex        = $ProbeIndexPath
    TotalFamilyDlls   = $assemblies.Count
    Referenced        = $referenced.Count
    ProbeOnly         = $probeOnly.Count
    Untouched         = $untouched.Count
    MissingReferences = $missingRefs
    Candidates        = $(if ($Top -gt 0) { @($ranked | Select-Object -First $Top) } else { $ranked })
    Assemblies        = $assemblies
    HostPowerShell    = "$($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
    ReflectionOnly    = $script:ReflectionOnlyReady
}

if ($Json) {
    $report | ConvertTo-Json -Depth 6
    return
}

Write-Host ''
Write-Host '=== GeneXus SDK coverage map ===' -ForegroundColor Cyan
Write-Host "Install : $gxPath  (source: $($resolved.Source))"
Write-Host "csproj  : $CsprojPath"
Write-Host "probe   : $ProbeIndexPath"
Write-Host ''

Write-Host '--- Coverage ---' -ForegroundColor Yellow
Write-Host "  GeneXus-family managed DLLs : $($assemblies.Count)"
Write-Host "  Referenced by the Worker    : $($referenced.Count)" -ForegroundColor Green
Write-Host "  Loaded but never referenced : $($probeOnly.Count)" -ForegroundColor DarkYellow
Write-Host "  Untouched (never inspected) : $($untouched.Count)" -ForegroundColor Magenta

if ($missingRefs.Count -gt 0) {
    Write-Host ''
    Write-Host '--- Build alignment: MISSING references ---' -ForegroundColor Red
    foreach ($m in $missingRefs) {
        Write-Host "  $($m.Name)  ->  $($m.Impact)"
        Write-Host "      expected at $($m.HintPath)"
    }
} else {
    Write-Host ''
    Write-Host '--- Build alignment: all csproj references present on disk ---' -ForegroundColor Green
}

if (-not $script:ReflectionOnlyReady) {
    Write-Host ''
    Write-Host '  NOTE: reflection-only load unavailable on this host; service counts omitted.' -ForegroundColor DarkYellow
    Write-Host '        Re-run under Windows PowerShell 5.1: powershell.exe -File .\map_sdk_coverage.ps1' -ForegroundColor DarkYellow
}

Write-Host ''
Write-Host '--- Untouched assemblies, ranked by exposed service interfaces ---' -ForegroundColor Yellow
Write-Host '    (these are the candidates for new MCP tools)' -ForegroundColor DarkGray
$shown = if ($Top -gt 0) { @($ranked | Select-Object -First $Top) } else { $ranked }
if ($shown.Count -eq 0) {
    Write-Host '  (none)'
} else {
    foreach ($a in $shown) {
        $svc = $a.ServiceInterfaces.Count
        $types = $(if ($null -ne $a.PublicTypes) { $a.PublicTypes } else { '?' })
        Write-Host ("  {0,-52} {1,3} svc  {2,5} types  [{3}]" -f $a.Name, $svc, $types, $a.Location)
        if ($svc -gt 0) {
            Write-Host ("      {0}" -f (($a.ServiceInterfaces | Select-Object -First 8) -join ', ')) -ForegroundColor DarkGray
            if ($svc -gt 8) { Write-Host ("      ... and {0} more" -f ($svc - 8)) -ForegroundColor DarkGray }
        }
    }
    if ($Top -gt 0 -and $ranked.Count -gt $Top) {
        Write-Host ''
        Write-Host "  ... $($ranked.Count - $Top) more untouched assemblies not shown (use -Top 0)." -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host "Host: PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))" -ForegroundColor DarkGray
Write-Host ''
