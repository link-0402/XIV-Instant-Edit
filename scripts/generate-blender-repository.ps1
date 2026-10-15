[CmdletBinding()]
param(
    [string]$RepositoryPath = '',
    [string]$BlenderPath = '',
    # Absolute URL the package is served from (a GitHub release asset). Empty keeps the
    # relative ./ URL Blender writes, for a repository folder served together with its ZIP.
    [string]$ArchiveUrl = ''
)

$ErrorActionPreference = 'Stop'

$scriptRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Definition
}
if ([string]::IsNullOrWhiteSpace($RepositoryPath)) {
    $RepositoryPath = Join-Path $scriptRoot '..\dist\blender_repo'
}

$packageName = 'XIV-Instant-Edit-Blender.zip'
$resolvedRepository = [System.IO.Path]::GetFullPath($RepositoryPath)
$packageScript = Join-Path $scriptRoot 'package-blender-addon.ps1'
$packagePath = Join-Path $resolvedRepository $packageName

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
# server-generate lists every ZIP in the folder, so a leftover one would join the index.
$strayArchives = @(Get-ChildItem -LiteralPath $resolvedRepository -Filter '*.zip' -File |
    Where-Object { $_.Name -ne $packageName })
if ($strayArchives.Count -gt 0) {
    throw "Remove the other ZIP files from ${resolvedRepository}: $($strayArchives.Name -join ', ')"
}

& $packageScript -OutputPath $packagePath
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Blender package creation did not produce the expected archive: $packagePath"
}

& $blenderExecutable --background --command extension server-generate "--repo-dir=$resolvedRepository" --html
if ($LASTEXITCODE -ne 0) {
    throw "Blender extension repository generation failed with exit code $LASTEXITCODE."
}

if (-not [string]::IsNullOrWhiteSpace($ArchiveUrl)) {
    if ($ArchiveUrl -notmatch '^https://') {
        throw "ArchiveUrl must be an absolute https URL: $ArchiveUrl"
    }
    # Edit the text rather than round-tripping the JSON, so the file keeps Blender's layout.
    $indexPath = Join-Path $resolvedRepository 'index.json'
    $relativeUrl = '"archive_url": "./' + $packageName + '"'
    $indexText = [System.IO.File]::ReadAllText($indexPath)
    $occurrences = ([regex]::Matches($indexText, [regex]::Escape($relativeUrl))).Count
    if ($occurrences -ne 1) {
        throw "Expected one $relativeUrl in $indexPath, found $occurrences."
    }
    $indexText = $indexText.Replace($relativeUrl, '"archive_url": "' + $ArchiveUrl + '"')
    [System.IO.File]::WriteAllText($indexPath, $indexText, [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Generated Blender extension repository at $resolvedRepository"
