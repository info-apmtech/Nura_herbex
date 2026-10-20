param([string]$ApiDll = (Join-Path $PSScriptRoot '../src/Nuraherbex.Api/bin/Release/net10.0/Nuraherbex.Api.dll'))
# End-to-end check of WhatsApp lead capture against a throw-away in-memory API (no real WhatsApp credentials, nothing is sent).
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('nura-lead-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$password = [Guid]::NewGuid().ToString('N')
$appSecret = [Guid]::NewGuid().ToString('N')
$config = @{
    Admin = @{ Email = 'admin@example.test'; Password = $password }
    Jwt = @{ Secret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N') }
    WhatsApp = @{ VerifyToken = 'verify-me'; AppSecret = $appSecret }
}
$configPath = Join-Path $testRoot 'appsettings.Local.json'
$config | ConvertTo-Json -Depth 5 | Set-Content $configPath
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start()
$port = $listener.LocalEndpoint.Port; $listener.Stop()
$base = "http://127.0.0.1:$port"

$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.WorkingDirectory = $testRoot
$start.Arguments = ('"' + (Resolve-Path $ApiDll).Path + '" --contentRoot "' + $testRoot + '" --urls ' + $base + ' --Database:Provider InMemory --Logging:EventLog:LogLevel:Default None')
$start.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
foreach ($key in @($start.EnvironmentVariables.Keys)) { if ($key -like 'Admin__*' -or $key -like 'WhatsApp__*') { $start.EnvironmentVariables.Remove($key) } }
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()

function Send-Webhook([string]$Phone, [string]$Name, [string]$Text, [string]$Wamid, $Referral = $null) {
    $msg = @{ from = $Phone; id = $Wamid; timestamp = '1'; type = 'text'; text = @{ body = $Text } }
    if ($Referral) { $msg.referral = $Referral }
    $payload = @{ object = 'whatsapp_business_account'; entry = @(@{ id = '1'; changes = @(@{ field = 'messages'; value = @{
        messaging_product = 'whatsapp'; contacts = @(@{ wa_id = $Phone; profile = @{ name = $Name } }); messages = @($msg) } }) }) } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $hmac = New-Object Security.Cryptography.HMACSHA256 (,[Text.Encoding]::UTF8.GetBytes($appSecret))
    $sig = 'sha256=' + ((($hmac.ComputeHash($bytes)) | ForEach-Object { $_.ToString('x2') }) -join '')
    Get-Status -Uri "$base/api/webhooks/whatsapp" -Body $bytes -Headers @{ 'X-Hub-Signature-256' = $sig }
}
# Windows PowerShell 5.1 has no -SkipHttpErrorCheck: return the HTTP status code for success and failure alike.
function Get-Status([string]$Uri, $Body, $Headers = @{}) {
    try { [int](Invoke-WebRequest $Uri -Method Post -ContentType 'application/json' -Body $Body -Headers $Headers -UseBasicParsing -TimeoutSec 10).StatusCode }
    catch { [int]$_.Exception.Response.StatusCode }
}
function Assert([bool]$Cond, [string]$Msg) { if (-not $Cond) { throw "FAIL: $Msg" }; Write-Output "PASS: $Msg" }

try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($process.HasExited) { throw ('API exited: ' + $stderr.Result) }
        try { Invoke-WebRequest "$base/api/admin/verify" -UseBasicParsing -TimeoutSec 1 | Out-Null; $ready = $true; break } catch { if ($_.Exception.Response) { $ready = $true; break }; Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'API did not start' }

    $login = Invoke-RestMethod "$base/api/admin/login" -Method Post -ContentType 'application/json' -Body (@{ email = 'admin@example.test'; password = $password } | ConvertTo-Json)
    $auth = @{ Authorization = "Bearer $($login.token)" }
    function Get-Leads([string]$Query = '') { Invoke-RestMethod "$base/api/admin/leads$Query" -Headers $auth }

    Assert ((Send-Webhook '919876543210' 'Asha' 'Hi, price of Stamix?' 'wamid.1' @{ headline = 'Diwali offer'; source_url = 'https://fb.me/ad'; source_id = '1' }) -eq 200) 'first inbound message accepted'
    $r = Get-Leads
    Assert ($r.total -eq 1) 'first message creates exactly one lead'
    Assert ($r.leads[0].name -eq 'Asha' -and $r.leads[0].phone -eq '919876543210' -and $r.leads[0].status -eq 'new') 'lead has profile name, normalised phone, status new'
    Assert ($r.leads[0].sourceDetail -eq 'Diwali offer') 'Click-to-WhatsApp referral captured as source detail'
    Assert ($r.leads[0].unreadCount -eq 1) 'conversation unread count is 1'

    Send-Webhook '919876543210' 'Asha' 'Hello again' 'wamid.2' | Out-Null
    Send-Webhook '919876543210' 'Asha' 'Hello again' 'wamid.2' | Out-Null   # Meta retry (same id)
    $r = Get-Leads
    Assert ($r.total -eq 1) 'repeat messages and retries do not create a duplicate lead'
    Assert ($r.leads[0].unreadCount -eq 2) 'retry with the same message id is idempotent (unread = 2)'

    Send-Webhook '9876543210' 'Asha K' 'from a local-format number' 'wamid.3' | Out-Null
    Assert ((Get-Leads).total -eq 1) 'local-format number maps to the same lead'

    Send-Webhook '919000000001' 'Ravi' 'STOP' 'wamid.4' | Out-Null
    $r = Get-Leads
    Assert ($r.total -eq 2) 'second phone creates a second lead'
    Assert (($r.leads | Where-Object phone -eq '919000000001').consentStatus -eq 'opted_out') 'STOP keyword sets opted_out'

    $id = ($r.leads | Where-Object phone -eq '919876543210').id
    $d = Invoke-RestMethod "$base/api/admin/leads/$id" -Headers $auth
    Assert ($d.messages.Count -eq 3) 'lead detail returns its 3 distinct messages'
    Invoke-RestMethod "$base/api/admin/leads/$id/status" -Method Put -Headers $auth -ContentType 'application/json' -Body (@{ status = 'interested'; note = 'asked price' } | ConvertTo-Json) | Out-Null
    $d = Invoke-RestMethod "$base/api/admin/leads/$id" -Headers $auth
    Assert ($d.lead.status -eq 'interested' -and $d.history.Count -eq 2) 'status change saved with history'
    Assert ($d.lead.unreadCount -eq 0 -or (Get-Leads "?q=Asha").leads[0].unreadCount -eq 0) 'opening a lead clears unread'
    Assert ((Get-Leads '?status=new').total -eq 1) 'status filter works'
    Assert ((Get-Status -Uri "$base/api/webhooks/whatsapp" -Body '{}') -ne 200) 'unsigned webhook rejected'
} catch {
    $failure = $_
    if (!$process.HasExited) { $process.Kill() }
    $process.WaitForExit()
    Write-Output $stderr.Result
    Write-Output (($stdout.Result -split "`n" | Select-Object -Last 25) -join "`n")
    throw $failure
} finally {
    if (!$process.HasExited) { $process.Kill() }
    $process.WaitForExit()
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
