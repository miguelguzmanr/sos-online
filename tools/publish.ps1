#Requires -Version 7
<#
.SYNOPSIS
  Publishes the current main as a GitHub release; players' games then update themselves at their next start.

.DESCRIPTION
  Needs a clean main pushed to GitHub, the GitHub CLI (gh) logged in, and the release signing key. Packs a signed zip
  (package.ps1) and creates release v0.1.<count> with it. Games check the latest release at start, install a newer
  signed build and run it from the start after.

.PARAMETER GameDir
  Sea of Stars install folder with BepInEx and its interop assemblies (needed to build).

.PARAMETER Notes
  Release notes (default: the commit subjects since the previous release).
#>
param(
    [string]$GameDir = "W:\SteamLibrary\steamapps\common\Sea of Stars",
    [string]$Notes
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ((git -C $root rev-parse --abbrev-ref HEAD).Trim() -ne "main") { throw "Publish from main." }
if (git -C $root status --porcelain) { throw "Commit or stash your changes first: releases are built from a clean main." }
if (-not (Test-Path (Join-Path $env:APPDATA "SosOnline\release-signing-key.txt"))) { throw "No release signing key: run tools\new-signing-key.ps1 once." }

git -C $root push origin main
if ($LASTEXITCODE -ne 0) { throw "Push failed." }
git -C $root fetch --tags --quiet origin

& "$PSScriptRoot\package.ps1" -GameDir $GameDir
$count = (git -C $root rev-list --count HEAD).Trim()
$tag = "v0.1.$count"
$zip = Get-ChildItem (Join-Path $root "dist") -Filter "SosOnline-0.1.$count+*.zip" | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $zip -or $zip.Name -like "*-modified*") { throw "Expected a clean dist\SosOnline-0.1.$count+<commit>.zip." }

if (-not $Notes) {
    $previous = gh release view --repo miguelguzmanr/sosonline --json tagName --jq .tagName 2>$null
    $range = if ($previous) { "$(git -C $root rev-list -n 1 $previous 2>$null)..HEAD" } else { "HEAD" }
    if ($range -like "..HEAD") { $range = "HEAD" }
    $Notes = (git -C $root log --format="- %s" $range | Select-Object -First 30) -join "`n"
}

gh release create $tag $zip.FullName --repo miguelguzmanr/sosonline --title "Sea of Stars Online Co-op $tag" --notes $Notes --target main
if ($LASTEXITCODE -ne 0) { throw "Release failed." }
Write-Host "Released $tag. Games update at their next start and run it from the start after."
