[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BlenderPath = '',
    # The GitHub release tag the Blender package is attached to. Defaults to the add-on version.
    [string]$Tag = '',
    [switch]$SkipPlugin,
    [switch]$SkipBlender,
    # Package uncommitted add-on or plugin changes anyway (not for a real release).
    [switch]$AllowDirty
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
        'Penumbra.Api.dll',
        'NVorbis.dll'
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

function Get-ManifestVersion {
    param([string]$ManifestPath)

    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ($line -match '^\s*version\s*=\s*"([^"]+)"') {
            return $Matches[1]
        }
    }
    throw "No version found in $ManifestPath"
}

$pluginProject = Join-Path $repoRoot 'Dalamud-Plugin\InstantEdit.csproj'
$distRoot = Join-Path $repoRoot 'dist'
# Build away from Dalamud-Plugin\bin\Release: that folder is a Dalamud dev-plugin location with
# automatic reloading, so building there would hot-reload the plugin in a running game.
$pluginBuildOutput = (Join-Path $distRoot 'plugin-build') + '/'
$pluginPackage = Join-Path $distRoot 'InstantEdit\latest.zip'
$blenderStaging = Join-Path $distRoot 'blender_repo'
$blenderPackage = Join-Path $blenderStaging 'XIV-Instant-Edit-Blender.zip'
$releaseAssets = Join-Path $distRoot 'release'
$trackedIndex = Join-Path $repoRoot 'Blender-Addon\blender_repo\index.json'
$blenderManifest = Join-Path $repoRoot 'Blender-Addon\blender_manifest.toml'

if (-not (Test-Path -LiteralPath $blenderManifest -PathType Leaf)) {
    throw "Blender manifest not found: $blenderManifest"
}

if (-not $AllowDirty) {
    $dirty = & git -C $repoRoot status --porcelain -- Blender-Addon Dalamud-Plugin Tools ':(exclude)Blender-Addon/blender_repo'
    if ($LASTEXITCODE -ne 0) {
        throw 'git status failed.'
    }
    if ($dirty) {
        throw "Commit or discard these changes first, or pass -AllowDirty:`n$($dirty -join "`n")"
    }
}

New-Item -ItemType Directory -Path $releaseAssets -Force | Out-Null
$assets = @()

if (-not $SkipPlugin) {
    Invoke-Checked 'dotnet' @('build', $pluginProject, '-c', $Configuration, "-p:OutputPath=$pluginBuildOutput")
    if ($Configuration -eq 'Release') {
        Assert-DalamudPackage $pluginPackage
        $pluginAsset = Join-Path $releaseAssets 'XIV-Instant-Edit.zip'
        Copy-Item -LiteralPath $pluginPackage -Destination $pluginAsset -Force
        $assets += $pluginAsset
    }
}

if (-not $SkipBlender) {
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        $Tag = Get-ManifestVersion $blenderManifest
    }
    $archiveUrl = "https://github.com/link-0402/XIV-Instant-Edit/releases/download/$Tag/XIV-Instant-Edit-Blender.zip"

    if (Test-Path -LiteralPath $blenderStaging) {
        Remove-Item -LiteralPath $blenderStaging -Recurse -Force
    }
    $generateScript = Join-Path $scriptRoot 'generate-blender-repository.ps1'
    $generateParameters = @{ RepositoryPath = $blenderStaging; ArchiveUrl = $archiveUrl }
    if (-not [string]::IsNullOrWhiteSpace($BlenderPath)) {
        $generateParameters.BlenderPath = $BlenderPath
    }
    & $generateScript @generateParameters

    Copy-Item -LiteralPath (Join-Path $blenderStaging 'index.json') -Destination $trackedIndex -Force
    & (Join-Path $scriptRoot 'verify-blender-repository.ps1') `
        -RepositoryPath (Split-Path -Parent $trackedIndex) `
        -ExpectedMinimum '4.5.0' `
        -ArchivePath $blenderPackage
    $blenderAsset = Join-Path $releaseAssets 'XIV-Instant-Edit-Blender.zip'
    Copy-Item -LiteralPath $blenderPackage -Destination $blenderAsset -Force
    $assets += $blenderAsset
}

Write-Host ''
Write-Host "Release assets in ${releaseAssets}:"
foreach ($asset in $assets) {
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "  $(Split-Path -Leaf $asset)  $((Get-Item -LiteralPath $asset).Length) bytes  sha256:$hash"
}
if (-not $SkipBlender) {
    Write-Host "Blender-Addon\blender_repo\index.json now points at $archiveUrl"
}
Write-Host 'Next (docs/release-packaging.md): commit, create the GitHub release with these assets, then push main.'
