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

function Get-ProjectChangelog {
    param([string]$ProjectPath)

    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
        throw "Dalamud project not found: $ProjectPath"
    }

    try {
        [xml]$project = Get-Content -Raw -LiteralPath $ProjectPath
    }
    catch {
        throw "Could not parse Dalamud project metadata: $ProjectPath. $($_.Exception.Message)"
    }

    $node = $project.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Changelog']")
    $changelog = if ($null -eq $node) { '' } else { [string]$node.InnerText }
    if ([string]::IsNullOrWhiteSpace($changelog)) {
        throw "Dalamud project changelog is missing or empty: $ProjectPath"
    }

    return $changelog
}

function Assert-RepositoryChangelog {
    param(
        [string]$ManifestPath,
        [string]$ExpectedChangelog
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Dalamud repository manifest not found: $ManifestPath"
    }

    try {
        $manifests = @(Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json)
    }
    catch {
        throw "Dalamud repository manifest is not valid JSON: $ManifestPath. $($_.Exception.Message)"
    }

    $matches = @($manifests | Where-Object { $_.InternalName -eq 'InstantEdit' })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one InstantEdit manifest in: $ManifestPath"
    }

    $actualChangelog = [string]$matches[0].Changelog
    if ([string]::IsNullOrWhiteSpace($actualChangelog)) {
        throw "Dalamud repository manifest changelog is missing or empty: $ManifestPath"
    }
    if ($actualChangelog -cne $ExpectedChangelog) {
        throw "Changelog mismatch between project metadata and repository manifest: $ManifestPath"
    }

    Write-Host "Verified repository changelog: $ManifestPath"
}

function Assert-DalamudPackage {
    param(
        [string]$PackagePath,
        [string]$ExpectedChangelog
    )

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

        $manifestEntry = $archive.GetEntry('InstantEdit.json')
        if ($null -eq $manifestEntry) {
            throw "Dalamud package is missing its root manifest: InstantEdit.json"
        }

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try {
            try {
                $manifest = $reader.ReadToEnd() | ConvertFrom-Json
            }
            catch {
                throw "Dalamud package manifest is not valid JSON: InstantEdit.json. $($_.Exception.Message)"
            }
        }
        finally {
            $reader.Dispose()
        }

        $actualChangelog = [string]$manifest.Changelog
        if ([string]::IsNullOrWhiteSpace($actualChangelog)) {
            throw "Dalamud package manifest changelog is missing or empty: InstantEdit.json"
        }
        if ($actualChangelog -cne $ExpectedChangelog) {
            throw "Changelog mismatch between project metadata and packaged manifest: $PackagePath"
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "Verified Dalamud package: $PackagePath"
}

$pluginProject = Join-Path $repoRoot 'Dalamud-Plugin\InstantEdit.csproj'
$pluginRepositoryManifest = Join-Path $repoRoot 'Dalamud-Plugin\repo.json'
$pluginPackage = Join-Path $repoRoot 'Dalamud-Plugin\InstantEdit\latest.zip'
$blenderRepository = Join-Path $repoRoot 'Blender-Addon\blender_repo'
$blenderManifest = Join-Path $repoRoot 'Blender-Addon\blender_manifest.toml'
$expectedChangelog = Get-ProjectChangelog $pluginProject
Assert-RepositoryChangelog $pluginRepositoryManifest $expectedChangelog

if (-not $SkipPlugin) {
    Invoke-Checked 'dotnet' @('build', $pluginProject, '-c', $Configuration)
    Assert-DalamudPackage $pluginPackage $expectedChangelog
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
