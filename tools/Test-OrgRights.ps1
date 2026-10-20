param([string]$ApiDll = (Join-Path $PSScriptRoot '../src/Nuraherbex.Api/bin/Release/net10.0/Nuraherbex.Api.dll'))
# HTTP-level check of departments, designations, page rights (default closed) and task management.
# Throw-away in-memory API; Windows PowerShell 5.1 compatible; nothing leaves this machine.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('nura-org-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$adminPw = [Guid]::NewGuid().ToString('N'); $appSecret = [Guid]::NewGuid().ToString('N'); $empPw = 'Staff-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
@{ Admin = @{ Email = 'admin@example.test'; Password = $adminPw }
   Jwt = @{ Secret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N') }
   FollowUps = @{ WorkerEnabled = $false }
   Shadowfax = @{ AutoBook = $false }
   WhatsApp = @{ VerifyToken = 'v'; AppSecret = $appSecret } } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $testRoot 'appsettings.Local.json')
$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.WorkingDirectory = $testRoot
$start.Arguments = ('"' + (Resolve-Path $ApiDll).Path + '" --contentRoot "' + $testRoot + '" --urls ' + $base + ' --Database:Provider InMemory --Logging:EventLog:LogLevel:Default None')
$start.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
foreach ($k in @($start.EnvironmentVariables.Keys)) { if ($k -like 'Admin__*' -or $k -like 'WhatsApp__*' -or $k -like 'FollowUps__*' -or $k -like 'Shadowfax__*') { $start.EnvironmentVariables.Remove($k) } }
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()

function Call([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null, [hashtable]$Extra = @{}) {
    $h = @{}; if ($Token) { $h.Authorization = "Bearer $Token" }; foreach ($k in $Extra.Keys) { $h[$k] = $Extra[$k] }
    $req = @{ Uri = "$base$Path"; Method = $Method; Headers = $h; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($null -ne $Body) { $req.ContentType = 'application/json'; if ($Body -is [byte[]]) { $req.Body = $Body } else { $req.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8)) } }
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
function Assert($Cond, [string]$Msg) { if (-not [bool]$Cond) { throw "FAIL: $Msg" }; Write-Output "PASS: $Msg" }
function Login([string]$Email) { (Call 'POST' '/api/admin/login' @{ email = $Email; password = $empPw }).Body.token }
function Staff([string]$Name, [string]$Email, $Designation, $Department = $null, [string]$Role = 'sales') {
    $r = Call 'POST' '/api/admin/employees' @{ name = $Name; email = $Email; role = $Role; password = $empPw; designationId = $Designation; departmentId = $Department } $script:admin
    if ($r.Status -ne 201) { throw "could not create $Name : $($r.Status) $($r.Body.message)" }
    $r.Body.employee.id
}

try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($process.HasExited) { throw ('API exited: ' + $stderr.Result) }
        $r = Call 'GET' '/api/admin/verify'; if ($r.Status -in 200, 401, 403) { $ready = $true; break }; Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'API did not start' }
    $script:admin = (Call 'POST' '/api/admin/login' @{ email = 'admin@example.test'; password = $adminPw }).Body.token

    # --- starter set and catalogue ---
    $deps = (Call 'GET' '/api/admin/departments' $null $admin).Body.departments
    $des = (Call 'GET' '/api/admin/designations' $null $admin).Body.designations
    Assert ($deps.Count -ge 4 -and $des.Count -ge 5) 'a starter set of departments and designations is created on first run'
    $cat = (Call 'GET' '/api/admin/rights/catalog' $null $admin).Body
    Assert ($cat.pages.Count -ge 15 -and $cat.presets.Count -ge 5 -and ($cat.pages | Where-Object { $_.key -eq 'tasks' }).fullLabel -eq "Everyone's records") 'the rights catalogue lists pages with plain-language levels'

    # --- departments and designations ---
    $depSave = Call 'POST' '/api/admin/departments' @{ name = 'Warehouse'; description = 'Packing' } $admin
    Assert ($depSave.Status -eq 201) 'admin creates a department'
    Assert ((Call 'POST' '/api/admin/departments' @{ name = 'warehouse' } $admin).Status -eq 400) 'duplicate department names are refused'
    $wh = ((Call 'GET' '/api/admin/departments' $null $admin).Body.departments | Where-Object { $_.name -eq 'Warehouse' }).id
    $packer = Call 'POST' '/api/admin/designations' @{ name = 'Packer'; departmentId = $wh; level = 2; rights = @{ orders = 2; tasks = 1 } } $admin
    $viewer = Call 'POST' '/api/admin/designations' @{ name = 'Order Viewer'; level = 2; rights = @{ orders = 1 } } $admin
    $orgMgr = Call 'POST' '/api/admin/designations' @{ name = 'Org Manager'; level = 6; rights = @{ team = 2; tasks = 2; leads = 1 } } $admin
    Assert ($packer.Status -eq 201 -and $viewer.Status -eq 201 -and $orgMgr.Status -eq 201) 'admin creates designations with page rights'
    Assert ((Call 'POST' '/api/admin/designations' @{ name = 'Bad'; level = 99; rights = @{} } $admin).Status -eq 400) 'seniority outside 1-10 is refused'
    $dl = (Call 'GET' '/api/admin/designations' $null $admin).Body.designations
    $dPacker = ($dl | Where-Object { $_.name -eq 'Packer' }).id; $dViewer = ($dl | Where-Object { $_.name -eq 'Order Viewer' }).id; $dMgr = ($dl | Where-Object { $_.name -eq 'Org Manager' }).id
    Assert ((($dl | Where-Object { $_.name -eq 'Packer' }).rights.orders) -eq 2 -and ($dl | Where-Object { $_.name -eq 'Packer' }).departmentName -eq 'Warehouse') 'rights and department are stored on the designation'

    # --- people ---
    $xId = Staff 'Packer Pat' 'pat@example.test' $dPacker $wh
    $yId = Staff 'Viewer Vik' 'vik@example.test' $dViewer
    $zId = Staff 'Default Dee' 'dee@example.test' $null
    $wId = Staff 'Manager Mo' 'mo@example.test' $dMgr
    $tx = Login 'pat@example.test'; $ty = Login 'vik@example.test'; $tz = Login 'dee@example.test'; $tw = Login 'mo@example.test'
    $vx = (Call 'GET' '/api/admin/verify' $null $tx).Body.user
    Assert ($vx.rights.orders -eq 2 -and $vx.designation -eq 'Packer' -and $vx.department -eq 'Warehouse' -and -not $vx.rights.payments) 'the signed-in person receives their page rights, designation and department'
    $vz = (Call 'GET' '/api/admin/verify' $null $tz).Body.user
    Assert ($vz.rights.leads -eq 1 -and $vz.rights.tasks -eq 1 -and -not $vz.rights.orders) 'a person with no designation keeps the original sales-staff pages'
    Assert ((Call 'GET' '/api/admin/verify' $null $admin).Body.user.rights.team -eq 2) 'the owner has every page'

    # --- enforcement ---
    Assert ((Call 'GET' '/api/admin/orders' $null $tx).Status -eq 200) 'a designation with orders access can read orders'
    Assert ((Call 'GET' '/api/admin/payments' $null $tx).Status -eq 403) 'a page the designation lacks is closed'
    Assert ((Call 'GET' '/api/admin/leads' $null $tx).Status -eq 403) 'leads are closed to a packer'
    Assert ((Call 'GET' '/api/admin/employees' $null $tx).Status -eq 403) 'the team list is closed without the team page'
    Assert ((Call 'GET' '/api/admin/orders' $null $ty).Status -eq 200) 'view-only can read'
    Assert ((Call 'POST' '/api/admin/orders/NH-NONE/deliver' @{} $ty).Status -eq 403) 'view-only cannot change orders'
    Assert ((Call 'POST' '/api/admin/orders/NH-NONE/deliver' @{} $tx).Status -ne 403) 'full access reaches the order action (not blocked by rights)'
    Assert ((Call 'GET' '/api/admin/orders' $null $tz).Status -eq 403) 'default staff cannot open orders'
    Assert ((Call 'GET' '/api/admin/leads' $null $tz).Status -eq 200) 'default staff can still open leads'
    Assert ((Call 'GET' '/api/admin/whatsapp/messages' $null $ty).Status -eq 403) 'the WhatsApp log is closed to view-only order staff'
    Assert ((Call 'GET' '/api/admin/some-new-page' $null $tx).Status -eq 403 -and (Call 'GET' '/api/admin/some-new-page' $null $admin).Status -eq 404) 'unknown admin routes are closed to staff by default'
    Assert ((Call 'GET' '/api/admin/notifications' $null $tx).Status -eq 200 -and (Call 'GET' '/api/admin/team?for=tasks' $null $tx).Status -eq 200) 'notifications and the people list need no page'
    Assert ((Call 'GET' '/api/admin/crm/dashboard' $null $tx).Status -eq 403) 'the sales dashboard is closed to a packer'

    # --- changes apply at once ---
    $edit = Call 'PUT' "/api/admin/designations/$dPacker" @{ name = 'Packer'; departmentId = $wh; level = 2; rights = @{ orders = 2; tasks = 1; payments = 1 } } $admin
    Assert ($edit.Status -eq 200) 'admin edits the rights of a designation'
    Assert ((Call 'GET' '/api/admin/payments' $null $tx).Status -eq 200) 'the new right works at once, without signing in again'
    Call 'PUT' "/api/admin/employees/$yId" @{ name = 'Viewer Vik'; email = 'vik@example.test'; role = 'sales'; isActive = $true; designationId = $dPacker } $admin | Out-Null
    Assert ((Call 'GET' '/api/admin/payments' $null $ty).Status -eq 200) 'moving a person to another designation changes their access at once'

    # --- who may grant what ---
    Assert ((Call 'GET' '/api/admin/employees' $null $tw).Status -eq 200) 'a manager with the team page can list people'
    Assert ((Call 'POST' '/api/admin/designations' @{ name = 'Too Much'; level = 3; rights = @{ orders = 2 } } $tw).Status -eq 400) 'a manager cannot grant a page they do not hold'
    Assert ((Call 'POST' '/api/admin/designations' @{ name = 'Task Helper'; level = 3; rights = @{ tasks = 1 } } $tw).Status -eq 201) 'a manager can create a designation within their own access'
    Assert ((Call 'POST' '/api/admin/employees' @{ name = 'Sneaky'; email = 'sneaky@example.test'; role = 'admin'; password = $empPw } $tw).Status -eq 400) 'a manager cannot create an administrator'
    Assert ((Call 'POST' '/api/admin/employees' @{ name = 'Boosted'; email = 'boost@example.test'; role = 'sales'; password = $empPw; designationId = $dPacker } $tw).Status -eq 400) 'a manager cannot give a designation that exceeds their own access'
    Assert ((Call 'PUT' "/api/admin/designations/$dMgr" @{ name = 'Org Manager'; level = 6; rights = @{ team = 2; tasks = 2; leads = 1; orders = 2 } } $tw).Status -eq 400) 'a manager cannot raise the rights of their own designation'
    Assert ((Call 'PUT' "/api/admin/employees/$wId" @{ name = 'Manager Mo'; email = 'mo@example.test'; role = 'sales'; isActive = $true; designationId = $dPacker } $tw).Status -eq 400) 'a manager cannot change their own designation'
    Assert ((Call 'DELETE' "/api/admin/designations/$dPacker" $null $admin).Status -eq 400) 'a designation held by people cannot be deleted'
    Assert ((Call 'DELETE' "/api/admin/departments/$wh" $null $admin).Status -eq 400) 'a department in use cannot be deleted'

    # --- leads rotation only reaches people whose designation allows leads ---
    1..6 | ForEach-Object { Send-Lead "91777770000$_" "Lead $_" "o$_" | Out-Null }
    $own = (Call 'GET' '/api/admin/leads' $null $admin).Body.leads | ForEach-Object { $_.assignedToEmployeeId } | Sort-Object -Unique
    Assert ($own -notcontains $xId -and $own -notcontains $yId -and ($own -contains $zId)) 'new leads go only to people whose designation includes leads'

    # --- tasks ---
    $due = (Get-Date).ToUniversalTime().AddDays(2).ToString('o')
    $t1 = Call 'POST' '/api/admin/tasks' @{ title = 'Pack the Chennai orders'; description = 'Before 5 pm'; priority = 'high'; dueAt = $due; assignedToEmployeeId = $yId; departmentId = $wh; checklist = @('Print labels', 'Pack', 'Hand over') } $tx
    Assert ($t1.Status -eq 201) 'a staff member creates a task for a colleague'
    $tid = $t1.Body.task.id
    $mine = (Call 'GET' '/api/admin/tasks?assignee=me' $null $ty).Body
    Assert ($mine.items.Count -eq 1 -and $mine.items[0].title -eq 'Pack the Chennai orders' -and $mine.items[0].checklistTotal -eq 3 -and $mine.items[0].departmentName -eq 'Warehouse') 'the assignee sees it with its checklist and department'
    Assert ((Call 'GET' '/api/admin/tasks' $null $tz).Body.items.Count -eq 0 -and (Call 'GET' "/api/admin/tasks/$tid" $null $tz).Status -eq 404) 'someone not involved cannot see the task'
    Assert ((Call 'GET' '/api/admin/tasks' $null $tw).Body.items.Count -eq 1) 'full task access sees every task'
    $nots = (Call 'GET' '/api/admin/notifications' $null $ty).Body
    Assert ((@($nots.items | Where-Object { $_.title -like "New task:*" })).Count -eq 1) "the assignee is notified"
    $det = (Call 'GET' "/api/admin/tasks/$tid" $null $ty).Body
    Assert ($det.checklist.Count -eq 3 -and $det.activity.Count -ge 1) 'task detail has the checklist and an activity log'
    $item = $det.checklist[0].id
    Assert ((Call 'PUT' "/api/admin/tasks/$tid/checklist/$item" @{ done = $true } $ty).Status -eq 200 -and (Call 'GET' '/api/admin/tasks?assignee=me' $null $ty).Body.items[0].checklistDone -eq 1) 'a checklist item can be ticked'
    Assert ((Call 'POST' "/api/admin/tasks/$tid/checklist" @{ text = 'Photograph the parcels' } $ty).Status -eq 200) 'a checklist item can be added'
    Assert ((Call 'POST' "/api/admin/tasks/$tid/comments" @{ body = 'Labels printed' } $ty).Status -eq 200 -and (Call 'GET' "/api/admin/tasks/$tid" $null $tx).Body.comments.Count -eq 1) 'comments are shared with everyone on the task'
    Assert ((Call 'GET' '/api/admin/notifications' $null $tx).Body.items | Where-Object { $_.title -like 'New comment*' }) 'the creator is notified of the comment'
    Assert ((Call 'PUT' "/api/admin/tasks/$tid/status" @{ status = 'in_progress' } $ty).Status -eq 200) 'the assignee can move the task to in progress'
    Assert ((Call 'PUT' "/api/admin/tasks/$tid/status" @{ status = 'nonsense' } $ty).Status -eq 400) 'an unknown status is refused'
    Assert ((Call 'DELETE' "/api/admin/tasks/$tid" $null $ty).Status -eq 403) 'an assignee who did not create the task cannot delete it'
    Assert ((Call 'PUT' "/api/admin/tasks/$tid/status" @{ status = 'done' } $ty).Status -eq 200) 'the assignee completes it'
    Assert ((Call 'GET' '/api/admin/notifications' $null $tx).Body.items | Where-Object { $_.title -like 'Task done*' }) 'the creator is told it is done'
    Assert ((Call 'GET' '/api/admin/tasks?scope=done' $null $tx).Body.items.Count -eq 1) 'completed tasks move to Done'

    $rep = Call 'POST' '/api/admin/tasks' @{ title = 'Weekly stock count'; priority = 'normal'; dueAt = $due; repeat = 'weekly'; assignedToEmployeeId = $xId } $tx
    $rid = $rep.Body.task.id
    Call 'PUT' "/api/admin/tasks/$rid/status" @{ status = 'done' } $tx | Out-Null
    $open = (Call 'GET' '/api/admin/tasks?q=Weekly' $null $tx).Body.items
    Assert ($open.Count -eq 1 -and $open[0].id -ne $rid -and $open[0].repeat -eq 'weekly') 'finishing a repeating task creates the next one'
    Assert ((Call 'POST' '/api/admin/tasks' @{ title = ''; priority = 'normal' } $tx).Status -eq 400 -and (Call 'POST' '/api/admin/tasks' @{ title = 'x'; repeat = 'weekly' } $tx).Status -eq 400) 'a task needs a title, and a repeat needs a due date'
    Assert ((Call 'DELETE' "/api/admin/tasks/$rid" $null $tx).Status -eq 200 -and (Call 'GET' "/api/admin/tasks/$rid" $null $tx).Status -eq 404) 'the creator can delete their task'

    # removing a person returns their open tasks to unassigned
    $t3 = Call 'POST' '/api/admin/tasks' @{ title = 'Call supplier'; priority = 'low'; assignedToEmployeeId = $zId } $tw
    Call 'DELETE' "/api/admin/employees/$zId" $null $admin | Out-Null
    Assert ((Call 'GET' "/api/admin/tasks/$($t3.Body.task.id)" $null $tw).Body.task.assignedToEmployeeId -eq $null) 'removing a person returns their open tasks to unassigned'
    Assert ((Call 'GET' '/api/admin/tasks?assignee=unassigned' $null $admin).Body.counts.unassigned -ge 1) 'unassigned tasks are counted'
    Assert ((Call 'GET' '/api/admin/tasks' $null $null).Status -eq 401) 'tasks need a login'
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
