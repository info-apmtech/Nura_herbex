param([string]$ApiDll = (Join-Path $PSScriptRoot '../src/Nuraherbex.Api/bin/Release/net10.0/Nuraherbex.Api.dll'))
# HTTP-level check of selling from a lead (order creation, customer view) and the CRM dashboard.
# Throw-away in-memory API with the seeded demo products; Windows PowerShell 5.1 compatible; nothing is sent anywhere.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('nura-sales-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$adminPw = [Guid]::NewGuid().ToString('N'); $appSecret = [Guid]::NewGuid().ToString('N'); $empPw = 'Sales-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
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
$start.Arguments = ('"' + (Resolve-Path $ApiDll).Path + '" --contentRoot "' + $testRoot + '" --urls ' + $base + ' --Database:Provider InMemory --Database:SeedDemoBatch true --Logging:EventLog:LogLevel:Default None')
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
$addr = @{ addressLine1 = '12 Gandhi Road'; city = 'Chennai'; state = 'Tamil Nadu'; pincode = '600001' }

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
    Send-Lead '919876501111' 'Asha Raman' 'Hi, I want Stamix' 's1' | Out-Null
    Send-Lead '919876502222' 'Rahul Verma' 'STOP' 's2' | Out-Null
    $ta = (Login 'priya@example.test' $empPw).Body.token; $tb = (Login 'karthik@example.test' $empPw).Body.token
    $mine = (Call 'GET' '/api/admin/leads' $null $ta).Body.leads
    $lead = $mine[0]
    $product = (Call 'GET' '/api/products').Body.products[0]
    Assert ($null -ne $product.id) 'demo products are available'

    $good = @{ items = @(@{ productId = $product.id; quantity = 2 }); email = 'asha@example.test'; address = $addr; alreadyPaid = $false }
    Assert ((Call 'POST' "/api/admin/leads/$($lead.id)/order" $good $tb).Status -eq 404) "a colleague cannot sell on someone else's lead"
    Assert ((Call 'POST' "/api/admin/leads/$($lead.id)/order" @{ items = @(); email = 'a@example.test'; address = $addr } $ta).Status -eq 400) 'an order needs at least one product'
    Assert ((Call 'POST' "/api/admin/leads/$($lead.id)/order" @{ items = $good.items; email = 'not-an-email'; address = $addr } $ta).Status -eq 400) 'an order needs a valid email'
    Assert ((Call 'POST' "/api/admin/leads/$($lead.id)/order" @{ items = $good.items; email = 'a@example.test'; address = @{ addressLine1 = 'x'; city = 'c'; state = 's'; pincode = '12' } } $ta).Status -eq 400) 'an order needs a valid pincode'

    $o1 = Call 'POST' "/api/admin/leads/$($lead.id)/order" $good $ta
    Assert ($o1.Status -eq 201 -and $o1.Body.orderId -like 'NH-*' -and $o1.Body.totalAmount -gt 0) 'sales creates a cash-on-delivery order for their lead'
    $o2 = Call 'POST' "/api/admin/leads/$($lead.id)/order" @{ items = @(@{ productId = $product.id; quantity = 1 }); email = 'asha@example.test'; address = $addr; alreadyPaid = $true; paymentReference = 'UPI-123' } $ta
    Assert ($o2.Status -eq 201) 'sales creates an already-paid order'

    $detail = (Call 'GET' "/api/admin/leads/$($lead.id)" $null $ta).Body
    Assert ($detail.lead.status -eq 'converted') 'the lead became a customer'
    Assert ($detail.orders.Count -eq 2 -and $detail.lead.orderCount -eq 2) 'the customer view lists both orders'
    $expected = [decimal]$o1.Body.totalAmount + [decimal]$o2.Body.totalAmount
    Assert ([decimal]$detail.lead.lifetimeValue -eq $expected) 'lifetime value is the sum of the orders'
    Assert ((($detail.orders | Where-Object { $_.paymentMethod -ne 'COD' }).paymentStatus) -eq 'PAID') 'the already-paid order is marked paid'
    Assert ($detail.lead.assignedToEmployeeId -eq $a) 'selling keeps the lead with its owner'
    $row = ((Call 'GET' '/api/admin/leads' $null $ta).Body.leads | Where-Object { $_.id -eq $lead.id })
    Assert ($row.orderCount -eq 2 -and [decimal]$row.lifetimeValue -eq $expected) 'the lead list shows orders and lifetime value'

    $stop = (Call 'GET' '/api/admin/leads' $null $admin).Body.leads | Where-Object { $_.name -eq 'Rahul Verma' }
    Assert ((Call 'POST' "/api/admin/leads/$($stop.id)/order" $good $admin).Status -eq 400) 'no order for a customer who opted out'

    # --- customer media: stickers, photos and reactions ---
    function Send-Raw([string]$Phone, $Msg) {
        $p = @{ object = 'whatsapp_business_account'; entry = @(@{ id = '1'; changes = @(@{ field = 'messages'; value = @{
            messaging_product = 'whatsapp'; contacts = @(@{ wa_id = $Phone; profile = @{ name = 'Asha Raman' } }); messages = @($Msg) } }) }) } | ConvertTo-Json -Depth 12 -Compress
        $b = [Text.Encoding]::UTF8.GetBytes($p)
        $h = New-Object Security.Cryptography.HMACSHA256 (,[Text.Encoding]::UTF8.GetBytes($appSecret))
        $sig = 'sha256=' + ((($h.ComputeHash($b)) | ForEach-Object { $_.ToString('x2') }) -join '')
        (Call 'POST' '/api/webhooks/whatsapp' $b $null @{ 'X-Hub-Signature-256' = $sig }).Status
    }
    Assert ((Send-Raw '919876501111' @{ from = '919876501111'; id = 'wamid.sticker1'; timestamp = '1'; type = 'sticker'; sticker = @{ id = 'MEDIA-ST-1'; mime_type = 'image/webp' } }) -eq 200) 'a sticker webhook is accepted'
    Assert ((Send-Raw '919876501111' @{ from = '919876501111'; id = 'wamid.react1'; timestamp = '2'; type = 'reaction'; reaction = @{ message_id = 'wamid.sticker1'; emoji = [char]::ConvertFromUtf32(0x1F44D) } }) -eq 200) 'a reaction webhook is accepted'
    $msgs = (Call 'GET' "/api/admin/leads/$($lead.id)" $null $ta).Body.messages
    $stk = $msgs | Where-Object { $_.type -eq 'sticker' } | Select-Object -First 1
    Assert ($null -ne $stk -and $stk.mediaUrl -like 'api/whatsapp-media/*' -and $stk.mediaMime -eq 'image/webp') 'the chat receives the sticker with a signed link'
    Assert ($stk.reaction -eq [char]::ConvertFromUtf32(0x1F44D)) 'the reaction is attached to the sticker message'
    Assert (-not ($msgs | Where-Object { $_.type -eq 'reaction' })) 'a reaction does not appear as its own message'
    Assert ((Call 'GET' "/api/leads-unused" $null $null).Status -eq 404) 'sanity: unknown route is 404'
    $mediaPath = '/' + $stk.mediaUrl
    Assert ((Call 'GET' $mediaPath).Status -eq 404) 'a valid link to a file that could not be downloaded is 404, not an error'
    $badPath = $mediaPath -replace 'sig=[0-9a-f]{4}', 'sig=0000'
    Assert ((Call 'GET' $badPath).Status -eq 403) 'a tampered link is refused'
    $oldPath = $mediaPath -replace 'exp=\d+', 'exp=1000000000'
    Assert ((Call 'GET' $oldPath).Status -eq 403) 'an expired link is refused'
    Assert ((Call 'GET' "/api/whatsapp-media/$($stk.id)").Status -eq 403) 'a link without a signature is refused'

    # --- reply assistant (no AI key here, so the built-in guide answers) ---
    $sug = Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{} $ta
    Assert ($sug.Status -eq 200 -and $sug.Body.source -eq 'guide' -and $sug.Body.reply -like '*Asha*' -and $sug.Body.reply.Length -gt 20) 'sales gets a drafted reply for their own lead'
    Assert ($sug.Body.respondingTo -eq '[sticker]' -and $sug.Body.intent -eq 'Sticker') 'the draft answers the latest message, even when it is only a sticker'
    Send-Raw '919876501111' @{ from = '919876501111'; id = 'wamid.price2'; timestamp = '3'; type = 'text'; text = @{ body = 'What is the price of Stamix?' } } | Out-Null
    $draft0 = Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{ fresh = $true } $ta
    foreach ($case in @(@('make it shorter', 'Hi Asha'), @('make it warmer and friendlier', 'Hi Asha!'), @('make it more formal and professional', 'Dear Asha'))) {
        $tw = Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{ instruction = $case[0]; currentDraft = $draft0.Body.reply } $ta
        Assert ($tw.Status -eq 200 -and $tw.Body.reply -ne $draft0.Body.reply -and $tw.Body.reply.StartsWith($case[1])) ("the button '" + $case[0] + "' really changes the draft")
    }
    $ta1 = Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{ instruction = 'translate it into natural Tamil'; currentDraft = $draft0.Body.reply } $ta
    Assert ($ta1.Status -eq 200 -and $ta1.Body.reply -ne $draft0.Body.reply -and $ta1.Body.reply -match 'Asha') 'the Tamil button gives a Tamil reply'
    $hi1 = Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{ instruction = 'translate it into natural Hindi'; currentDraft = $ta1.Body.reply } $ta
    Assert ($hi1.Status -eq 200 -and $hi1.Body.reply -ne $ta1.Body.reply) 'the Hindi button gives a Hindi reply'
    Assert ((Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{} $tb).Status -eq 404) "a colleague cannot draft replies for someone else's lead"
    Assert ((Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{} $null).Status -eq 401) 'drafting needs a login'
    $stopSug = Call 'POST' "/api/admin/leads/$($stop.id)/suggest-reply" @{ fresh = $true } $admin
    Assert ($stopSug.Status -eq 200 -and $stopSug.Body.needsReview) 'a flagged message is marked for review'
    $rs = (Call 'GET' '/api/admin/ai/reply-settings' $null $ta).Body
    Assert ($rs.enabled -and $rs.knowledge -like '*Never promise medical results*' -and -not $rs.aiConnected) 'settings show the safe default knowledge and no AI key'
    Assert ((Call 'PUT' '/api/admin/ai/reply-settings' @{ enabled = $false; knowledge = 'x' } $ta).Status -eq 403) 'sales cannot change the assistant settings'
    Assert ((Call 'PUT' '/api/admin/ai/reply-settings' @{ enabled = $false; knowledge = 'Custom facts' } $admin).Status -eq 200) 'admin switches the assistant off'
    Assert ((Call 'POST' "/api/admin/leads/$($lead.id)/suggest-reply" @{ fresh = $true } $ta).Status -eq 409) 'no drafts while the assistant is off'
    Call 'PUT' '/api/admin/ai/reply-settings' @{ enabled = $true; knowledge = 'Custom facts' } $admin | Out-Null
    Assert ((Call 'GET' '/api/admin/ai/reply-settings' $null $ta).Body.knowledge -eq 'Custom facts') 'the team text is saved'

    # --- dashboard ---
    $d = (Call 'GET' '/api/admin/crm/dashboard?days=30' $null $admin).Body
    Assert ($d.success -and $d.newLeads -ge 2 -and $d.orders -eq 2 -and [decimal]$d.revenue -eq $expected) 'dashboard counts leads, orders and sales'
    Assert ($d.conversionRate -gt 0 -and $d.funnel.Count -eq 5) 'dashboard shows conversion and the funnel'
    Assert ((($d.bySource | Where-Object { $_.source -eq 'whatsapp' }).revenue) -eq $expected) 'sales are credited to the lead source'
    Assert ((($d.byEmployee | Where-Object { $_.name -eq 'Priya' }).revenue) -eq $expected) 'sales are credited to the owner'
    $dp = (Call 'GET' '/api/admin/crm/dashboard?days=30' $null $ta).Body
    Assert ($dp.personal -and [decimal]$dp.revenue -eq $expected -and @($dp.byEmployee | Where-Object { $_.employeeId -ne $a }).Count -eq 0) 'a sales dashboard only covers their own work'
    $db = (Call 'GET' '/api/admin/crm/dashboard?days=30' $null $tb).Body
    Assert ([decimal]$db.revenue -eq 0 -and $db.orders -eq 0) "a colleague's dashboard does not include those sales"
    Assert ((Call 'GET' '/api/admin/crm/dashboard' $null $null).Status -eq 401) 'the dashboard needs a login'
} catch {
    $failure = $_
    if (!$process.HasExited) { $process.Kill() }
    $process.WaitForExit()
    Write-Output $stderr.Result
    Write-Output (($stdout.Result -split "`n" | Select-Object -Last 30) -join "`n")
    throw $failure
} finally {
    if (!$process.HasExited) { $process.Kill() }
    $process.WaitForExit()
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
