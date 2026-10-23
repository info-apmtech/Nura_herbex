Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Bitmap]::FromFile("C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\.user_uploaded\media_1792647969340.png")
Write-Host "Mockup size: $($src.Width) x $($src.Height)"

# In media_1792647969340.png (775 x 420):
# Navbar is 0..62
# The hero banner is 62..418
# Let's crop from X=320 to 775, Y=62 to 418
$cropX = 330
$cropY = 62
$cropW = $src.Width - $cropX
$cropH = 418 - $cropY

$bmp = New-Object System.Drawing.Bitmap $cropW, $cropH
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($src, [System.Drawing.Rectangle]::new(0, 0, $cropW, $cropH), $cropX, $cropY, $cropW, $cropH, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

$bmp.Save("D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-showcase-perfect.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$src.Dispose()
Write-Host "Saved hero-showcase-perfect.png ($cropW x $cropH)"
