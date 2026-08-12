<#
.SYNOPSIS
    For each service interface in the given SDK assemblies, decides HOW the worker could
    resolve it -- or that it probably cannot be resolved headless at all.

.DESCRIPTION
    Read-only. Never writes to the GeneXus install directory.

    map_sdk_coverage.ps1 answers "which assemblies has nobody looked at?". This script
    answers the next question: "of the services in there, which are actually reachable?".

    The worker has exactly two idioms for obtaining an SDK service, and which one applies
    is a property of the type, not a matter of taste:

      1. The interface implements IGxService
         -> SdkServiceResolver.Resolve<T>()          (Helpers/SdkServiceResolver.cs:23)
      2. It does not, but a concrete impl has a public parameterless constructor
         -> SdkServiceLocator.ConstructOrResolve<T>() (Helpers/SdkServiceLocator.cs:40)

    Anything matching neither has no known headless entry point. Reporting that up front
    is the whole point: it is far cheaper than discovering it after wiring a tool.

    Note the analysis is STATIC. A verdict of Resolver/Locator says an entry point exists,
    not that the service initialises correctly in a headless worker -- several UI-side
    services resolve on paper and still fail at runtime (see the "wall" table in
    docs/sdk_endpoints_roadmap.md). Treat the output as a shortlist to try, not a promise.

.PARAMETER Assembly
    Assembly simple names or wildcard patterns (e.g. 'GeneXus.Server.Contracts',
    'Artech.ReverseEngineering.*'). Omit to scan every GeneXus-family assembly.

.PARAMETER Pattern
    Regex the interface name must match. Default '(Service|Manager|Provider)$' -- broader
    than map_sdk_coverage's I*Service, which misses Manager/Provider shaped entry points.

.PARAMETER IncludeMembers
    List each interface's public methods (first 12).

.PARAMETER Json
    Emit a single JSON object instead of the human-readable report.

.EXAMPLE
    powershell.exe -File .\probe_sdk_services.ps1 -Assembly GeneXus.Server.Contracts -IncludeMembers
.EXAMPLE
    powershell.exe -File .\probe_sdk_services.ps1 -Assembly 'Artech.ReverseEngineering.*','GeneXus.DesignOps.*'

.NOTES
    Requires Windows PowerShell 5.1 (powershell.exe): reflection-only load does not exist
    on .NET Core.
#>
[CmdletBinding()]
param(
    [string[]]$Assembly,
    [string]$Pattern = '(Service|Manager|Provider)$',
    [switch]$IncludeMembers,
    [switch]$CommandClasses,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_gx_common.ps1')

# Accept both a real array (-Assembly a,b from a PS host) and a single CSV string
# (-Assembly "a,b"), which is what survives invocation via powershell.exe -File.
if ($Assembly) {
    $Assembly = @($Assembly |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ })
}

$familyPrefixes = @('Artech.', 'Genexus.', 'GeneXus.', 'DVelop.')

function Test-IsFamilyAssembly {
    param([string]$Name)
    if ($Name -eq 'Connector') { return $true }
    foreach ($p in $familyPrefixes) {
        if ($Name.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Test-MatchesRequest {
    param([string]$Name)
    if (-not $Assembly -or $Assembly.Count -eq 0) { return $true }
    foreach ($pat in $Assembly) {
        if ($Name -like $pat) { return $true }
    }
    return $false
}

function Test-ImplementsIGxService {
    <#
        IGxService is the marker Services.TryGetService<T>()'s generic constraint requires.
        Walking GetInterfaces() by simple name avoids depending on which Artech assembly
        happens to declare it in a given SDK build.
    #>
    param([Parameter(Mandatory)]$Type)
    try {
        foreach ($i in $Type.GetInterfaces()) {
            if ($i.Name -eq 'IGxService') { return $true }
        }
    } catch {
        Write-Verbose "GetInterfaces failed on $($Type.FullName): $($_.Exception.Message)"
    }
    return $false
}

function Get-PublicParameterlessCtor {
    param([Parameter(Mandatory)]$Type)
    try {
        foreach ($c in $Type.GetConstructors()) {
            if ($c.IsPublic -and $c.GetParameters().Count -eq 0) { return $true }
        }
    } catch {
        Write-Verbose "GetConstructors failed on $($Type.FullName): $($_.Exception.Message)"
    }
    return $false
}

# --------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------
$resolved = Resolve-GxPath -ScriptRoot $PSScriptRoot
$gxPath   = $resolved.Path
Initialize-ReflectionOnly -GxPath $gxPath

if (-not (Test-ReflectionOnlyReady)) {
    throw "Reflection-only load unavailable on PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition)). Re-run with powershell.exe (Windows PowerShell 5.1)."
}

$searchDirs = @($gxPath)
$packagesDir = Join-Path $gxPath 'Packages'
if (Test-Path -LiteralPath $packagesDir) { $searchDirs += $packagesDir }

# Pass 1: collect the interfaces and every concrete public class, so implementations can be
# matched across the whole scanned set rather than only within the declaring assembly.
$interfaces = @()
$concretes  = @()
$commands   = @()
$scanned    = 0

foreach ($dir in $searchDirs) {
    Get-ChildItem -LiteralPath $dir -Filter '*.dll' -File | ForEach-Object {
        $simple = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
        if (-not (Test-IsFamilyAssembly $simple)) { return }

        # An interface and its implementation routinely live in DIFFERENT assemblies:
        # ISpecifierService is declared in Artech.Genexus.Common while the concrete
        # SpecifierService ships in Artech.Packages.Specifier. So the implementation pool
        # must be built from every family assembly, and -Assembly filters only which
        # interfaces get REPORTED. Scanning just the requested assemblies would silently
        # report "no concrete impl found" for exactly the services that are wired today.
        $isRequested = Test-MatchesRequest $simple

        $pe = Get-PeInfo -FilePath $_.FullName -HeaderOnly
        if (-not $pe -or -not $pe.IsManaged) { return }

        # @() is load-bearing: PowerShell unwraps a single-element array on return, and
        # Set-StrictMode then makes .Count throw PropertyNotFoundStrict.
        $types = @(Get-PublicTypesSafe -FilePath $_.FullName)
        if ($types.Count -eq 0) { return }
        if ($isRequested) { $scanned++ }

        foreach ($t in $types) {
            try {
                if ($t.IsInterface) {
                    if ($isRequested -and $t.Name -match $Pattern) {
                        $interfaces += [PSCustomObject]@{
                            Assembly = $simple
                            Name     = $t.Name
                            FullName = $t.FullName
                            Type     = $t
                        }
                    }
                } elseif (-not $t.IsAbstract) {
                    $concretes += [PSCustomObject]@{ Assembly = $simple; Type = $t }

                    # Command-shaped classes are a second entry-point family the SDK uses
                    # heavily (e.g. Artech.Genexus.Common.Commands.CSSGen.
                    # GenerateCssForMainObjectCommand). They are concrete classes, not
                    # interfaces, so an interface-only census misses them entirely.
                    if ($CommandClasses -and $isRequested -and $t.Name -match 'Command$') {
                        $ctors = @()
                        try {
                            $ctors = @($t.GetConstructors() | Where-Object { $_.IsPublic } | ForEach-Object {
                                $ps = ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
                                "($ps)"
                            })
                        } catch { $ctors = @() }
                        if ($ctors.Count -gt 0) {
                            $commands += [PSCustomObject]@{
                                Assembly  = $simple
                                Name      = $t.Name
                                Namespace = $t.Namespace
                                Ctors     = $ctors
                            }
                        }
                    }
                }
            } catch {
                Write-Verbose "type inspect failed: $($_.Exception.Message)"
            }
        }
    }
}

# Pass 2: classify.
$results = foreach ($iface in $interfaces) {
    $isGx = Test-ImplementsIGxService -Type $iface.Type

    $impls = @()
    foreach ($c in $concretes) {
        try {
            $names = $c.Type.GetInterfaces() | ForEach-Object { $_.FullName }
            if ($names -contains $iface.FullName) {
                $impls += [PSCustomObject]@{
                    Assembly       = $c.Assembly
                    FullName       = $c.Type.FullName
                    PublicCtor     = (Get-PublicParameterlessCtor -Type $c.Type)
                    ImplementsIGx  = (Test-ImplementsIGxService -Type $c.Type)
                }
            }
        } catch { continue }
    }

    $constructible = @($impls | Where-Object { $_.PublicCtor })
    $verdict = if ($isGx) { 'SdkServiceResolver (IGxService)' }
               elseif ($constructible.Count -gt 0) { 'SdkServiceLocator.ConstructOrResolve' }
               elseif ($impls.Count -gt 0) { 'impl found but no public parameterless ctor' }
               else { 'no concrete impl found - likely not reachable headless' }

    $members = @()
    if ($IncludeMembers) {
        try {
            $members = @($iface.Type.GetMethods() | Select-Object -First 12 | ForEach-Object {
                $ps = ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
                "$($_.ReturnType.Name) $($_.Name)($ps)"
            })
        } catch { $members = @() }
    }

    [PSCustomObject]@{
        Assembly        = $iface.Assembly
        Interface       = $iface.Name
        FullName        = $iface.FullName
        ImplementsIGx   = $isGx
        Implementations = $impls
        Constructible   = $constructible.Count
        Verdict         = $verdict
        Members         = $members
    }
}

$results = @($results | Sort-Object @{ Expression = {
    switch -Regex ($_.Verdict) {
        '^SdkServiceResolver'  { 0 }
        '^SdkServiceLocator'   { 1 }
        '^impl found'          { 2 }
        default                { 3 }
    } } }, Assembly, Interface)

$commands = @($commands | Sort-Object Assembly, Namespace, Name)

$report = [PSCustomObject]@{
    InstallPath       = $gxPath
    AssembliesScanned = $scanned
    Pattern           = $Pattern
    Total             = $results.Count
    Reachable         = @($results | Where-Object { $_.Verdict -like 'SdkService*' }).Count
    Services          = $results
    CommandClasses    = $commands
}

if ($Json) {
    $report | ConvertTo-Json -Depth 6
    return
}

Write-Host ''
Write-Host '=== SDK service reachability ===' -ForegroundColor Cyan
Write-Host "Install  : $gxPath"
Write-Host "Scanned  : $($scanned) assemblies   Pattern: $Pattern"
Write-Host "Found    : $($results.Count) service-shaped interfaces, $($report.Reachable) with a known entry point"
Write-Host ''

$lastVerdict = $null
foreach ($r in $results) {
    if ($r.Verdict -ne $lastVerdict) {
        $color = switch -Regex ($r.Verdict) {
            '^SdkServiceResolver' { 'Green' }
            '^SdkServiceLocator'  { 'Cyan' }
            '^impl found'         { 'DarkYellow' }
            default               { 'DarkGray' }
        }
        Write-Host ''
        Write-Host "--- $($r.Verdict) ---" -ForegroundColor $color
        $lastVerdict = $r.Verdict
    }
    Write-Host ("  {0,-34} {1}" -f $r.Interface, $r.Assembly)
    foreach ($i in $r.Implementations) {
        $ctor = if ($i.PublicCtor) { 'public ctor()' } else { 'NO public ctor()' }
        Write-Host ("      impl: {0}  [{1}]" -f $i.FullName, $ctor) -ForegroundColor DarkGray
    }
    foreach ($m in $r.Members) {
        Write-Host ("      . {0}" -f $m) -ForegroundColor DarkGray
    }
}

if ($CommandClasses) {
    Write-Host ''
    Write-Host "--- Command-shaped classes with a public constructor ($($commands.Count)) ---" -ForegroundColor Yellow
    Write-Host '    A second entry-point family: concrete *Command classes, invoked directly' -ForegroundColor DarkGray
    Write-Host '    rather than resolved from the service registry.' -ForegroundColor DarkGray
    $lastNs = $null
    foreach ($c in $commands) {
        if ($c.Namespace -ne $lastNs) {
            Write-Host ''
            Write-Host ("  [{0}] {1}" -f $c.Assembly, $c.Namespace) -ForegroundColor Cyan
            $lastNs = $c.Namespace
        }
        Write-Host ("    {0}" -f $c.Name)
        foreach ($ct in $c.Ctors) { Write-Host ("        ctor {0}" -f $ct) -ForegroundColor DarkGray }
    }
}

Write-Host ''
