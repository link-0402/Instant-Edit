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

function Assert-BlenderRepository {
    param(
        [string]$RepositoryPath,
        [string]$ExpectedMinimum
    )

    $archivePath = Join-Path $RepositoryPath 'XIV-Instant-Edit.zip'
    $indexPath = Join-Path $RepositoryPath 'index.json'
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Blender extension package not found: $archivePath"
    }
    if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
        throw "Blender repository index not found: $indexPath"
    }

    $entry = (Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json).data |
        Where-Object { $_.id -eq 'xiv_instant_edit' } |
        Select-Object -First 1
    if ($null -eq $entry) {
        throw 'Blender repository index does not contain xiv_instant_edit.'
    }
    if ($entry.blender_version_min -ne $ExpectedMinimum) {
        throw "Blender minimum mismatch: expected $ExpectedMinimum, found $($entry.blender_version_min)."
    }

    $archive = Get-Item -LiteralPath $archivePath
    if ([int64]$entry.archive_size -ne $archive.Length) {
        throw "Blender archive size mismatch: index=$($entry.archive_size), actual=$($archive.Length)."
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (([string]$entry.archive_hash).ToLowerInvariant() -ne "sha256:$actualHash") {
        throw "Blender archive hash mismatch: index=$($entry.archive_hash), actual=sha256:$actualHash."
    }

    Write-Host "Verified Blender repository: $($archive.Length) bytes, sha256:$actualHash"
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
    Assert-BlenderRepository $blenderRepository '4.2.0'
}

if (-not (Test-Path -LiteralPath $blenderManifest -PathType Leaf)) {
    throw "Blender manifest not found: $blenderManifest"
}

Write-Host 'Release preparation completed. Review git diff, commit, push, and tag separately.'
