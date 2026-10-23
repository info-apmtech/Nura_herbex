Add-Type -AssemblyName System.Drawing
$m1 = [System.Drawing.Bitmap]::FromFile("C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\.user_uploaded\media_1792652877123.png")

# Let's find pixel with highest (R + G - B) in the right side:
$maxScore = 0
$bestX = 0; $bestY = 0;
for ($y = 100; $y -lt $m1.Height; $y++) {
    for ($x = 450; $x -lt $m1.Width; $x++) {
        $p = $m1.GetPixel($x, $y)
        $score = [int]$p.R + [int]$p.G - [int]$p.B
        if ($score -gt $maxScore) {
            $maxScore = $score
            $bestX = $x
            $bestY = $y
        }
    }
}
$p = $m1.GetPixel($bestX, $bestY)
Write-Host "Brightest warm spot in media_1 at ($bestX, $bestY): R=$($p.R) G=$($p.G) B=$($p.B)"

$m1.Dispose()
