Add-Type -AssemblyName System.Drawing
$b = [System.Drawing.Bitmap]::FromFile("D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\product-1.png")
$px = $b.GetPixel(5, 5)
Write-Host "Pixel (5,5): A=$($px.A), R=$($px.R), G=$($px.G), B=$($px.B)"
$b.Dispose()
