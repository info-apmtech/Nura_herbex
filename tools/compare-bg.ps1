Add-Type -AssemblyName System.Drawing
$bg = [System.Drawing.Bitmap]::FromFile("D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-bg-nature.png")
$m1 = [System.Drawing.Bitmap]::FromFile("C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\.user_uploaded\media_1792652877123.png")

Write-Host "hero-bg-nature: $($bg.Width) x $($bg.Height)"
Write-Host "media_1: $($m1.Width) x $($m1.Height)"

# In media_1 (658 x 315), let's sample the top right corner pixel:
$pxM1_TR = $m1.GetPixel(650, 20)
Write-Host "media_1 TR: R=$($pxM1_TR.R) G=$($pxM1_TR.G) B=$($pxM1_TR.B)"

# Let's sample top right of hero-bg-nature:
$pxBg_TR = $bg.GetPixel(1430, 40)
Write-Host "hero-bg TR: R=$($pxBg_TR.R) G=$($pxBg_TR.G) B=$($pxBg_TR.B)"

$bg.Dispose()
$m1.Dispose()
