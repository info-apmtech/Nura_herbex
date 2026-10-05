<#
 Builds the Android app from the command line.

 Why a script: with the current .NET 10 SDK, restoring the MAUI project for Android overwrites the shared
 Nuraherbex.UI restore data, so the order matters (MAUI restore -> UI restore -> UI build -> MAUI build).
 Visual Studio 2026 (v18+) does this for you; use this script only on the command line.

 Usage:  powershell -File blazor/build-android.ps1 [-Configuration Release]
#>
param([string]$Configuration = "Debug")
Set-Location $PSScriptRoot
$tfm = "net10.0-android36.0"
dotnet restore src/Nuraherbex.Maui -p:TargetFrameworks=$tfm -v q; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet restore src/Nuraherbex.UI -v q;                         if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build   src/Nuraherbex.UI --no-restore -c $Configuration --nologo -v q; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build   src/Nuraherbex.Maui -p:TargetFrameworks=$tfm --no-restore -c $Configuration --nologo -v q
exit $LASTEXITCODE
