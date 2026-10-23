Add-Type -AssemblyName System.Drawing
$jar = [System.Drawing.Bitmap]::FromFile("D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\gut365-jar.png")
Write-Host "Jar dimensions: $($jar.Width) x $($jar.Height)"

$minX = 9999; $maxX = 0; $minY = 9999; $maxY = 0;
for ($y = 0; $y -lt $jar.Height; $y += 5) {
    for ($x = 0; $x -lt $jar.Width; $x += 5) {
        $a = $jar.GetPixel($x, $y).A
        if ($a -gt 20) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
Write-Host "Visible jar bounds: X=[$minX, $maxX], Y=[$minY, $maxY]"
$jar.Dispose()
