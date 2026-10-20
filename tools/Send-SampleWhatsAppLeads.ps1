param(
    [string]$Api = 'http://localhost:5118',
    # Signing needs the Meta app secret; it is read from the API config and never printed.
    [string]$ConfigDir = (Join-Path $PSScriptRoot '../src/Nuraherbex.Api')
)
# Posts correctly signed WhatsApp webhook events to a running API so sample leads appear under Admin -> Leads.
# Sample numbers are fake (91999990xxxx) and none of the texts trigger the auto-reply, so nothing is sent to WhatsApp.
# NOTE: the API writes to whatever database it is configured with. Sample leads are named "[Sample] ..." so they are easy to spot.
$ErrorActionPreference = 'Stop'

$secret = $null
foreach ($f in 'appsettings.Local.json', 'appsettings.Development.json', 'appsettings.json') {
    $p = Join-Path $ConfigDir $f
    if (Test-Path $p) {
        $m = [regex]::Match((Get-Content $p -Raw), '"AppSecret"\s*:\s*"([^"]+)"')
        if ($m.Success) { $secret = $m.Groups[1].Value; break }
    }
}
if (-not $secret) { throw 'WhatsApp:AppSecret not found in the API config.' }

function Send-Event([string]$Phone, [string]$Name, [string]$Text, [string]$Wamid, $Referral = $null) {
    $msg = @{ from = $Phone; id = $Wamid; timestamp = [string][int][double]::Parse((Get-Date -UFormat %s)); type = 'text'; text = @{ body = $Text } }
    if ($Referral) { $msg.referral = $Referral }
    $payload = @{ object = 'whatsapp_business_account'; entry = @(@{ id = '1'; changes = @(@{ field = 'messages'; value = @{
        messaging_product = 'whatsapp'; contacts = @(@{ wa_id = $Phone; profile = @{ name = $Name } }); messages = @($msg) } }) }) } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $hmac = New-Object Security.Cryptography.HMACSHA256 (,[Text.Encoding]::UTF8.GetBytes($secret))
    $sig = 'sha256=' + ((($hmac.ComputeHash($bytes)) | ForEach-Object { $_.ToString('x2') }) -join '')
    $r = Invoke-WebRequest "$Api/api/webhooks/whatsapp" -Method Post -ContentType 'application/json' -Body $bytes -Headers @{ 'X-Hub-Signature-256' = $sig } -UseBasicParsing -TimeoutSec 20
    "{0}  {1,-22} -> HTTP {2}" -f $Phone, $Name, [int]$r.StatusCode
}

$run = [Guid]::NewGuid().ToString('N').Substring(0, 8)
Send-Event '919999900001' '[Sample] Asha Raman'   'Hi team, what is the price of Stamix powder for 30 days?' "wamid.sample.$run.1"
Send-Event '919999900002' '[Sample] Karthik S'    'Do you deliver to Madurai? Is COD available?'            "wamid.sample.$run.2" @{ headline = 'Stamix Diwali Offer'; source_url = 'https://fb.me/sample-ad'; source_id = '1234567890' }
Send-Event '919999900003' '[Sample] Meena Iyer'   'Please call me tomorrow about the 3 month pack'         "wamid.sample.$run.3"
Send-Event '919999900003' '[Sample] Meena Iyer'   'Evening is better for me'                              "wamid.sample.$run.4"
Send-Event '919999900004' '[Sample] Rahul Verma'  'Is this safe with diabetes medicines?'                   "wamid.sample.$run.5"
Write-Output "`nOpen Admin -> Leads. Expect 4 sample leads (Meena has 2 messages but only one lead)."
