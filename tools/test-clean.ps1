Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Bitmap]::FromFile("C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\.user_uploaded\media_1792652877123.png")

# Let's clone src
$clean = New-Object System.Drawing.Bitmap $src.Width, $src.Height
$g = [System.Drawing.Graphics]::FromImage($clean)
$g.DrawImage($src, 0, 0, $src.Width, $src.Height)

# In media_1, the text is roughly from X=40 to 320, Y=25 to 290.
# The background on the left is a smooth linear/radial gradient:
# At (0,0): ~ (245, 238, 248)
# At (340, 0): ~ (225, 215, 235)
# At (0, 315): ~ (240, 230, 245)
# At (340, 315): ~ (215, 205, 225)
# And around (300..340) there are green leaves poking in.

Write-Host "Creating clean hero banner..."
# Let's inspect pixel colors on the left column outside text:
$pTL = $src.GetPixel(5, 5)
$pBL = $src.GetPixel(5, 310)
$pTopMid = $src.GetPixel(320, 5)
$pBotMid = $src.GetPixel(320, 310)

Write-Host "TL: $($pTL.R), $($pTL.G), $($pTL.B)"
Write-Host "BL: $($pBL.R), $($pBL.G), $($pBL.B)"
Write-Host "TopMid: $($pTopMid.R), $($pTopMid.G), $($pTopMid.B)"
Write-Host "BotMid: $($pBotMid.R), $($pBotMid.G), $($pBotMid.B)"

$g.Dispose()
$clean.Dispose()
$src.Dispose()
