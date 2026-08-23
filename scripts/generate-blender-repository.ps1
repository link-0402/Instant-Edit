[CmdletBinding()]
param(
    [string]$RepositoryPath = ''
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

$blender = Get-Command blender -ErrorAction SilentlyContinue
if ($null -eq $blender) {
    throw 'Blender 4.2 or newer is required on PATH to generate the extension repository.'
}

New-Item -ItemType Directory -Path $resolvedRepository -Force | Out-Null
& $packageScript -OutputPath $packagePath
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Blender package creation did not produce the expected archive: $packagePath"
}

& $blender.Source --background --command extension server-generate "--repo-dir=$resolvedRepository" --html
if ($LASTEXITCODE -ne 0) {
    throw "Blender extension repository generation failed with exit code $LASTEXITCODE."
}

Write-Host "Generated Blender extension repository at $resolvedRepository"
