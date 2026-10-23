Add-Type -AssemblyName System.Drawing
$bg = [System.Drawing.Bitmap]::FromFile("D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-bg-nature.png")
$m1 = [System.Drawing.Bitmap]::FromFile("C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\.user_uploaded\media_1792652877123.png")

# Let's inspect a distinct feature, e.g. the purple flower cluster on the right
# In media_1, where is the bright yellow center of the purple flower?
# In media_1, let's find the yellow flower center (R > 200, G > 180, B < 80)
for ($y = 150; $y -lt 300; $y++) {
    for ($x = 400; $x -lt $m1.Width; $x++) {
        $p = $m1.GetPixel($x, $y)
        if ($p.R -gt 220 -and $p.G -gt 190 -and $p.B -lt 80) {
            Write-Host "Flower center in media_1 at ($x, $y)"
            break
        }
    }
}

# In hero-bg-nature, let's find the same yellow center:
for ($y = 400; $y -lt 900; $y++) {
    for ($x = 900; $x -lt $bg.Width; $x++) {
        $p = $bg.GetPixel($x, $y)
        if ($p.R -gt 220 -and $p.G -gt 190 -and $p.B -lt 80) {
            Write-Host "Flower center in hero-bg-nature at ($x, $y)"
            break
        }
    }
}

$bg.Dispose()
$m1.Dispose()
