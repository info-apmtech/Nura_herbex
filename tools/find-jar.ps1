Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Bitmap]::FromFile("C:\Users\ADMIN\.gemini\antigravity-ide\brain\8ebcbe54-25db-45f1-8440-f6e25a1a5c25\.user_uploaded\media_1792652877123.png")

# Let's find pixels where R,G,B match the purple "GUT 365" label (e.g. R ~ 70-130, G ~ 20-60, B ~ 150-220)
$minX = 9999; $maxX = 0; $minY = 9999; $maxY = 0;
for ($y = 0; $y -lt $img.Height; $y += 2) {
    for ($x = 0; $x -lt $img.Width; $x += 2) {
        $px = $img.GetPixel($x, $y)
        if ($px.B -gt 150 -and $px.R -lt 120 -and $px.G -lt 80) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
Write-Host "Purple label bounds in media_1: X=[$minX, $maxX], Y=[$minY, $maxY]"
Write-Host "Jar Width roughly: $($maxX - $minX), Jar Height roughly: $($maxY - $minY)"
$img.Dispose()
