#Requires -Version 7.0
# Publishes RefusionRelay as a self-contained single-file executable for the selected target.
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Target = 'linux-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet was not found on PATH.'
    }

    $project = Join-Path $repoRoot 'RefusionRelay\RefusionRelay.csproj'
    $outputDir = Join-Path $repoRoot "RefusionRelay\bin\publish\$Target"

    Write-Host "Publishing RefusionRelay ($Target, self-contained single file)..."
    & dotnet publish $project -c Release -r $Target --self-contained true -p:PublishSingleFile=true -o $outputDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE`: $project" }

    $binaryName = if ($Target -like 'win-*') { 'RefusionRelay.exe' } else { 'RefusionRelay' }
    $binaryPath = Join-Path $outputDir $binaryName
    if (-not (Test-Path -LiteralPath $binaryPath)) { throw "Publish output not found: $binaryPath" }

    $binary = Get-Item -LiteralPath $binaryPath
    $hash = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash

    Write-Host ''
    Write-Host "Published RefusionRelay ($Target, $($binary.Length) bytes)"
    Write-Host "  dir:    $outputDir"
    Write-Host "  sha256: $hash"
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
