[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryPath,
    [string]$ExpectedMinimum = '4.5.3'
)

$ErrorActionPreference = 'Stop'
$resolvedRepository = (Resolve-Path -LiteralPath $RepositoryPath).Path
$archivePath = Join-Path $resolvedRepository 'XIV-Instant-Edit.zip'
$indexPath = Join-Path $resolvedRepository 'index.json'
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
