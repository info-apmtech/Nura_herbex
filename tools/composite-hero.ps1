Add-Type -AssemblyName System.Drawing

$bgPath = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-bg-nature.png"
$jarPath = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\gut365-jar.png"
$outPath = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-composite.png"

$bg = [System.Drawing.Bitmap]::FromFile($bgPath)
$jar = [System.Drawing.Bitmap]::FromFile($jarPath)

$canvas = New-Object System.Drawing.Bitmap $bg.Width, $bg.Height
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

$g.DrawImage($bg, 0, 0, $bg.Width, $bg.Height)

# Position jar so it sits naturally on the rock surface
$jarW = 670
$jarH = 670
$jarX = 710
$jarY = 320

# Subtle contact shadow tightly under the jar base (drawn before the jar)
$shadowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(140, 30, 25, 40))
$g.FillEllipse($shadowBrush, ($jarX + 70), ($jarY + $jarH - 30), ($jarW - 140), 28)
$shadowBrush.Dispose()

$g.DrawImage($jar, $jarX, $jarY, $jarW, $jarH)

$g.Dispose()
$bg.Dispose()
$jar.Dispose()

$canvas.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
Write-Host "Updated composite saved to $outPath"
