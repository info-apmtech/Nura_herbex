Add-Type -AssemblyName System.Drawing

$bgPath = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-bg-nature.png"
$jarPath = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\gut365-jar.png"
$mockupPath = "C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\.user_uploaded\media_1792652877123.png"
$outBanner = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-banner-exact.png"

$bg = [System.Drawing.Bitmap]::FromFile($bgPath)
$rawJar = [System.Drawing.Bitmap]::FromFile($jarPath)
$m1 = [System.Drawing.Bitmap]::FromFile($mockupPath)

# 1. Crop tight jar: X=160, Y=150, W=930, H=1005
$tightJar = New-Object System.Drawing.Bitmap 930, 1005
$tjg = [System.Drawing.Graphics]::FromImage($tightJar)
$tjg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$tjg.DrawImage($rawJar, [System.Drawing.Rectangle]::new(0, 0, 930, 1005), 160, 150, 930, 1005, [System.Drawing.GraphicsUnit]::Pixel)
$tjg.Dispose()
$rawJar.Dispose()

# 2. Canvas 1448 x 680 (exact 2.13:1 aspect ratio of Image 1)
$bannerW = 1448
$bannerH = 680
$srcCropY = 175

$canvas = New-Object System.Drawing.Bitmap $bannerW, $bannerH
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# Draw background
$g.DrawImage($bg, [System.Drawing.Rectangle]::new(0, 0, $bannerW, $bannerH), 0, $srcCropY, $bannerW, $bannerH, [System.Drawing.GraphicsUnit]::Pixel)

# 3. Position Jar on rock
$jarW = 410
$jarH = [int](410 / (930 / 1005)) # ~ 443
$jarX = 760
$jarY = 140 # perfectly resting on rock surface

# Contact shadow under jar
$contactBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 25, 20, 35))
$g.FillEllipse($contactBrush, ($jarX + 25), ($jarY + $jarH - 10), ($jarW - 50), 22)
$contactBrush.Dispose()

# Ambient shadow under jar
$ambientBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 25, 20, 35))
$g.FillEllipse($ambientBrush, ($jarX + 10), ($jarY + $jarH - 16), ($jarW - 20), 30)
$ambientBrush.Dispose()

# Draw Jar
$g.DrawImage($tightJar, $jarX, $jarY, $jarW, $jarH)

# 4. Circular Stamp: Crop circular stamp from media_1 with circular alpha mask
# In media_1 (658 x 315), the circular stamp center is at approx X=595, Y=60, radius ~ 45
$stampRadius = 46
$stampCenterX = 595
$stampCenterY = 60
$stampDiam = $stampRadius * 2
$stampCrop = New-Object System.Drawing.Bitmap $stampDiam, $stampDiam
for ($py = 0; $py -lt $stampDiam; $py++) {
    for ($px = 0; $px -lt $stampDiam; $px++) {
        $dx = $px - $stampRadius
        $dy = $py - $stampRadius
        $dist = [Math]::Sqrt($dx * $dx + $dy * $dy)
        if ($dist -le $stampRadius) {
            $srcX = $stampCenterX - $stampRadius + $px
            $srcY = $stampCenterY - $stampRadius + $py
            if ($srcX -ge 0 -and $srcX -lt $m1.Width -and $srcY -ge 0 -and $srcY -lt $m1.Height) {
                # Feather outer 2 pixels
                $alpha = 255
                if ($dist -gt ($stampRadius - 2)) {
                    $alpha = [int](255 * ($stampRadius - $dist) / 2)
                }
                $c = $m1.GetPixel($srcX, $srcY)
                $stampCrop.SetPixel($px, $py, [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B))
            }
        }
    }
}

# Scale and place the circular stamp at top-right of jar
# In Image 1, stamp diameter is ~ 92px. On our 1448 canvas, scale factor is ~ 2.2 => diam ~ 200px
$stampDestDiam = 195
$stampDestX = $jarX + $jarW - 55
$stampDestY = 28
$g.DrawImage($stampCrop, $stampDestX, $stampDestY, $stampDestDiam, $stampDestDiam)

$stampCrop.Dispose()
$m1.Dispose()
$tightJar.Dispose()
$bg.Dispose()
$g.Dispose()

$canvas.Save($outBanner, [System.Drawing.Imaging.ImageFormat]::Png)
Copy-Item $outBanner "D:\Project\Nuraherbex\src\Nuraherbex.Web\wwwroot\images\hero-banner-exact.png" -Force

$canvas.Dispose()
Write-Host "Created exact hero banner with feathered stamp"
