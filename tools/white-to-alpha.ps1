<#
 Converts a photo shot on a flat light backdrop into a transparent PNG that looks identical when laid over any light colour
 (the "multiply -> alpha" conversion: alpha = 1 - min(r,g,b)/255 after levelling the backdrop to pure white).
 Usage: powershell -File tools/white-to-alpha.ps1 -In photo.png -Out wwwroot/images/name.png [-Mode soft]
 -Mode soft keeps the original colours and only derives a soft alpha (good for powder spray on any backdrop).
#>
param([string]$In, [string]$Out, [string]$Mode = "multiply")
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class W2A {
  public static string Run(string src, string dst, bool soft) {
    Bitmap b = new Bitmap(src); int W=b.Width,H=b.Height;
    Bitmap img = new Bitmap(W,H,PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(img)) g.DrawImage(b,0,0,W,H);
    // backdrop level = median of the 4 corner patches
    int[] lv = new int[4]; int k=0;
    foreach (var pt in new[]{new Point(6,6),new Point(W-7,6),new Point(6,H-7),new Point(W-7,H-7)}) { Color c=img.GetPixel(pt.X,pt.Y); lv[k++]=Math.Min(c.R,Math.Min(c.G,c.B)); }
    Array.Sort(lv); double bg=(lv[1]+lv[2])/2.0; if (bg<200) bg=255;
    BitmapData d = img.LockBits(new Rectangle(0,0,W,H),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
    byte[] px = new byte[d.Stride*H]; Marshal.Copy(d.Scan0,px,0,px.Length);
    for (int y=0;y<H;y++) for (int x=0;x<W;x++) { int i=y*d.Stride+x*4;
      double r=Math.Min(255,px[i+2]*255.0/bg), gg=Math.Min(255,px[i+1]*255.0/bg), bl=Math.Min(255,px[i]*255.0/bg);
      double m=Math.Min(r,Math.Min(gg,bl));
      if (soft) { double sa=Math.Min(1.0,Math.Max(0.0,(255.0-m)/70.0)); if (sa<0.02) { px[i]=px[i+1]=px[i+2]=0; px[i+3]=0; continue; } px[i+3]=(byte)Math.Round(sa*255); px[i+2]=(byte)Math.Min(255,px[i+2]*255.0/bg); px[i+1]=(byte)Math.Min(255,px[i+1]*255.0/bg); px[i]=(byte)Math.Min(255,px[i]*255.0/bg); continue; }
      double a=1-m/255.0; // 0 on the backdrop
      if (a<0.012) { px[i]=px[i+1]=px[i+2]=0; px[i+3]=0; continue; }
      double den=255.0-m;
      px[i+2]=(byte)Math.Round(255.0*(r-m)/den); px[i+1]=(byte)Math.Round(255.0*(gg-m)/den); px[i]=(byte)Math.Round(255.0*(bl-m)/den);
      px[i+3]=(byte)Math.Round(a*255);
    }
    Marshal.Copy(px,0,d.Scan0,px.Length); img.UnlockBits(d);
    img.Save(dst,ImageFormat.Png);
    return string.Format("{0}x{1} backdrop={2}",W,H,bg);
  }
}
"@
[W2A]::Run((Resolve-Path $In).Path, [System.IO.Path]::GetFullPath($Out), ($Mode -eq "soft"))
