Add-Type -AssemblyName System.Drawing

$srcPath = "C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\leaf_sprig_1792655846573.jpg"
$src = [System.Drawing.Bitmap]::FromFile($srcPath)

$w = $src.Width
$h = $src.Height
$png = New-Object System.Drawing.Bitmap $w, $h

for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
        $p = $src.GetPixel($x, $y)
        # Background is white/light cream (R > 235, G > 235, B > 235)
        # Calculate how close to white it is
        $brightness = [Math]::Min($p.R, [Math]::Min($p.G, $p.B))
        if ($brightness -ge 248) {
            # Completely transparent
            $png.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, $p.R, $p.G, $p.B))
        } elseif ($brightness -ge 230) {
            # Smooth fade from 230 to 248
            $alpha = [int](255 * (248 - $brightness) / 18.0)
            $png.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $p.R, $p.G, $p.B))
        } else {
            $png.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $p.R, $p.G, $p.B))
        }
    }
}

# Save left sprig (rotated/aligned to enter from left)
$outLeft = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\leaf-sprig-left.png"
$png.Save($outLeft, [System.Drawing.Imaging.ImageFormat]::Png)
Copy-Item $outLeft "D:\Project\Nuraherbex\src\Nuraherbex.Web\wwwroot\images\leaf-sprig-left.png" -Force

# Flipped horizontally for right sprig
$rightBmp = New-Object System.Drawing.Bitmap $w, $h
$rg = [System.Drawing.Graphics]::FromImage($rightBmp)
$rg.DrawImage($png, 0, 0, $w, $h)
$rightBmp.RotateFlip([System.Drawing.RotateFlipType]::RotateNoneFlipX)
$outRight = "D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\leaf-sprig-right.png"
$rightBmp.Save($outRight, [System.Drawing.Imaging.ImageFormat]::Png)
Copy-Item $outRight "D:\Project\Nuraherbex\src\Nuraherbex.Web\wwwroot\images\leaf-sprig-right.png" -Force

$rg.Dispose()
$rightBmp.Dispose()
$png.Dispose()
$src.Dispose()

Write-Host "Created leaf-sprig-left.png and leaf-sprig-right.png with alpha transparency"
