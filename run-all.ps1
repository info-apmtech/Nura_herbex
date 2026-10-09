<#
 Starts the API (http://localhost:5118) and the web app (http://localhost:5200), reusing this app if already running.
 Usage:  powershell -ExecutionPolicy Bypass -File run-all.ps1
 Stop:   close the console windows it opens.
#>
Set-Location $PSScriptRoot

function Test-Endpoint([string]$Url) {
  try {
    $response = Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 2
    return $response.StatusCode -ge 200 -and $response.StatusCode -lt 400
  } catch { return $false }
}

function Get-AppState([int]$Port, [string]$ProjectName) {
  $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
  if ($listeners.Count -eq 0) {
    return [pscustomobject]@{ State = "Stopped"; Pid = 0; Process = "" }
  }
  foreach ($listener in $listeners) {
    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    $native = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue
    $isThisApp = ($process.ProcessName -like "*$ProjectName*") -or
      ($native.CommandLine -and $native.CommandLine.IndexOf($ProjectName, [StringComparison]::OrdinalIgnoreCase) -ge 0)
    if ($isThisApp) {
      return [pscustomobject]@{ State = "Running"; Pid = $listener.OwningProcess; Process = $process.ProcessName }
    }
    return [pscustomobject]@{ State = "Occupied"; Pid = $listener.OwningProcess; Process = $process.ProcessName }
  }
  return [pscustomobject]@{ State = "Stopped"; Pid = 0; Process = "" }
}

$apiState = Get-AppState 5118 "Nuraherbex.Api"
$webState = Get-AppState 5200 "Nuraherbex.Web"
if ($apiState.State -eq "Occupied") {
  Write-Host "Port 5118 is held by $($apiState.Process) (PID $($apiState.Pid)); API was not started." -ForegroundColor Red
  exit 1
}
if ($webState.State -eq "Occupied") {
  Write-Host "Port 5200 is held by $($webState.Process) (PID $($webState.Pid)); web app was not started." -ForegroundColor Red
  exit 1
}

$needApi = $apiState.State -eq "Stopped"
$needWeb = $webState.State -eq "Stopped"
if ($needApi) {
  Write-Host "Building API..." -ForegroundColor Cyan
  dotnet build src/Nuraherbex.Api --nologo -v q
  if ($LASTEXITCODE) { Write-Host "API build failed" -ForegroundColor Red; exit 1 }
}
if ($needWeb) {
  Write-Host "Building web app..." -ForegroundColor Cyan
  dotnet build src/Nuraherbex.Web --nologo -v q
  if ($LASTEXITCODE) { Write-Host "Web build failed" -ForegroundColor Red; exit 1 }
}

if ($needApi) {
  Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PSScriptRoot'; dotnet run --no-build --project src/Nuraherbex.Api --launch-profile http"
} else {
  Write-Host "API is already running; reusing it." -ForegroundColor Yellow
}
if ($needWeb) {
  Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PSScriptRoot'; dotnet run --no-build --project src/Nuraherbex.Web --launch-profile http"
} else {
  Write-Host "Web app is already running; reusing it." -ForegroundColor Yellow
}

Write-Host "Waiting for the web app..." -ForegroundColor Cyan
$webReady = $false
for ($i = 0; $i -lt 60; $i++) {
  if (Test-Endpoint "http://localhost:5200") { $webReady = $true; break }
  Start-Sleep 1
}
if (-not $webReady) {
  Write-Host "Web app did not become ready on port 5200. Check the API/web console windows for startup errors." -ForegroundColor Red
  exit 1
}
Start-Process "http://localhost:5200"
Write-Host "Web: http://localhost:5200   API: http://localhost:5118/api/health   Admin: /admin" -ForegroundColor Green