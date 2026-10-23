Add-Type -AssemblyName System.Drawing
$b = [System.Drawing.Bitmap]::FromFile("D:\Project\Nuraherbex\src\Nuraherbex.UI\wwwroot\images\hero-jar-rock.png")
Write-Host "Width: $($b.Width), Height: $($b.Height)"
$tl = $b.GetPixel(0, 0)
$tr = $b.GetPixel($b.Width - 1, 0)
$bl = $b.GetPixel(0, $b.Height - 1)
$br = $b.GetPixel($b.Width - 1, $b.Height - 1)
Write-Host "TL: A=$($tl.A) R=$($tl.R) G=$($tl.G) B=$($tl.B)"
Write-Host "TR: A=$($tr.A) R=$($tr.R) G=$($tr.G) B=$($tr.B)"
Write-Host "BL: A=$($bl.A) R=$($bl.R) G=$($bl.G) B=$($bl.B)"
Write-Host "BR: A=$($br.A) R=$($br.R) G=$($br.G) B=$($br.B)"
$b.Dispose()
