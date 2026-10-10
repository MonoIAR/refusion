#Requires -Version 7.0
# Builds Refusion in Debug and deploys LabFusion.dll into the local BONELAB install.
[CmdletBinding()]
param(
    [string]$BonelabDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

function Get-SteamRoots {
    $roots = [System.Collections.Generic.List[string]]::new()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if (-not $props) { continue }
        foreach ($name in 'SteamPath', 'InstallPath') {
            $value = $props.$name
            if ($value) { $roots.Add($value.Replace('/', '\')) }
        }
    }

    $libraries = [System.Collections.Generic.List[string]]::new()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        if ($libraries -notcontains $root) { $libraries.Add($root) }

        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf)) { continue }
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $library = $match.Groups[1].Value -replace '\\\\', '\' -replace '/', '\'
            if ((Test-Path -LiteralPath $library) -and ($libraries -notcontains $library)) { $libraries.Add($library) }
        }
    }
    $libraries
}

function Get-BonelabDirCandidates {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:BONELAB_DIR) { $candidates.Add($env:BONELAB_DIR) }

    $persisted = (Get-ItemProperty -Path 'HKCU:\Environment' -Name BONELAB_DIR -ErrorAction SilentlyContinue).BONELAB_DIR
    if ($persisted) { $candidates.Add($persisted) }

    foreach ($steamRoot in Get-SteamRoots) {
        $candidates.Add((Join-Path $steamRoot 'steamapps\common\BONELAB'))
    }
    $candidates
}

function Invoke-Build {
    param([string]$Project, [string]$Configuration)
    Write-Host "Building $(Split-Path -Leaf $Project) ($Configuration)..."
    & dotnet build $Project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE`: $Project" }
}

try {
    if ($BonelabDir -and -not (Test-Path -LiteralPath $BonelabDir)) {
        throw "BONELAB directory not found: $BonelabDir"
    }

    $gameDir = $BonelabDir
    if (-not $gameDir) {
        $gameDir = Get-BonelabDirCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $gameDir) {
        throw 'Could not locate the BONELAB install. Set the BONELAB_DIR environment variable or pass -BonelabDir.'
    }
    $gameDir = (Resolve-Path -LiteralPath $gameDir).Path
    Write-Host "Game directory: $gameDir"

    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'BONELAB*' })
    if ($running.Count -gt 0) {
        throw "BONELAB is running (PID $($running[0].Id)); close the game before deploying."
    }

    if (-not $SkipBuild) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'dotnet was not found on PATH.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $gameDir 'MelonLoader\Il2CppAssemblies'))) {
            throw "MelonLoader Il2CppAssemblies not found in $gameDir; launch the game once with MelonLoader installed, then retry."
        }

        $env:BONELAB_DIR = $gameDir

        # Build support first, then stage its DLL and rebuild LabFusion to embed the fresh one.
        # Explicit staging, because the csproj xcopy only compares timestamps.
        Invoke-Build (Join-Path $repoRoot 'BonelabSupport\BonelabSupport.csproj') 'Debug'

        $supportDll = Join-Path $repoRoot 'BonelabSupport\bin\Debug\net6.0\BonelabSupport.dll'
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

        Invoke-Build (Join-Path $repoRoot 'LabFusion\LabFusion.csproj') 'Debug'
    }

    $modDll = Join-Path $repoRoot 'LabFusion\bin\Debug\net6.0\LabFusion.dll'
    if (-not (Test-Path -LiteralPath $modDll)) {
        throw "Build output not found: $modDll (run without -SkipBuild?)"
    }

    $modsDir = Join-Path $gameDir 'Mods'
    if (-not (Test-Path -LiteralPath $modsDir)) { New-Item -ItemType Directory -Path $modsDir | Out-Null }
    $target = Join-Path $modsDir 'LabFusion.dll'
    Copy-Item -LiteralPath $modDll -Destination $target -Force

    $version = [regex]::Match((Get-Content -LiteralPath (Join-Path $repoRoot 'LabFusion\src\Mod.cs') -Raw), 'VersionString\s*=\s*"([^"]+)"').Groups[1].Value
    $deployed = Get-Item -LiteralPath $target
    Write-Host ''
    Write-Host "Deployed LabFusion $version (Debug, $($deployed.Length) bytes)"
    Write-Host "  $modDll"
    Write-Host "  -> $target"
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
