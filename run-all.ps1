<#
 Starts the API (http://localhost:5118) and the web app (http://localhost:5200) and opens the browser.
 Usage:  powershell -ExecutionPolicy Bypass -File blazor\run-all.ps1
 Stop:   close the two console windows it opens.
#>
Set-Location $PSScriptRoot
Write-Host "Building..." -ForegroundColor Cyan
dotnet build src/Nuraherbex.Api --nologo -v q; if ($LASTEXITCODE) { Write-Host "API build failed" -ForegroundColor Red; exit 1 }
dotnet build src/Nuraherbex.Web --nologo -v q; if ($LASTEXITCODE) { Write-Host "Web build failed" -ForegroundColor Red; exit 1 }

Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PSScriptRoot'; dotnet run --no-build --project src/Nuraherbex.Api --launch-profile http"
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PSScriptRoot'; dotnet run --project src/Nuraherbex.Web --launch-profile http"

Write-Host "Waiting for the web app..." -ForegroundColor Cyan
for ($i = 0; $i -lt 60; $i++) {
  try { if ((Invoke-WebRequest http://localhost:5200 -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { break } } catch { Start-Sleep 1 }
}
Start-Process "http://localhost:5200"
Write-Host "Web: http://localhost:5200   API: http://localhost:5118/api/health   Admin: /admin" -ForegroundColor Green
