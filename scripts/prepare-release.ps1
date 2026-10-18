# Windows PowerShell 5.1 turns a native command's redirected stderr into a terminating error under
# ErrorActionPreference Stop, so `gh release view` of a new tag would end the script.
#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BlenderPath = '',
    # The GitHub release tag the packages are attached to. Defaults to the plugin version, the tag
    # the release steps create.
    [string]$Tag = '',
    [switch]$SkipPlugin,
    [switch]$SkipBlender,
    # Package uncommitted add-on or plugin changes anyway (not for a real release).
    [switch]$AllowDirty,
    # Only run the component version checks and print what the release would ship.
    [switch]$CheckOnly
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

function Get-CsprojValue {
    param([string]$ProjectPath, [string]$Name)

    $match = Select-String -LiteralPath $ProjectPath -Pattern "<$Name>([^<]+)</$Name>" | Select-Object -First 1
    if ($null -eq $match) {
        throw "No <$Name> found in $ProjectPath"
    }
    return $match.Matches[0].Groups[1].Value.Trim()
}

function Get-PluginVersion {
    param([string]$ProjectPath)

    return Get-CsprojValue $ProjectPath 'Version'
}

# The add-on version the tracked index.json hands out, and the git tag it was released from: the tag
# in its archive_url, or (for a relative archive_url, as before release assets) the tag named after it.
function Get-ReleasedAddon {
    param([string]$IndexPath)

    $entry = @((Get-Content -LiteralPath $IndexPath -Raw | ConvertFrom-Json).data | Where-Object { $_.id -eq 'xiv_instant_edit' }) | Select-Object -First 1
    if ($null -eq $entry) {
        throw "$IndexPath lists no xiv_instant_edit entry."
    }
    $tag = $entry.version
    if ($entry.archive_url -match '/releases/download/([^/]+)/') {
        $tag = $Matches[1]
    }
    return @{ Version = $entry.version; Tag = $tag }
}

function Get-ManifestVersion {
    param([string]$ManifestPath)

    $match = [regex]::Match([System.IO.File]::ReadAllText($ManifestPath), '(?m)^version\s*=\s*"([^"]+)"')
    if (-not $match.Success) {
        throw "No version found in $ManifestPath"
    }
    return $match.Groups[1].Value
}

function Get-PainterVersion {
    param([string]$Source)

    $match = [regex]::Match($Source, '(?m)^PLUGIN_VERSION\s*=\s*"([^"]+)"')
    if (-not $match.Success) {
        throw 'No PLUGIN_VERSION found in the Painter plugin.'
    }
    return $match.Groups[1].Value
}

# Whether committed files under the pathspecs differ between the tag and HEAD.
function Test-PathsChanged {
    param([string]$Tag, [string[]]$Pathspecs)

    & git -C $repoRoot rev-parse --verify --quiet "refs/tags/$Tag" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Tag $Tag not found. Fetch the release tags (git fetch --tags) first."
    }
    & git -C $repoRoot diff --quiet $Tag HEAD -- @Pathspecs
    switch ($LASTEXITCODE) {
        0 { return $false }
        1 { return $true }
        default { throw "git diff $Tag HEAD failed." }
    }
}

# Points the in-repo repo.json's download links at this version's release, not "latest": its
# AssemblyVersion ships with main, so a pinned link always hands out the version it names, even
# when main is pushed before the release exists.
function Update-RepoJson {
    param([string]$RepoJsonPath, [string]$Version, [string]$ReleaseTag)

    $text = [System.IO.File]::ReadAllText($RepoJsonPath)
    $listed = [regex]::Match($text, '"AssemblyVersion"\s*:\s*"([^"]+)"')
    if (-not $listed.Success -or $listed.Groups[1].Value -ne $Version) {
        throw "Dalamud-Plugin\repo.json lists AssemblyVersion '$($listed.Groups[1].Value)', but the plugin is $Version. Bump repo.json first."
    }
    $url = "https://github.com/link-0402/XIV-Instant-Edit/releases/download/$ReleaseTag/XIV-Instant-Edit.zip"
    $updated = [regex]::Replace($text, '("DownloadLink(?:Install|Update|Testing)"\s*:\s*")[^"]*(")', "`${1}$url`${2}")
    if ($updated -ne $text) {
        [System.IO.File]::WriteAllText($RepoJsonPath, $updated, [System.Text.UTF8Encoding]::new($false))
    }
    return $url
}

# Refuses a tag whose GitHub release already holds this asset: the new build's bytes would not
# match the uploaded one, so the index (or repo.json) would name a file that fails its check.
function Assert-AssetFree {
    param([string]$ReleaseTag, [string]$AssetName)

    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Warning "gh not found; can't check that release $ReleaseTag has no $AssetName yet."
        return
    }
    $errors = New-TemporaryFile
    try {
        $names = & gh release view $ReleaseTag --repo link-0402/XIV-Instant-Edit --json assets --jq '.assets[].name' 2>$errors
        $failure = (Get-Content -Raw $errors) ?? ''
    } finally {
        Remove-Item $errors -ErrorAction SilentlyContinue
    }
    if ($LASTEXITCODE -ne 0) {
        # A new release has no tag on GitHub yet; anything else (not logged in, offline) leaves the check undone.
        if ($failure -notmatch 'release not found') {
            Write-Warning "Could not check release ${ReleaseTag} for ${AssetName}: $($failure.Trim())"
        }
        return
    }
    if (@($names) -contains $AssetName) {
        throw "Release $ReleaseTag already has $AssetName. Pass the new release's -Tag (or -SkipBlender / -SkipPlugin)."
    }
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

# Component versions. The add-on and the Painter plugin are versioned on their own: bumped only when
# their code changes, and the plugin accepts any version at or above its declared minimum.
$addonVersion = Get-ManifestVersion $blenderManifest
$released = Get-ReleasedAddon $trackedIndex
# What the add-on package holds (blender_manifest.toml's paths_exclude_pattern), less its docs.
$addonPaths = @(
    'Blender-Addon',
    ':(exclude)Blender-Addon/blender_repo',
    ':(exclude)Blender-Addon/testing',
    ':(exclude,glob)Blender-Addon/**/__pycache__/**',
    ':(exclude)Blender-Addon/*.zip',
    ':(exclude)Blender-Addon/*.ps1',
    ':(exclude)Blender-Addon/.*',
    ':(exclude)Blender-Addon/*.md',
    ':(exclude)Blender-Addon/LICENSE'
)
$pluginVersion = Get-PluginVersion $pluginProject
if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = $pluginVersion
}
$forcedSkip = [bool]$SkipBlender
$shipsAddon = $false
if ([version]$addonVersion -lt [version]$released.Version) {
    throw "blender_manifest.toml is $addonVersion, below the released add-on $($released.Version)."
}
if ([version]$addonVersion -eq [version]$released.Version) {
    & git -C $repoRoot rev-parse --verify --quiet "refs/tags/$($released.Tag)" *> $null
    if ($LASTEXITCODE -ne 0 -and $released.Tag -eq $Tag) {
        # An earlier run of this release already pointed index.json at it; the tag comes later.
        Write-Host "Blender add-on $addonVersion is being released with $Tag; packaging it."
        $shipsAddon = $true
    } elseif (Test-PathsChanged $released.Tag $addonPaths) {
        if (-not $forcedSkip) {
            throw "Add-on files changed since $($released.Version) ($($released.Tag)). Bump the version in blender_manifest.toml."
        }
        Write-Warning "Add-on files changed since $($released.Version) without a version bump; -SkipBlender leaves them out of this release."
    } elseif (-not $forcedSkip) {
        Write-Host "Blender add-on $addonVersion is already released and unchanged; skipping its package."
    }
} elseif ($forcedSkip) {
    Write-Host "Blender add-on $addonVersion is new (released: $($released.Version)), but -SkipBlender leaves it out."
} else {
    Write-Host "Blender add-on $addonVersion is new (released: $($released.Version)); packaging it."
    $shipsAddon = $true
}
if ($forcedSkip -or -not $shipsAddon) {
    $SkipBlender = $true
}
# The add-on users can get from GitHub after this release.
$publishedAddon = if ($SkipBlender) { $released.Version } else { $addonVersion }

$painterInit = Join-Path $repoRoot 'Tools\SubstancePainter\xiv_instant_edit\__init__.py'
$painterVersion = Get-PainterVersion ([System.IO.File]::ReadAllText($painterInit))
$previousTag = & git -C $repoRoot describe --tags --abbrev=0 HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($previousTag)) {
    Write-Warning "No release tag found; can't check the Painter plugin for unbumped changes."
} else {
    $previousPainter = Get-PainterVersion ((& git -C $repoRoot show "${previousTag}:Tools/SubstancePainter/xiv_instant_edit/__init__.py") -join "`n")
    if ($previousPainter -eq $painterVersion -and (Test-PathsChanged $previousTag @('Tools/SubstancePainter/xiv_instant_edit'))) {
        throw "Painter plugin files changed since $previousTag. Bump PLUGIN_VERSION in Tools/SubstancePainter/xiv_instant_edit/__init__.py."
    }
    Write-Host "Painter plugin $painterVersion (at $previousTag`: $previousPainter)."
}

$addonMinimum = Get-CsprojValue $pluginProject 'BlenderAddonMinimumVersion'
$painterMinimum = Get-CsprojValue $pluginProject 'PainterPluginMinimumVersion'
if ([version]$addonMinimum -gt [version]$publishedAddon) {
    throw "BlenderAddonMinimumVersion $addonMinimum is above the add-on version users can get after this release, $publishedAddon."
}
if ([version]$painterMinimum -gt [version]$painterVersion) {
    throw "PainterPluginMinimumVersion $painterMinimum is above the Painter plugin version $painterVersion."
}
Write-Host "The plugin needs add-on $addonMinimum or newer and Painter plugin $painterMinimum or newer."
if ($CheckOnly) {
    Write-Host "Checks passed. Blender package: $(if ($SkipBlender) { 'skipped' } else { 'built' })."
    exit 0
}

$repoJson = Join-Path $repoRoot 'Dalamud-Plugin\repo.json'
if (-not $SkipPlugin -and $Configuration -eq 'Release') {
    Assert-AssetFree $Tag 'XIV-Instant-Edit.zip'
}
if (-not $SkipBlender) {
    Assert-AssetFree $Tag 'XIV-Instant-Edit-Blender.zip'
}

New-Item -ItemType Directory -Path $releaseAssets -Force | Out-Null
$assets = @()
$pluginUrl = $null

if (-not $SkipPlugin) {
    # A clean output folder, so files the project no longer has can't end up in the package.
    if (Test-Path -LiteralPath $pluginBuildOutput) {
        Remove-Item -LiteralPath $pluginBuildOutput -Recurse -Force
    }
    Invoke-Checked 'dotnet' @('build', $pluginProject, '-c', $Configuration, "-p:OutputPath=$pluginBuildOutput")
    if ($Configuration -eq 'Release') {
        Assert-DalamudPackage $pluginPackage
        $pluginAsset = Join-Path $releaseAssets 'XIV-Instant-Edit.zip'
        Copy-Item -LiteralPath $pluginPackage -Destination $pluginAsset -Force
        $assets += $pluginAsset
        $pluginUrl = Update-RepoJson $repoJson $pluginVersion $Tag
        # The first release lived at the repository root; users who added that URL still read it.
        Copy-Item -LiteralPath $repoJson -Destination (Join-Path $repoRoot 'repo.json') -Force
    }
}

if (-not $SkipBlender) {
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
if ($pluginUrl) {
    Write-Host "Dalamud-Plugin\repo.json now downloads $pluginUrl"
}
if (-not $SkipBlender) {
    Write-Host "Blender-Addon\blender_repo\index.json now points at $archiveUrl"
}
Write-Host "Next (docs/release-packaging.md): commit, create the GitHub release $Tag with these assets, then push main."
