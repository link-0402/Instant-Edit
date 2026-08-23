[CmdletBinding()]
param(
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'

$scriptRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Definition
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $scriptRoot '..\Blender-Addon\blender_repo\XIV-Instant-Edit.zip'
}

$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
$addonRoot = Join-Path $repoRoot 'Blender-Addon'
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)

if (-not (Test-Path -LiteralPath $addonRoot -PathType Container)) {
    throw "Blender add-on directory not found: $addonRoot"
}

$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Force
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = $null

try {
    $archive = [System.IO.Compression.ZipFile]::Open(
        $resolvedOutput,
        [System.IO.Compression.ZipArchiveMode]::Create)

    $files = Get-ChildItem -LiteralPath $addonRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/]__pycache__([\\/]|$)' -and
        $_.FullName -notmatch '[\\/]testing([\\/]|$)' -and
        $_.FullName -notmatch '[\\/]blender_repo([\\/]|$)' -and
        $_.Name -notmatch '\.py[cod]$' -and
        $_.Name -notmatch '^\.' -and
        $_.Extension -ne '.zip'
    }

    if ($null -eq $files -or @($files).Count -eq 0) {
        throw "No add-on files were found under: $addonRoot"
    }

    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($addonRoot.Length).TrimStart('\', '/')
        $relativePath = $relativePath.Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $file.FullName,
            $relativePath,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    if ($null -ne $archive) {
        $archive.Dispose()
    }
}

Write-Host "Created $resolvedOutput"
