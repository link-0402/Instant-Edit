[CmdletBinding()]
param(
    [string]$RepositoryPath = '',
    [string]$BlenderPath = ''
)

$ErrorActionPreference = 'Stop'

$scriptRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Definition
}
if ([string]::IsNullOrWhiteSpace($RepositoryPath)) {
    $RepositoryPath = Join-Path $scriptRoot '..\Blender-Addon\blender_repo'
}

$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
$resolvedRepository = [System.IO.Path]::GetFullPath($RepositoryPath)
$packageScript = Join-Path $scriptRoot 'package-blender-addon.ps1'
$packagePath = Join-Path $resolvedRepository 'XIV-Instant-Edit.zip'

$blenderCommand = if ([string]::IsNullOrWhiteSpace($BlenderPath)) {
    Get-Command blender -ErrorAction SilentlyContinue
} else {
    $resolvedBlenderPath = [System.IO.Path]::GetFullPath($BlenderPath)
    if (-not (Test-Path -LiteralPath $resolvedBlenderPath -PathType Leaf)) {
        throw "Blender executable not found: $resolvedBlenderPath"
    }
    Get-Item -LiteralPath $resolvedBlenderPath
}
if ($null -eq $blenderCommand) {
    throw 'Blender 4.2 or newer is required on PATH, or pass -BlenderPath.'
}
$blenderExecutable = if ($blenderCommand.PSObject.Properties.Name -contains 'Source') {
    $blenderCommand.Source
} else {
    $blenderCommand.FullName
}

New-Item -ItemType Directory -Path $resolvedRepository -Force | Out-Null
& $packageScript -OutputPath $packagePath
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Blender package creation did not produce the expected archive: $packagePath"
}

& $blenderExecutable --background --command extension server-generate "--repo-dir=$resolvedRepository" --html
if ($LASTEXITCODE -ne 0) {
    throw "Blender extension repository generation failed with exit code $LASTEXITCODE."
}

Write-Host "Generated Blender extension repository at $resolvedRepository"
