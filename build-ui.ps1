<#
 Serialised build of the shared UI library. Several people/agents can call this at once:
 a global mutex queues them so the shared obj/ folder is never written concurrently.
 Usage:  pwsh blazor/build-ui.ps1 [-Match "Pages\\Shop|Components\\Hero"]
 Prints de-duplicated compiler errors (optionally only those whose path matches -Match).
#>
param([string]$Match = "", [string]$Project = "src/Nuraherbex.UI", [string]$Configuration = "Debug")
Set-Location $PSScriptRoot
$mutex = New-Object System.Threading.Mutex($false, "Global\NuraherbexUiBuild")
[void]$mutex.WaitOne()
try {
  $out = dotnet build $Project -c $Configuration --nologo -v q 2>&1 | Out-String
  $buildExitCode = $LASTEXITCODE
} finally { $mutex.ReleaseMutex() }

$errors = $out -split "`r?`n" | Where-Object { $_ -match ": error " } |
  ForEach-Object { ($_ -replace '\s*\[[^\]]+\]\s*$', '').Trim() } | Sort-Object -Unique
if ($Match) { $errors = $errors | Where-Object { $_ -match $Match } }
if ($errors) { $errors; "---- $($errors.Count) error(s)" } else { "Build OK (no errors$(if ($Match) { " in files matching '$Match'" }))" }


if ($buildExitCode -ne 0) { throw "UI build failed (exit $buildExitCode)." }
