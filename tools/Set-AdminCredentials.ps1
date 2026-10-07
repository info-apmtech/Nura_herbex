# Run with PowerShell 7: pwsh -File tools/Set-AdminCredentials.ps1
param([string]$Email = 'admin@nuraherbex.com')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7 (pwsh).' }
if ($Email -notmatch '^[^\s@]+@[^\s@]+\.[^\s@]+$') { throw 'Enter a valid administrator email.' }
$secret = Read-Host 'New administrator password (at least 12 characters)' -AsSecureString
$confirm = Read-Host 'Confirm password' -AsSecureString
$password = [Net.NetworkCredential]::new('', $secret).Password
if ($password.Length -lt 12) { throw 'Use a password with at least 12 characters.' }
if ($password -cne [Net.NetworkCredential]::new('', $confirm).Password) { throw 'Passwords do not match.' }
$configPath = Join-Path $PSScriptRoot '../src/Nuraherbex.Api/appsettings.Local.json'
$config = if (Test-Path $configPath) { Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
if (!$config.ContainsKey('Admin')) { $config.Admin = @{} }
$salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA512, 64)
$config.Admin.Email = $Email.Trim().ToLowerInvariant()
$config.Admin.PasswordHash = 'pbkdf2-sha512:210000:' + [Convert]::ToBase64String($salt) + ':' + [Convert]::ToBase64String($hash)
$config.Admin.Password = ''
$config | ConvertTo-Json -Depth 100 | Set-Content $configPath
$password = $null
Write-Host "Administrator $Email configured. Restart the API, then sign in at /admin."
