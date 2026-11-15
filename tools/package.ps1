<#
.SYNOPSIS
  Builds the plugin and packs it for players who don't build it themselves.

.DESCRIPTION
  Writes dist\SosOnline-<commit>.zip with BepInEx\plugins\SosOnline\SosOnline.dll inside. Extract it into the game
  folder (after BepInEx is installed there) so the DLL lands in BepInEx\plugins\SosOnline. Everyone in a session needs
  the same build: the zip name carries the commit it was built from.

.PARAMETER GameDir
  Sea of Stars install folder with BepInEx and its interop assemblies (needed to build).
#>
param(
    [string]$GameDir = "W:\SteamLibrary\steamapps\common\Sea of Stars"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path "$GameDir\BepInEx\interop\Assembly-CSharp.dll")) {
    throw "BepInEx interop assemblies are missing in '$GameDir'. Run tools\deploy.ps1 first."
}

dotnet build "$root\SosOnline\SosOnline.csproj" -c Release -p:GameDir="$GameDir" -p:DeployToGame=false
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty = git -C $root status --porcelain -- SosOnline
if ($dirty) { $commit += "-modified" }

$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$plugin = New-Item -ItemType Directory -Force "$stage\BepInEx\plugins\SosOnline"
Copy-Item "$root\SosOnline\bin\Release\SosOnline.dll" $plugin

$zip = Join-Path $root "dist\SosOnline-$commit.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\BepInEx" -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "Packed $zip"
