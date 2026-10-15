param([string]$ApiDll = (Join-Path $PSScriptRoot '../src/Nuraherbex.Api/bin/Release/net10.0/Nuraherbex.Api.dll'))
# End-to-end check of employees, staff login, role scoping and lead assignment against a throw-away in-memory API.
# Windows PowerShell 5.1 compatible. Nothing is sent to WhatsApp and no real database is touched.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('nura-emp-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$adminPw = [Guid]::NewGuid().ToString('N'); $appSecret = [Guid]::NewGuid().ToString('N'); $empPw = 'Sales-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
@{ Admin = @{ Email = 'admin@example.test'; Password = $adminPw }
   Jwt = @{ Secret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N') }
   WhatsApp = @{ VerifyToken = 'v'; AppSecret = $appSecret } } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $testRoot 'appsettings.Local.json')
$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.WorkingDirectory = $testRoot
$start.Arguments = ('"' + (Resolve-Path $ApiDll).Path + '" --contentRoot "' + $testRoot + '" --urls ' + $base + ' --Database:Provider InMemory --Logging:EventLog:LogLevel:Default None')
$start.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
foreach ($k in @($start.EnvironmentVariables.Keys)) { if ($k -like 'Admin__*' -or $k -like 'WhatsApp__*') { $start.EnvironmentVariables.Remove($k) } }
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()

# Returns @{ Status; Body } for success and HTTP errors alike (5.1 has no -SkipHttpErrorCheck).
function Call([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null, [hashtable]$Extra = @{}) {
    $h = @{}; if ($Token) { $h.Authorization = "Bearer $Token" }; foreach ($k in $Extra.Keys) { $h[$k] = $Extra[$k] }
    $req = @{ Uri = "$base$Path"; Method = $Method; Headers = $h; UseBasicParsing = $true; TimeoutSec = 20 }
    if ($null -ne $Body) { $req.ContentType = "application/json"; if ($Body -is [byte[]]) { $req.Body = $Body } else { $req.Body = ($Body | ConvertTo-Json -Depth 8) } }
    try { $r = Invoke-WebRequest @req; $code = [int]$r.StatusCode; $text = $r.Content }
    catch { $code = [int]$_.Exception.Response.StatusCode; $text = (New-Object IO.StreamReader $_.Exception.Response.GetResponseStream()).ReadToEnd() }
    $obj = $null; if ($text) { try { $obj = $text | ConvertFrom-Json } catch {} }
    @{ Status = $code; Body = $obj }
}
function Send-Lead([string]$Phone, [string]$Name, [string]$Text, [string]$Id) {
    $m = @{ from = $Phone; id = $Id; timestamp = '1'; type = 'text'; text = @{ body = $Text } }
    $p = @{ object = 'whatsapp_business_account'; entry = @(@{ id = '1'; changes = @(@{ field = 'messages'; value = @{
        messaging_product = 'whatsapp'; contacts = @(@{ wa_id = $Phone; profile = @{ name = $Name } }); messages = @($m) } }) }) } | ConvertTo-Json -Depth 12 -Compress
    $b = [Text.Encoding]::UTF8.GetBytes($p)
    $h = New-Object Security.Cryptography.HMACSHA256 (,[Text.Encoding]::UTF8.GetBytes($appSecret))
    $sig = 'sha256=' + ((($h.ComputeHash($b)) | ForEach-Object { $_.ToString('x2') }) -join '')
    (Call 'POST' '/api/webhooks/whatsapp' $b $null @{ 'X-Hub-Signature-256' = $sig }).Status
}
function Assert([bool]$Cond, [string]$Msg) { if (-not $Cond) { throw "FAIL: $Msg" }; Write-Output "PASS: $Msg" }
function Login([string]$Email, [string]$Pw) { Call 'POST' '/api/admin/login' @{ email = $Email; password = $Pw } }

try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($process.HasExited) { throw ('API exited: ' + $stderr.Result) }
        $r = Call 'GET' '/api/admin/verify'; if ($r.Status -in 200, 401, 403) { $ready = $true; break }; Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'API did not start' }

    $admin = (Login 'admin@example.test' $adminPw).Body.token
    Assert ((Call 'GET' '/api/admin/verify' $null $admin).Body.user.role -eq 'admin') 'configured admin verifies as admin'

    # --- employees ---
    Assert ((Call 'POST' '/api/admin/employees' @{ name = 'Short'; email = 'short@example.test'; role = 'sales'; password = '123' } $admin).Status -eq 400) 'password under 8 characters rejected'
    $a = (Call 'POST' '/api/admin/employees' @{ name = 'Priya Raman'; email = 'Priya@Example.test'; phone = '9000000001'; role = 'sales'; password = $empPw } $admin)
    $b = (Call 'POST' '/api/admin/employees' @{ name = 'Karthik S'; email = 'karthik@example.test'; phone = '9000000002'; role = 'sales'; password = $empPw } $admin)
    Assert ($a.Status -eq 201 -and $b.Status -eq 201) 'two sales employees created'
    Assert ($a.Body.employee.email -eq 'priya@example.test') 'email stored lower-case'
    Assert ((Call 'POST' '/api/admin/employees' @{ name = 'Dup'; email = 'PRIYA@example.test'; role = 'sales'; password = $empPw } $admin).Status -eq 400) 'duplicate email rejected'
    $aId = $a.Body.employee.id; $bId = $b.Body.employee.id

    # --- round-robin on new leads ---
    1..4 | ForEach-Object { Send-Lead "91999990000$_" "Lead $_" "Hello $_" "w$_" | Out-Null }
    $all = (Call 'GET' '/api/admin/leads' $null $admin).Body
    $byName = @{}; $all.leads | ForEach-Object { $byName[$_.name] = $_ }
    Assert (($all.total -eq 4) -and ($all.unassignedCount -eq 0)) 'all 4 leads were auto-assigned'
    Assert (($byName['Lead 1'].assignedToEmployeeId -ne $byName['Lead 2'].assignedToEmployeeId) -and ($byName['Lead 1'].assignedToEmployeeId -eq $byName['Lead 3'].assignedToEmployeeId)) 'assignment alternates between the two employees (round-robin)'

    # --- sales login and scoping ---
    $sa = Login 'priya@example.test' $empPw
    Assert ($sa.Status -eq 200 -and $sa.Body.user.role -eq 'sales' -and $sa.Body.user.employeeId -eq $aId) 'sales executive can sign in'
    $ta = $sa.Body.token
    Assert ((Login 'priya@example.test' 'wrong-password').Status -eq 401) 'wrong employee password rejected'
    $mine = (Call 'GET' '/api/admin/leads' $null $ta).Body
    Assert ($mine.total -eq 2 -and (@($mine.leads | Where-Object { $_.assignedToEmployeeId -ne $aId }).Count -eq 0)) 'sales sees only their own 2 leads'
    $others = $all.leads | Where-Object { $_.assignedToEmployeeId -eq $bId }
    $foreign = $others[0].id
    Assert ((Call 'GET' "/api/admin/leads/$foreign" $null $ta).Status -eq 404) "sales cannot open a colleague's lead"
    Assert ((Call 'PUT' "/api/admin/leads/$foreign/status" @{ status = 'converted' } $ta).Status -eq 404) "sales cannot change a colleague's lead status"
    Assert ((Call 'GET' '/api/admin/employees' $null $ta).Status -eq 403) 'sales cannot list employees (admin only)'
    Assert ((Call 'GET' '/api/admin/orders' $null $ta).Status -eq 403) 'sales cannot read store orders'
    Assert ((Call 'POST' '/api/admin/employees' @{ name = 'X'; email = 'x@example.test'; role = 'admin'; password = $empPw } $ta).Status -eq 403) 'sales cannot create employees'

    # --- transfer ---
    $own = $mine.leads[0].id
    Assert ((Call 'PUT' "/api/admin/leads/$own/assign" @{ employeeId = $null } $ta).Status -eq 403) 'sales cannot unassign a lead'
    $t = Call 'PUT' "/api/admin/leads/$own/assign" @{ employeeId = $bId; reason = 'Speaks Tamil' } $ta
    Assert ($t.Status -eq 200) 'sales owner can transfer their lead'
    Assert ((Call 'GET' '/api/admin/leads' $null $ta).Body.total -eq 1) 'transferred lead leaves the sender list'
    $d = (Call 'GET' "/api/admin/leads/$own" $null $admin).Body
    Assert ($d.assignments.Count -ge 2 -and $d.assignments[0].reason -eq 'Speaks Tamil') 'assignment history records the transfer and reason'

    # --- auto-assign switch ---
    Assert ((Call 'PUT' '/api/admin/leads/settings' @{ autoAssign = $false } $admin).Status -eq 200) 'auto-assign can be switched off'
    Send-Lead '919999900099' 'Manual Queue' 'Hi' 'w99' | Out-Null
    $q = (Call 'GET' '/api/admin/leads?assignee=unassigned' $null $admin).Body
    Assert ($q.total -eq 1 -and $q.leads[0].name -eq 'Manual Queue') 'with auto-assign off the new lead waits unassigned'
    Assert ((Call 'PUT' "/api/admin/leads/$($q.leads[0].id)/assign" @{ employeeId = $aId } $admin).Status -eq 200) 'admin assigns it manually'
    Call 'PUT' '/api/admin/leads/settings' @{ autoAssign = $true } $admin | Out-Null

    # --- removal redistributes open leads and locks the login ---
    $tb = (Login 'karthik@example.test' $empPw).Body.token
    $before = (Call 'GET' '/api/admin/leads' $null $tb).Body.total
    Assert ($before -ge 2) 'second employee holds leads before removal'
    $rm = Call 'DELETE' "/api/admin/employees/$bId" $null $admin
    Assert ($rm.Status -eq 200) 'employee removed'
    Assert ((Call 'GET' '/api/admin/leads' $null $tb).Status -eq 401) 'removed employee existing token is rejected immediately'
    Assert ((Login 'karthik@example.test' $empPw).Status -eq 401) 'removed employee cannot sign in'
    $after = (Call 'GET' '/api/admin/leads' $null $ta).Body
    Assert ($after.total -ge 3) 'removed employee leads were redistributed to the remaining team'
    Assert ((Call 'GET' '/api/admin/leads?assignee=unassigned' $null $admin).Body.total -eq 0) 'no lead was orphaned'
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
