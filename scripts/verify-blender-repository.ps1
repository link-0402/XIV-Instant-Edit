[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryPath,
    [string]$ExpectedMinimum = '4.5.0',
    # Checks this file instead of the one archive_url names (a release asset not uploaded yet).
    [string]$ArchivePath = ''
)

$ErrorActionPreference = 'Stop'
$resolvedRepository = (Resolve-Path -LiteralPath $RepositoryPath).Path
$indexPath = Join-Path $resolvedRepository 'index.json'
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

$archiveUrl = [string]$entry.archive_url
$downloadedArchive = $null
try {
    if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
        $archivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
    } elseif ($archiveUrl.StartsWith('./')) {
        $archivePath = Join-Path $resolvedRepository ([System.Uri]::UnescapeDataString($archiveUrl.Substring(2)))
    } elseif ($archiveUrl -match '^https://') {
        $downloadedArchive = Join-Path ([System.IO.Path]::GetTempPath()) ("xiv-ie-archive-" + [guid]::NewGuid() + '.zip')
        Invoke-WebRequest -Uri $archiveUrl -OutFile $downloadedArchive -UseBasicParsing
        $archivePath = $downloadedArchive
    } else {
        throw "Unsupported archive_url: $archiveUrl"
    }
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Blender extension package not found: $archivePath"
    }

    $archive = Get-Item -LiteralPath $archivePath
    if ([int64]$entry.archive_size -ne $archive.Length) {
        throw "Blender archive size mismatch: index=$($entry.archive_size), actual=$($archive.Length)."
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (([string]$entry.archive_hash).ToLowerInvariant() -ne "sha256:$actualHash") {
        throw "Blender archive hash mismatch: index=$($entry.archive_hash), actual=sha256:$actualHash."
    }
}
finally {
    if ($null -ne $downloadedArchive -and (Test-Path -LiteralPath $downloadedArchive)) {
        Remove-Item -LiteralPath $downloadedArchive -Force
    }
}

Write-Host "Verified Blender repository: $archiveUrl, $($archive.Length) bytes, sha256:$actualHash"
