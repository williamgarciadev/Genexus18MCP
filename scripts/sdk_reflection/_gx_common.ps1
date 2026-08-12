<#
.SYNOPSIS
    Shared helpers for the GeneXus SDK inspection scripts. Dot-source it:
        . (Join-Path $PSScriptRoot '_gx_common.ps1')

.DESCRIPTION
    Read-only. Defines functions only -- dot-sourcing this file runs no work.

    Get-PeInfo parses the PE/CLI headers by hand instead of using reflection because
    ReflectionOnlyLoadFrom is .NET Framework only (PowerShell 7 throws
    PlatformNotSupportedException) and because parsing never *loads* the assembly,
    which makes classifying hundreds of DLLs cheap and side-effect free.
#>

# COR20 header Flags bits (ECMA-335 II.25.3.3.1 COMIMAGE_FLAGS_*).
$script:COMIMAGE_FLAGS_ILONLY            = 0x00000001
$script:COMIMAGE_FLAGS_32BITREQUIRED     = 0x00000002
$script:COMIMAGE_FLAGS_STRONGNAMESIGNED  = 0x00000008
$script:COMIMAGE_FLAGS_NATIVE_ENTRYPOINT = 0x00000010
$script:COMIMAGE_FLAGS_32BITPREFERRED    = 0x00020000

function Resolve-GxPath {
    <#
        Resolves the GeneXus install directory and reports WHICH source won, mirroring
        DoctorService.BuildGxBlock's `source` field. Order: GX_PROGRAM_DIR (what the
        worker reads at runtime) -> GX_PATH (build-time) -> config.json -> default.
    #>
    [CmdletBinding()]
    param([string]$ScriptRoot = $PSScriptRoot)

    $candidates = @(
        @{ Source = 'GX_PROGRAM_DIR'; Value = $env:GX_PROGRAM_DIR }
        @{ Source = 'GX_PATH';        Value = $env:GX_PATH }
    )

    $configPath = Join-Path $ScriptRoot '..\..\src\nexus-ide\backend\config.json'
    if (Test-Path $configPath) {
        try {
            $config = Get-Content $configPath -Raw | ConvertFrom-Json
            if ($config.PSObject.Properties.Name -contains 'InstallationPath') {
                $candidates += @{ Source = 'config.json'; Value = $config.InstallationPath }
            }
        } catch {
            Write-Verbose "config.json unreadable: $($_.Exception.Message)"
        }
    }

    $candidates += @{ Source = 'default'; Value = 'C:\Program Files (x86)\GeneXus\GeneXus18' }

    foreach ($c in $candidates) {
        if ($c.Value -and (Test-Path -LiteralPath $c.Value -PathType Container)) {
            return [PSCustomObject]@{ Path = $c.Value; Source = $c.Source }
        }
    }

    throw "GeneXus install directory not found. Tried: $(($candidates | ForEach-Object { $_.Source }) -join ', ')."
}

function Get-PeInfo {
    <#
        Returns a PSCustomObject describing a PE image, or $null when the file is not a
        valid PE. IsManaged is decided by data directory 14 (the CLI header) being present
        and non-empty -- the same test the CLR loader makes.

        -HeaderOnly stops right after the CLI directory entry, which is all a
        managed-vs-native verdict needs. The full pass also resolves the CLI header and
        metadata root, which requires walking the section table to map RVAs to offsets.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [switch]$HeaderOnly
    )

    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Open,
                                         [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $reader = New-Object System.IO.BinaryReader($stream)

        if ($stream.Length -lt 0x40) { return $null }

        # DOS header: 'MZ' magic; e_lfanew at 0x3C points at the PE signature.
        if ($reader.ReadUInt16() -ne 0x5A4D) { return $null }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -le 0 -or ($peOffset + 24) -ge $stream.Length) { return $null }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return $null }   # 'PE\0\0'

        # COFF file header.
        $machine              = $reader.ReadUInt16()
        $numberOfSections     = $reader.ReadUInt16()
        $null                 = $reader.ReadUInt32()   # TimeDateStamp
        $null                 = $reader.ReadUInt32()   # PointerToSymbolTable
        $null                 = $reader.ReadUInt32()   # NumberOfSymbols
        $sizeOfOptionalHeader = $reader.ReadUInt16()
        $null                 = $reader.ReadUInt16()   # Characteristics

        $optionalHeaderOffset = $stream.Position
        if ($sizeOfOptionalHeader -eq 0) { return $null }

        $magic = $reader.ReadUInt16()
        $isPe32Plus = ($magic -eq 0x20B)
        # Data directories start at +96 (PE32) or +112 (PE32+) from the optional header.
        $dataDirOffset = $optionalHeaderOffset + $(if ($isPe32Plus) { 112 } else { 96 })

        # Directory 14 == CLI header (COR20).
        $cliDirOffset = $dataDirOffset + (14 * 8)
        $cliRva = 0; $cliSize = 0
        if (($cliDirOffset + 8) -le $stream.Length) {
            $stream.Position = $cliDirOffset
            $cliRva  = $reader.ReadUInt32()
            $cliSize = $reader.ReadUInt32()
        }

        $info = [ordered]@{
            FilePath      = $FilePath
            FileName      = [System.IO.Path]::GetFileName($FilePath)
            Machine       = ('0x{0:X4}' -f $machine)
            MachineName   = switch ($machine) {
                0x014C  { 'i386' }
                0x8664  { 'x64' }
                0x01C4  { 'ARM' }
                0xAA64  { 'ARM64' }
                default { ('unknown (0x{0:X4})' -f $machine) }
            }
            OptionalMagic = ('0x{0:X3}' -f $magic)
            PeFormat      = $(if ($isPe32Plus) { 'PE32+' } else { 'PE32' })
            IsManaged     = ($cliRva -ne 0 -and $cliSize -ne 0)
            CliHeaderRva  = $cliRva
            CliHeaderSize = $cliSize
        }

        if (-not $info.IsManaged -or $HeaderOnly) {
            return [PSCustomObject]$info
        }

        # Section table -> RVA-to-file-offset translation.
        $sections = @()
        $stream.Position = $peOffset + 24 + $sizeOfOptionalHeader
        for ($i = 0; $i -lt $numberOfSections; $i++) {
            if (($stream.Position + 40) -gt $stream.Length) { break }
            $null           = $reader.ReadBytes(8)     # Name
            $virtualSize    = $reader.ReadUInt32()
            $virtualAddress = $reader.ReadUInt32()
            $sizeOfRawData  = $reader.ReadUInt32()
            $pointerToRaw   = $reader.ReadUInt32()
            $null           = $reader.ReadBytes(16)    # relocations / line numbers / counts
            $sections += [PSCustomObject]@{
                VirtualAddress = $virtualAddress
                VirtualSize    = $virtualSize
                SizeOfRawData  = $sizeOfRawData
                PointerToRaw   = $pointerToRaw
            }
        }

        function Convert-RvaToOffset([uint32]$rva) {
            foreach ($s in $sections) {
                # Use the larger of virtual/raw size: a section can be zero-padded either way.
                $span = [Math]::Max($s.VirtualSize, $s.SizeOfRawData)
                if ($rva -ge $s.VirtualAddress -and $rva -lt ($s.VirtualAddress + $span)) {
                    return [int64]($rva - $s.VirtualAddress + $s.PointerToRaw)
                }
            }
            return [int64](-1)
        }

        $cliOffset = Convert-RvaToOffset $cliRva
        if ($cliOffset -lt 0 -or ($cliOffset + 72) -gt $stream.Length) {
            return [PSCustomObject]$info
        }

        # CLI header (ECMA-335 II.25.3.3).
        $stream.Position = $cliOffset
        $null        = $reader.ReadUInt32()   # Cb
        $rtMajor     = $reader.ReadUInt16()
        $rtMinor     = $reader.ReadUInt16()
        $metadataRva = $reader.ReadUInt32()
        $null        = $reader.ReadUInt32()   # Metadata size
        $corFlags    = $reader.ReadUInt32()

        $info.CliRuntimeVersion = "$rtMajor.$rtMinor"
        $info.Cor20Flags        = ('0x{0:X8}' -f $corFlags)
        $info.IlOnly            = (($corFlags -band $script:COMIMAGE_FLAGS_ILONLY) -ne 0)
        $info.Requires32Bit     = (($corFlags -band $script:COMIMAGE_FLAGS_32BITREQUIRED) -ne 0)
        $info.Prefers32Bit      = (($corFlags -band $script:COMIMAGE_FLAGS_32BITPREFERRED) -ne 0)
        $info.StrongNameSigned  = (($corFlags -band $script:COMIMAGE_FLAGS_STRONGNAMESIGNED) -ne 0)
        $info.NativeEntryPoint  = (($corFlags -band $script:COMIMAGE_FLAGS_NATIVE_ENTRYPOINT) -ne 0)

        # 32BITREQUIRED alone means x86-only. Set TOGETHER with 32BITPREFERRED it means
        # "AnyCPU, 32-bit preferred" (the .NET 4.5+ encoding of the "Prefer 32-bit" build
        # option) -- an MSIL image that merely *runs* in a 32-bit process. Reading only the
        # 32BITREQUIRED bit and calling the image x86-only is a classic misreading.
        $info.Platform = if (-not $info.Requires32Bit) { 'AnyCPU (64-bit capable)' }
                         elseif ($info.Prefers32Bit)   { 'AnyCPU, 32-bit preferred' }
                         else                          { 'x86 (32-bit required)' }

        # Metadata root: 'BSJB' signature, then the runtime version string.
        $metadataOffset = Convert-RvaToOffset $metadataRva
        if ($metadataOffset -ge 0 -and ($metadataOffset + 20) -le $stream.Length) {
            $stream.Position = $metadataOffset
            if ($reader.ReadUInt32() -eq 0x424A5342) {   # 'BSJB'
                $info.MetadataSignature = 'BSJB'
                $mdMajor    = $reader.ReadUInt16()
                $mdMinor    = $reader.ReadUInt16()
                $null       = $reader.ReadUInt32()       # Reserved
                $versionLen = $reader.ReadInt32()
                if ($versionLen -gt 0 -and $versionLen -le 256) {
                    $bytes = $reader.ReadBytes($versionLen)
                    $info.RuntimeVersion = ([System.Text.Encoding]::UTF8.GetString($bytes)).TrimEnd([char]0)
                }
                $info.MetadataVersion = "$mdMajor.$mdMinor"
            }
        }

        return [PSCustomObject]$info
    } catch {
        Write-Verbose "PE parse failed for ${FilePath}: $($_.Exception.Message)"
        return $null
    } finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
    }
}

$script:ReflectionOnlyReady = $false

function Initialize-ReflectionOnly {
    <#
        Installs a ReflectionOnlyAssemblyResolve hook that probes the GeneXus install root
        and Packages\. Without it, GetTypes() on a reflection-only assembly throws
        ReflectionTypeLoadException for nearly every dependency and any type census
        collapses to noise.

        Sets $script:ReflectionOnlyReady. Reflection-only load is .NET Framework only, so
        on PowerShell 7 this reports false and callers must degrade explicitly.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GxPath)

    if ($script:ReflectionOnlyReady) { return }
    try {
        $handler = [System.ResolveEventHandler] {
            param($sender, $e)
            $simple = ($e.Name -split ',')[0].Trim()
            foreach ($dir in @($GxPath, (Join-Path $GxPath 'Packages'))) {
                $candidate = Join-Path $dir "$simple.dll"
                if (Test-Path -LiteralPath $candidate) {
                    return [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($candidate)
                }
            }
            try { return [System.Reflection.Assembly]::ReflectionOnlyLoad($e.Name) } catch { return $null }
        }
        [System.AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($handler)
        $script:ReflectionOnlyReady = $true
    } catch {
        Write-Verbose "ReflectionOnly resolve hook unavailable: $($_.Exception.Message)"
    }
}

function Test-ReflectionOnlyReady { return $script:ReflectionOnlyReady }

function Get-PublicTypesSafe {
    <#
        Public types of a reflection-only assembly, tolerating partial type loads the same
        way SdkSurfaceProbe does. Returns an empty array rather than throwing.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FilePath)

    if (-not $script:ReflectionOnlyReady) { return @() }
    try {
        $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($FilePath)
        try {
            return @($asm.GetTypes() | Where-Object { $_.IsPublic })
        } catch [System.Reflection.ReflectionTypeLoadException] {
            return @($_.Exception.Types | Where-Object { $_ -ne $null -and $_.IsPublic })
        }
    } catch {
        Write-Verbose "type load failed for ${FilePath}: $($_.Exception.Message)"
        return @()
    }
}

function Get-AssemblyIdentity {
    <#
        Strong-name identity via GetAssemblyName. This reads metadata without loading the
        assembly into the AppDomain, and works on both PowerShell editions.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FilePath)

    try {
        $name = [System.Reflection.AssemblyName]::GetAssemblyName($FilePath)
        $tok = $name.GetPublicKeyToken()
        return [PSCustomObject]@{
            FullName              = $name.FullName
            Name                  = $name.Name
            Version               = $name.Version.ToString()
            PublicKeyToken        = $(if ($tok -and $tok.Length) { (($tok | ForEach-Object { '{0:x2}' -f $_ }) -join '') } else { $null })
            ProcessorArchitecture = $name.ProcessorArchitecture.ToString()
            Error                 = $null
        }
    } catch {
        return [PSCustomObject]@{
            FullName = $null; Name = $null; Version = $null; PublicKeyToken = $null
            ProcessorArchitecture = $null; Error = $_.Exception.GetType().Name
        }
    }
}
