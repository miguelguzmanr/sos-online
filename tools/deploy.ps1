<#
.SYNOPSIS
  Builds the Sea of Stars Online Co-op plugin and installs it into the game.

.DESCRIPTION
  1. Installs BepInEx 6 (IL2CPP, x64) into the game folder if it is missing, after asking.
  2. Builds SosOnline and copies it to BepInEx\plugins\SosOnline.
  BepInEx has to run the game once to generate its interop assemblies before the plugin can build.

.PARAMETER GameDir
  Sea of Stars install folder.

.PARAMETER BepInExUrl
  BepInEx bleeding-edge build to install if BepInEx is missing.
#>
param(
    [string]$GameDir = "W:\SteamLibrary\steamapps\common\Sea of Stars",
    [string]$BepInExUrl = "https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path "$GameDir\SeaOfStars.exe")) { throw "SeaOfStars.exe not found in '$GameDir'. Pass -GameDir." }

if (-not (Test-Path "$GameDir\BepInEx\core\BepInEx.Unity.IL2CPP.dll")) {
    $answer = Read-Host "BepInEx is not installed in '$GameDir'. Download and install it from builds.bepinex.dev? (y/n)"
    if ($answer -ne "y") { throw "BepInEx is required." }
    $zip = Join-Path $env:TEMP "bepinex-il2cpp.zip"
    Invoke-WebRequest $BepInExUrl -OutFile $zip
    Expand-Archive $zip -DestinationPath $GameDir -Force
    Write-Host "BepInEx installed. Start the game once (it generates interop assemblies, ~1 min), quit, then run this script again."
    exit 0
}

if (-not (Test-Path "$GameDir\BepInEx\interop\Assembly-CSharp.dll")) {
    throw "BepInEx interop assemblies are missing. Start the game once with BepInEx installed, quit, then run this script again."
}

dotnet build "$root\SosOnline\SosOnline.csproj" -c Release -p:GameDir="$GameDir" -p:DeployToGame=true
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host "Installed to $GameDir\BepInEx\plugins\SosOnline. Start the game and pick Online on the title screen."
