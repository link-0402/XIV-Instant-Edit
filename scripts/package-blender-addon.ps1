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
    $OutputPath = Join-Path $scriptRoot '..\dist\blender_repo\XIV-Instant-Edit-Blender.zip'
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

# Entries get one fixed timestamp and a fixed order, so the same checkout packs to the same
# bytes (on the same PowerShell runtime) and the index hash only changes with the files.
$entryTimestamp = [System.DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)

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

    $entries = foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($addonRoot.Length).TrimStart('\', '/')
        [pscustomobject]@{
            Name = $relativePath.Replace('\', '/')
            Path = $file.FullName
        }
    }
    $sortedEntries = @($entries)
    [System.Array]::Sort(
        $sortedEntries,
        [System.Comparison[object]] { param($a, $b) [string]::CompareOrdinal($a.Name, $b.Name) })

    foreach ($entry in $sortedEntries) {
        $bytes = [System.IO.File]::ReadAllBytes($entry.Path)
        # Text files go in with LF endings, as git stores them, so a Windows checkout's CRLF
        # copies pack the same as a Linux one. Files with a NUL byte are binary and left alone.
        if ([System.Array]::IndexOf($bytes, [byte]0) -lt 0) {
            $latin1 = [System.Text.Encoding]::GetEncoding(28591)
            $bytes = $latin1.GetBytes($latin1.GetString($bytes).Replace("`r`n", "`n"))
        }
        $zipEntry = $archive.CreateEntry($entry.Name, [System.IO.Compression.CompressionLevel]::Optimal)
        $zipEntry.LastWriteTime = $entryTimestamp
        $target = $zipEntry.Open()
        try {
            $target.Write($bytes, 0, $bytes.Length)
        }
        finally {
            $target.Dispose()
        }
    }
}
finally {
    if ($null -ne $archive) {
        $archive.Dispose()
    }
}

Write-Host "Created $resolvedOutput"
