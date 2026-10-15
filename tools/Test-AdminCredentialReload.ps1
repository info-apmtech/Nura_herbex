param([string]$ApiDll = (Join-Path $PSScriptRoot '../src/Nuraherbex.Api/bin/Debug/net10.0/Nuraherbex.Api.dll'))
$ErrorActionPreference = 'Stop'
function New-Hash([string]$Password) {
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($Password, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA512, 64)
    'pbkdf2-sha512:210000:' + [Convert]::ToBase64String($salt) + ':' + [Convert]::ToBase64String($hash)
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('nura-login-reload-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$configPath = Join-Path $testRoot 'appsettings.Local.json'
$oldPassword = [Guid]::NewGuid().ToString('N')
$newPassword = [Guid]::NewGuid().ToString('N')
$config = @{ Admin = @{ Email = 'admin@example.test'; PasswordHash = (New-Hash $oldPassword) }; Jwt = @{ Secret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N') } }
$config | ConvertTo-Json -Depth 5 | Set-Content $configPath
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $testRoot
foreach ($arg in @($ApiDll, '--contentRoot', $testRoot, '--urls', "http://127.0.0.1:$port", '--Database:Provider', 'InMemory', '--Database:AutoCreate', 'false', '--Logging:EventLog:LogLevel:Default', 'None')) { $start.ArgumentList.Add($arg) }
$start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
foreach ($key in @($start.Environment.Keys)) { if ($key -like 'Admin__*') { $start.Environment.Remove($key) | Out-Null } }
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
function Login([string]$Password) {
    $body = @{email='admin@example.test';password=$Password} | ConvertTo-Json
    $response = Invoke-WebRequest "http://127.0.0.1:$port/api/admin/login" -Method Post -ContentType 'application/json' -Body $body -SkipHttpErrorCheck -TimeoutSec 10
    [int]$response.StatusCode
}
try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        if ($process.HasExited) { throw ('API exited: ' + $stderr.Result) }
        try { Invoke-WebRequest "http://127.0.0.1:$port/api/admin/verify" -SkipHttpErrorCheck -TimeoutSec 1 | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$ready) { throw 'API did not start' }
    if ((Login $oldPassword) -ne 200) { throw 'Initial configured password rejected' }
    Write-Output 'PASS: initial configured password accepted'
    $config.Admin.PasswordHash = New-Hash $newPassword
    $config | ConvertTo-Json -Depth 5 | Set-Content $configPath
    Start-Sleep -Seconds 3
    if ((Login $newPassword) -ne 200) { throw 'Updated password rejected after configuration reload' }
    Write-Output 'PASS: updated password accepted without restarting API'
    if ((Login $oldPassword) -ne 401) { throw 'Old password was not rejected' }
    Write-Output 'PASS: old password rejected after configuration reload'
} catch {
    $failure = $_
    if (!$process.HasExited) { $process.Kill($true) }
    $process.WaitForExit()
    Write-Output $stderr.Result
    Write-Output (($stdout.Result -split "`n" | Select-Object -Last 25) -join "`n")
    throw $failure
} finally {
    if (!$process.HasExited) { $process.Kill($true) }
    $process.WaitForExit()
    Remove-Item -LiteralPath $configPath
    [IO.Directory]::Delete($testRoot, $false)
}
