#Requires -Version 7.0
# Builds Refusion in Release and packs the Staging\Thunderstore folder into a store-only zip.
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$stagingRoot = Join-Path $repoRoot 'Staging\Thunderstore'

function Invoke-Build {
    param([string]$Project, [string]$Configuration)
    Write-Host "Building $(Split-Path -Leaf $Project) ($Configuration)..."
    & dotnet build $Project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE`: $Project" }
}

function Get-FusionVersion {
    $modSource = Get-Content -LiteralPath (Join-Path $repoRoot 'LabFusion\src\Mod.cs') -Raw
    $versions = @([regex]::Matches($modSource, 'VersionString\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($versions.Count -ne 1) {
        throw "src/Mod.cs has inconsistent or missing VersionString values: $($versions -join ', ')"
    }
    $versions[0]
}

function Sync-ManifestVersion {
    param([string]$Path, [string]$Version)
    $raw = [System.IO.File]::ReadAllText($Path)
    $pattern = '("version_number"\s*:\s*)"[^"]*"'
    if (-not [regex]::IsMatch($raw, $pattern)) {
        throw "version_number not found in $Path"
    }

    $updated = $raw -replace $pattern, ('${1}"' + $Version + '"')
    if ($updated -eq $raw) {
        Write-Host "manifest.json: version_number is already $Version"
        return
    }

    [System.IO.File]::WriteAllText($Path, $updated, [System.Text.UTF8Encoding]::new($false))
    Write-Host "manifest.json: version_number updated to $Version"
}

function New-StoreZip {
    param([string]$ZipPath, [string]$SourceDir)
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

    $parent = Split-Path -Parent $ZipPath
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }

    $sourceRoot = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $SourceDir -Recurse -File | Sort-Object FullName) {
            $entryName = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::NoCompression)
            $reader = [System.IO.File]::OpenRead($file.FullName)
            try {
                $writer = $entry.Open()
                try { $reader.CopyTo($writer) } finally { $writer.Dispose() }
            } finally { $reader.Dispose() }
            Write-Host "  + $entryName"
        }
    } finally {
        $archive.Dispose()
    }
}

try {
    if (-not $SkipBuild) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'dotnet was not found on PATH.'
        }
        if (-not $env:BONELAB_DIR -or -not (Test-Path -LiteralPath $env:BONELAB_DIR)) {
            throw 'BONELAB_DIR is not set to a valid directory; it is required to build the mod.'
        }

        # Build support first, then stage its DLL and rebuild LabFusion to embed the fresh one.
        # Explicit staging, because the csproj xcopy only compares timestamps.
        Invoke-Build (Join-Path $repoRoot 'BonelabSupport\BonelabSupport.csproj') 'Release'

        $supportDll = Join-Path $repoRoot 'BonelabSupport\bin\Release\net6.0\BonelabSupport.dll'
        $stagedSupport = Join-Path $repoRoot 'LabFusion\SupportModules\BonelabSupport.dll'
        $supportChanged = -not (Test-Path -LiteralPath $stagedSupport)
        if (-not $supportChanged) {
            $supportChanged = (Get-FileHash -LiteralPath $supportDll).Hash -ne (Get-FileHash -LiteralPath $stagedSupport).Hash
        }
        if ($supportChanged) {
            Copy-Item -LiteralPath $supportDll -Destination $stagedSupport -Force
            # Copies keep the source timestamp; bump it so LabFusion's incremental build re-embeds the DLL.
            (Get-Item -LiteralPath $stagedSupport).LastWriteTime = Get-Date
        }

        Invoke-Build (Join-Path $repoRoot 'LabFusion\LabFusion.csproj') 'Release'
    }

    $version = Get-FusionVersion
    Write-Host "Fusion version: $version"

    Sync-ManifestVersion -Path (Join-Path $stagingRoot 'manifest.json') -Version $version

    $releaseDll = Join-Path $repoRoot 'LabFusion\bin\Release\net6.0\LabFusion.dll'
    if (-not (Test-Path -LiteralPath $releaseDll)) {
        throw "Release build not found: $releaseDll (run without -SkipBuild?)"
    }

    $stagedDll = Join-Path $stagingRoot 'Mods\LabFusion.dll'
    $stagedDir = Split-Path -Parent $stagedDll
    if (-not (Test-Path -LiteralPath $stagedDir)) { New-Item -ItemType Directory -Path $stagedDir | Out-Null }
    Copy-Item -LiteralPath $releaseDll -Destination $stagedDll -Force

    if (-not $ZipPath) { $ZipPath = Join-Path $repoRoot "Staging\Refusion-$version.zip" }
    $ZipPath = [System.IO.Path]::GetFullPath($ZipPath)

    Write-Host "Packing $stagingRoot"
    New-StoreZip -ZipPath $ZipPath -SourceDir $stagingRoot

    $zipFile = Get-Item -LiteralPath $ZipPath
    $hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host "Packaged Refusion $version"
    Write-Host "  zip:    $ZipPath"
    Write-Host "  size:   $($zipFile.Length) bytes"
    Write-Host "  sha256: $hash"
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
