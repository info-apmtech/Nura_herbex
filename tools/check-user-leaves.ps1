Add-Type -AssemblyName System.Drawing

$p1 = "D:\Project\Nuraherbex\src\Nuraherbex.Web\wwwroot\Watercolor Green Leaf Sprig.png"
$p2 = "D:\Project\Nuraherbex\src\Nuraherbex.Web\wwwroot\Minimalist Green Leaf Corner.png"

$b1 = [System.Drawing.Bitmap]::FromFile($p1)
$b2 = [System.Drawing.Bitmap]::FromFile($p2)

Write-Host "P1 (Sprig): $($b1.Width)x$($b1.Height)"
$px1 = $b1.GetPixel($b1.Width - 10, $b1.Height - 10)
Write-Host "P1 bottom-right pixel: A=$($px1.A), R=$($px1.R), G=$($px1.G), B=$($px1.B)"

Write-Host "P2 (Corner): $($b2.Width)x$($b2.Height)"
$px2 = $b2.GetPixel(10, 10)
Write-Host "P2 top-left pixel: A=$($px2.A), R=$($px2.R), G=$($px2.G), B=$($px2.B)"

$b1.Dispose()
$b2.Dispose()
