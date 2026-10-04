<#
.SYNOPSIS
    Builds one release zip per Missfits addon.

.DESCRIPTION
    Every folder under addons/ that has a plugin.cfg is an addon to release. Its zip holds the addon
    itself plus addons/misscore, because Godot does not resolve dependencies between assets: each
    addon has to bring the core along. The zips unpack straight into a project (addons/... at the
    top level).

    All addons are released with the same version number. The script refuses to build when their
    plugin.cfg files disagree; -Version writes one number into all of them first.

    Unless -SkipVerify is given, every zip is unpacked into an empty project and compiled there,
    as an editor build and as an export build. That catches an addon that reaches into a sibling
    addon, and runtime code that reaches into editor code.

.PARAMETER Version
    Sets this version in the plugin.cfg of every addon before building.

.PARAMETER OutDir
    Where the zips go. Defaults to dist/ in the repository.

.PARAMETER IncludeTests
    Ships the tests/ folders too. They are left out by default: their probe classes would show up
    in the projects of everyone who installs the addon.

.PARAMETER SkipVerify
    Does not compile the zips.

.EXAMPLE
    tools/build-zips.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutDir,
    [switch] $IncludeTests,
    [switch] $SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Core = 'misscore'
$root = Split-Path -Parent $PSScriptRoot
$addonsDir = Join-Path $root 'addons'
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Get-PluginVersion([string] $pluginCfg) {
    $match = [regex]::Match([IO.File]::ReadAllText($pluginCfg), '(?m)^version\s*=\s*"([^"]*)"')
    if (-not $match.Success) { throw "No version in $pluginCfg" }
    return $match.Groups[1].Value
}

function Set-PluginVersion([string] $pluginCfg, [string] $version) {
    $text = [IO.File]::ReadAllText($pluginCfg)
    $updated = [regex]::Replace($text, '(?m)^(version\s*=\s*)"[^"]*"', ('$1"' + $version + '"'))
    if ($updated -ne $text) { [IO.File]::WriteAllText($pluginCfg, $updated, $utf8) }
}

# The files of one addon folder, as paths relative to the repository, with forward slashes.
function Get-AddonFiles([string] $addon) {
    $dir = Join-Path $addonsDir $addon
    foreach ($file in Get-ChildItem -LiteralPath $dir -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not $IncludeTests -and $relative -match '^addons/[^/]+/tests/') { continue }
        $relative
    }
}

function New-AddonZip([string] $zipPath, [string[]] $files) {
    if (Test-Path -LiteralPath $zipPath) { [IO.File]::Delete($zipPath) }
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $files) {
            # Entry names are written by hand: forward slashes, whatever the platform.
            $source = Join-Path $root $relative
            [void] [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, $relative, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $zip.Dispose()
    }
}

# Unpacks a zip into an empty project and compiles it, the way a project that installs only this
# addon would.
function Test-AddonZip([string] $zipPath, [string] $addon) {
    $work = Join-Path ([IO.Path]::GetTempPath()) "missfits-verify-$addon"
    if (Test-Path -LiteralPath $work) { [IO.Directory]::Delete($work, $true) }
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $work)

        # Same SDK and framework as the development project, under a name of its own: nothing in
        # an addon may count on the assembly being called Missfits.
        $project = Join-Path $work 'Verify.csproj'
        Copy-Item -LiteralPath (Join-Path $root 'Missfits.csproj') -Destination $project

        foreach ($configuration in 'Debug', 'ExportRelease') {
            $output = & dotnet build $project -c $configuration --nologo -v quiet
            if ($LASTEXITCODE -ne 0) {
                $output | Where-Object { $_ -match 'error' } | Select-Object -Unique -First 20 | ForEach-Object { Write-Host "    $_" }
                throw "$addon does not compile on its own ($configuration)."
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $work) { [IO.Directory]::Delete($work, $true) }
    }
}

# ---- which addons, which version ------------------------------------------------------------

$addons = @(Get-ChildItem -LiteralPath $addonsDir -Directory |
    Where-Object { $_.Name -ne $Core -and (Test-Path -LiteralPath (Join-Path $_.FullName 'plugin.cfg')) } |
    Sort-Object Name | ForEach-Object { $_.Name })
if ($addons.Count -eq 0) { throw "No addon with a plugin.cfg under $addonsDir" }
if (-not (Test-Path -LiteralPath (Join-Path $addonsDir $Core))) { throw "addons/$Core is missing" }

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.]+)?$') { throw "'$Version' is not a version like 1.2.3" }
    foreach ($addon in $addons) { Set-PluginVersion (Join-Path $addonsDir "$addon/plugin.cfg") $Version }
}

$versions = @{}
foreach ($addon in $addons) { $versions[$addon] = Get-PluginVersion (Join-Path $addonsDir "$addon/plugin.cfg") }
$distinct = @($versions.Values | Sort-Object -Unique)
if ($distinct.Count -ne 1) {
    $listing = ($addons | ForEach-Object { "$_ $($versions[$_])" }) -join ', '
    throw "The addons are released together and need one version, but have: $listing. Pass -Version to set it."
}
$release = $distinct[0]

$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    $dirty = @(& git -C $root status --porcelain -- addons)
    if ($dirty.Count -gt 0) { Write-Warning "addons/ has uncommitted changes; the zips contain them." }
}

# ---- build ----------------------------------------------------------------------------------

[void] [IO.Directory]::CreateDirectory($OutDir)
$coreFiles = @(Get-AddonFiles $Core)

Write-Host "Missfits $release"
foreach ($addon in $addons) {
    $files = @($coreFiles) + @(Get-AddonFiles $addon)
    $zipPath = Join-Path $OutDir "$addon-$release.zip"
    New-AddonZip $zipPath $files

    $state = 'not compiled'
    if (-not $SkipVerify) {
        try {
            Test-AddonZip $zipPath $addon
        }
        catch {
            # A zip that does not compile must not be left lying around looking like a release.
            [IO.File]::Delete($zipPath)
            throw
        }
        $state = 'compiles on its own'
    }
    $size = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)
    Write-Host ("  {0}  {1} files, {2} KB, {3}" -f $zipPath, $files.Count, $size, $state)
}
