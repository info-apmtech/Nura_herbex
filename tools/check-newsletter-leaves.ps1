Add-Type -AssemblyName System.Drawing

$p1 = "D:\Project\Nuraherbex\src\Nuraherbex.Web\wwwroot\leave1 Image Oct 22, 2026, 12_22_59 PM.png"
$p2 = "D:\Project\Nuraherbex\src\Nuraherbex.Web\wwwroot\leave2 Image Oct 22, 2026, 12_22_49 PM.png"

$b1 = [System.Drawing.Bitmap]::FromFile($p1)
$b2 = [System.Drawing.Bitmap]::FromFile($p2)

Write-Host "leave1: $($b1.Width)x$($b1.Height)"
$px1 = $b1.GetPixel(10, 10)
Write-Host "leave1 (10,10): A=$($px1.A), R=$($px1.R), G=$($px1.G), B=$($px1.B)"

Write-Host "leave2: $($b2.Width)x$($b2.Height)"
$px2 = $b2.GetPixel(10, 10)
Write-Host "leave2 (10,10): A=$($px2.A), R=$($px2.R), G=$($px2.G), B=$($px2.B)"

$b1.Dispose()
$b2.Dispose()
