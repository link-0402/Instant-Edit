[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BlenderPath = '',
    [switch]$SkipPlugin,
    [switch]$SkipBlender
)

$ErrorActionPreference = 'Stop'

$scriptRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Definition
}
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($ArgumentList -join ' ')"
    }
}

function Assert-DalamudPackage {
    param([string]$PackagePath)

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Dalamud release package not found: $PackagePath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $requiredEntries = @(
        'InstantEdit.dll',
        'InstantEdit.json',
        'InstantEdit.deps.json',
        'InstantEdit.pdb',
        'Penumbra.Api.dll'
    )
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        $missing = @($requiredEntries | Where-Object { $_ -notin $entryNames })
        if ($missing.Count -gt 0) {
            throw "Dalamud package is missing required entries: $($missing -join ', ')"
        }

        $stale = @($entryNames | Where-Object { $_ -match '(^|/)net10\.0(-windows)?(/|$)' })
        if ($stale.Count -gt 0) {
            throw "Dalamud package contains stale target-framework entries: $($stale -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "Verified Dalamud package: $PackagePath"
}

$pluginProject = Join-Path $repoRoot 'Dalamud-Plugin\InstantEdit.csproj'
$pluginPackage = Join-Path $repoRoot 'Dalamud-Plugin\InstantEdit\latest.zip'
$blenderRepository = Join-Path $repoRoot 'Blender-Addon\blender_repo'
$blenderManifest = Join-Path $repoRoot 'Blender-Addon\blender_manifest.toml'

if (-not $SkipPlugin) {
    Invoke-Checked 'dotnet' @('build', $pluginProject, '-c', $Configuration)
    Assert-DalamudPackage $pluginPackage
}

if (-not $SkipBlender) {
    $generateScript = Join-Path $scriptRoot 'generate-blender-repository.ps1'
    $generateParameters = @{ RepositoryPath = $blenderRepository }
    if (-not [string]::IsNullOrWhiteSpace($BlenderPath)) {
        $generateParameters.BlenderPath = $BlenderPath
    }
    & $generateScript @generateParameters
    if ($LASTEXITCODE -ne 0) {
        throw "Blender repository generation failed with exit code $LASTEXITCODE."
    }
    & (Join-Path $scriptRoot 'verify-blender-repository.ps1') `
        -RepositoryPath $blenderRepository `
        -ExpectedMinimum '4.5.3'
}

if (-not (Test-Path -LiteralPath $blenderManifest -PathType Leaf)) {
    throw "Blender manifest not found: $blenderManifest"
}

Write-Host 'Release preparation completed. Review git diff, commit, push, and tag separately.'
