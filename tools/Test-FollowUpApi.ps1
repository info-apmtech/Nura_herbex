param([string]$ApiDll = (Join-Path $PSScriptRoot '../src/Nuraherbex.Api/bin/Release/net10.0/Nuraherbex.Api.dll'))
# HTTP-level check of follow-up endpoints: scoping by role, scheduling, completing, reports and notifications.
# Throw-away in-memory API; Windows PowerShell 5.1 compatible; nothing is sent to WhatsApp.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('nura-fu-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$adminPw = [Guid]::NewGuid().ToString('N'); $appSecret = [Guid]::NewGuid().ToString('N'); $empPw = 'Sales-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
@{ Admin = @{ Email = 'admin@example.test'; Password = $adminPw }
   Jwt = @{ Secret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N') }
   FollowUps = @{ WorkerEnabled = $false }
   WhatsApp = @{ VerifyToken = 'v'; AppSecret = $appSecret } } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $testRoot 'appsettings.Local.json')
$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.WorkingDirectory = $testRoot
$start.Arguments = ('"' + (Resolve-Path $ApiDll).Path + '" --contentRoot "' + $testRoot + '" --urls ' + $base + ' --Database:Provider InMemory --Logging:EventLog:LogLevel:Default None')
$start.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
foreach ($k in @($start.EnvironmentVariables.Keys)) { if ($k -like 'Admin__*' -or $k -like 'WhatsApp__*' -or $k -like 'FollowUps__*') { $start.EnvironmentVariables.Remove($k) } }
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()

function Call([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null, [hashtable]$Extra = @{}) {
    $h = @{}; if ($Token) { $h.Authorization = "Bearer $Token" }; foreach ($k in $Extra.Keys) { $h[$k] = $Extra[$k] }
    $req = @{ Uri = "$base$Path"; Method = $Method; Headers = $h; UseBasicParsing = $true; TimeoutSec = 20 }
    if ($null -ne $Body) { $req.ContentType = 'application/json'; if ($Body -is [byte[]]) { $req.Body = $Body } else { $req.Body = ($Body | ConvertTo-Json -Depth 8) } }
    try { $r = Invoke-WebRequest @req; $code = [int]$r.StatusCode; $text = $r.Content }
    catch { $code = [int]$_.Exception.Response.StatusCode; $text = (New-Object IO.StreamReader $_.Exception.Response.GetResponseStream()).ReadToEnd() }
    $obj = $null; if ($text) { try { $obj = $text | ConvertFrom-Json } catch {} }
    @{ Status = $code; Body = $obj }
}
function Send-Lead([string]$Phone, [string]$Name, [string]$Id) {
    $m = @{ from = $Phone; id = $Id; timestamp = '1'; type = 'text'; text = @{ body = 'Hello' } }
    $p = @{ object = 'whatsapp_business_account'; entry = @(@{ id = '1'; changes = @(@{ field = 'messages'; value = @{
        messaging_product = 'whatsapp'; contacts = @(@{ wa_id = $Phone; profile = @{ name = $Name } }); messages = @($m) } }) }) } | ConvertTo-Json -Depth 12 -Compress
    $b = [Text.Encoding]::UTF8.GetBytes($p)
    $h = New-Object Security.Cryptography.HMACSHA256 (,[Text.Encoding]::UTF8.GetBytes($appSecret))
    $sig = 'sha256=' + ((($h.ComputeHash($b)) | ForEach-Object { $_.ToString('x2') }) -join '')
    (Call 'POST' '/api/webhooks/whatsapp' $b $null @{ 'X-Hub-Signature-256' = $sig }).Status
}
function Assert([bool]$Cond, [string]$Msg) { if (-not $Cond) { throw "FAIL: $Msg" }; Write-Output "PASS: $Msg" }
function Login([string]$Email, [string]$Pw) { Call 'POST' '/api/admin/login' @{ email = $Email; password = $Pw } }
function InDays([int]$d, [int]$h = 10) { (Get-Date).ToUniversalTime().Date.AddDays($d).AddHours($h).ToString('o') }

try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($process.HasExited) { throw ('API exited: ' + $stderr.Result) }
        $r = Call 'GET' '/api/admin/verify'; if ($r.Status -in 200, 401, 403) { $ready = $true; break }; Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'API did not start' }
    $admin = (Login 'admin@example.test' $adminPw).Body.token
    $a = (Call 'POST' '/api/admin/employees' @{ name = 'Priya'; email = 'priya@example.test'; role = 'sales'; password = $empPw } $admin).Body.employee.id
    $b = (Call 'POST' '/api/admin/employees' @{ name = 'Karthik'; email = 'karthik@example.test'; role = 'sales'; password = $empPw } $admin).Body.employee.id
    1..4 | ForEach-Object { Send-Lead "91888880000$_" "Lead $_" "f$_" | Out-Null }
    $ta = (Login 'priya@example.test' $empPw).Body.token; $tb = (Login 'karthik@example.test' $empPw).Body.token
    $mineA = (Call 'GET' '/api/admin/leads' $null $ta).Body.leads; $mineB = (Call 'GET' '/api/admin/leads' $null $tb).Body.leads
    $leadA = $mineA[0].id; $leadB = $mineB[0].id

    # --- scheduling ---
    $c = Call 'POST' '/api/admin/followups' @{ leadId = $leadA; type = 'call'; dueAt = (InDays 4); notes = 'Wants 3-month pack' } $ta
    Assert ($c.Status -eq 201) 'sales schedules a follow-up on their own lead'
    Assert ((Call 'POST' '/api/admin/followups' @{ leadId = $leadB; type = 'call'; dueAt = (InDays 4) } $ta).Status -eq 400) "sales cannot schedule on a colleague's lead"
    Assert ((Call 'POST' '/api/admin/followups' @{ leadId = $leadA; type = 'call'; dueAt = (InDays -2) } $ta).Status -eq 400) 'a follow-up in the past is refused'
    Assert ((Call 'POST' '/api/admin/followups' @{ leadId = $leadB; type = 'whatsapp'; dueAt = (InDays 1) } $admin).Status -eq 201) 'admin schedules on any lead'

    # --- visibility ---
    $la = (Call 'GET' '/api/admin/followups?scope=upcoming' $null $ta).Body
    Assert ($la.items.Count -eq 1 -and $la.items[0].leadId -eq $leadA -and $la.counts.upcoming -eq 1) 'sales sees only their own follow-up'
    $lb = (Call 'GET' '/api/admin/followups?scope=upcoming' $null $tb).Body
    Assert ($lb.items.Count -eq 1 -and $lb.items[0].leadId -eq $leadB) 'colleague sees only theirs'
    $all = (Call 'GET' '/api/admin/followups?scope=upcoming' $null $admin).Body
    Assert ($all.items.Count -eq 2) 'admin sees every follow-up'
    Assert ((Call 'GET' "/api/admin/followups?scope=upcoming&assignee=$a" $null $admin).Body.items.Count -eq 1) 'admin can filter by employee'
    $itemA = $la.items[0].id
    Assert ((Call 'PUT' "/api/admin/followups/$itemA/complete" @{ outcome = 'done' } $tb).Status -eq 404) "sales cannot complete a colleague's follow-up"

    $detail = (Call 'GET' "/api/admin/leads/$leadA" $null $ta).Body
    Assert ($detail.followUps.Count -eq 1 -and $null -ne $detail.lead.nextFollowUpAt) 'lead detail lists follow-ups and the next due date'
    Assert ([bool](Call 'GET' '/api/admin/leads' $null $ta).Body.leads[0].nextFollowUpAt) 'lead list shows the next follow-up'

    # --- reschedule / complete / skip ---
    Assert ((Call 'PUT' "/api/admin/followups/$itemA/reschedule" @{ dueAt = (InDays 6) } $ta).Status -eq 200) 'reschedule works'
    Assert ((Call 'PUT' "/api/admin/followups/$itemA/complete" @{ outcome = 'callback' } $ta).Status -eq 400) 'callback needs a time'
    $done = Call 'PUT' "/api/admin/followups/$itemA/complete" @{ outcome = 'interested'; note = 'Will order Friday' } $ta
    Assert ($done.Status -eq 200) 'complete with an outcome'
    Assert ((Call 'GET' "/api/admin/leads/$leadA" $null $ta).Body.lead.status -eq 'interested') "'interested' moved the lead forward"
    Assert ((Call 'GET' '/api/admin/followups?scope=done' $null $ta).Body.items.Count -eq 1) 'completed items move to Done'
    $itemB = $all.items | Where-Object { $_.leadId -eq $leadB } | Select-Object -First 1
    Assert ((Call 'PUT' "/api/admin/followups/$($itemB.id)/skip" $null $tb).Status -eq 200) 'a task can be skipped'

    # --- settings, report, notifications ---
    Assert ((Call 'GET' '/api/admin/followups/settings' $null $ta).Body.steps.Count -eq 7) 'journey plan has 7 steps'
    Assert ((Call 'PUT' '/api/admin/followups/settings' @{ journeyEnabled = $true; autoSend = $false; reminderDays = 3 } $ta).Status -eq 403) 'sales cannot change settings'
    Assert ((Call 'PUT' '/api/admin/followups/settings' @{ journeyEnabled = $true; autoSend = $false; reminderDays = 3 } $admin).Status -eq 200) 'admin changes settings'
    Assert ((Call 'GET' '/api/admin/followups/settings' $null $admin).Body.reminderDays -eq 3) 'settings persist'
    $rep = (Call 'GET' '/api/admin/followups/report?days=30' $null $admin).Body
    Assert ($rep.success -and $rep.journey.Count -eq 7 -and $rep.byEmployee.Count -ge 2) 'admin report: journey funnel and per-employee rows'
    $repA = (Call 'GET' '/api/admin/followups/report?days=30' $null $ta).Body
    Assert (@($repA.byEmployee | Where-Object { $_.employeeId -ne $a }).Count -eq 0) 'sales report only covers themselves'
    $n = Call 'GET' '/api/admin/notifications' $null $ta
    Assert ($n.Status -eq 200 -and $null -ne $n.Body.unread) 'notification list loads'
    Assert ((Call 'PUT' '/api/admin/notifications/read-all' @{} $ta).Status -eq 200) 'mark all read works'
    Assert ((Call 'GET' '/api/admin/followups' $null $null).Status -eq 401) 'follow-ups need a login'
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
