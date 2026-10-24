<#
 Splits a jar product shot into a lid-less body PNG and a lid-only PNG (true alpha, no rectangular crops).
 The lid is the low-saturation white cap; each column ends where the gold ring starts.
 Usage: powershell -File tools/split-lid.ps1 -Name product-2 -Out stamix
 Writes wwwroot/images/<Out>-body.png and <Out>-lid.png and prints normalized geometry for home-v3.css.
#>
param([string]$Name, [string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class LidSplit {
  public static string Run(string src, string bodyPath, string lidPath) {
    Bitmap b = new Bitmap(src); int W=b.Width,H=b.Height;
    Bitmap img = new Bitmap(W,H,PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(img)) g.DrawImage(b,0,0,W,H);
    BitmapData d = img.LockBits(new Rectangle(0,0,W,H),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
    byte[] px = new byte[d.Stride*H]; Marshal.Copy(d.Scan0,px,0,px.Length); int S=d.Stride;
    bool[] isLid = new bool[W*H]; int[] gold = new int[W]; int[] top = new int[W];
    int x0=W,x1=-1,y0=H,yMaxG=0;
    for (int x=0;x<W;x++){ gold[x]=-1; top[x]=-1;
      int y=0; // find first opaque low-sat bright pixel in the cap band (upper 60%)
      for (;y<H*6/10;y++){ int i=y*S+x*4; int a=px[i+3]; int bl=px[i],gr=px[i+1],r=px[i+2];
        int mx=Math.Max(r,Math.Max(gr,bl)), mn=Math.Min(r,Math.Min(gr,bl));
        if (a>40 && mx>165 && mx-mn<=14){ break; } }
      if (y>=H*6/10) continue;
      int start=y, miss=0, last=y;
      for (;y<H*7/10;y++){ int i=y*S+x*4; int a=px[i+3]; int bl=px[i],gr=px[i+1],r=px[i+2];
        int mx=Math.Max(r,Math.Max(gr,bl)), mn=Math.Min(r,Math.Min(gr,bl));
        bool isCap = a>20 && mx>150 && mx-mn<=16;
        if (isCap){ miss=0; last=y; } else { miss++; if (miss>=3) break; } }
      if (last-start<40) continue; // not a cap column
      for (int yy=start; yy<=last; yy++) isLid[yy*W+x]=true;
      gold[x]=last; top[x]=start; if (x<x0)x0=x; if (x>x1)x1=x; if (start<y0)y0=start;
    }
    int ei=12; for (int x=x0;x<x0+ei;x++) if(gold[x]>=0) gold[x]=Math.Min(gold[x],gold[x0+ei]+3); for (int x=x1;x>x1-ei;x--) if(gold[x]>=0) gold[x]=Math.Min(gold[x],gold[x1-ei]+3);
    for (int x=0;x<W;x++) if(gold[x]>=0) for(int y=gold[x]+1;y<H*7/10;y++) isLid[y*W+x]=false;
    yMaxG=0; for(int x=0;x<W;x++) if(gold[x]>yMaxG) yMaxG=gold[x];
    Bitmap body = new Bitmap(W,H,PixelFormat.Format32bppArgb), lid = new Bitmap(W,H,PixelFormat.Format32bppArgb);
    BitmapData bd = body.LockBits(new Rectangle(0,0,W,H),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
    BitmapData ld = lid.LockBits(new Rectangle(0,0,W,H),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
    byte[] bp = new byte[bd.Stride*H], lp = new byte[ld.Stride*H];
    int yMaxGold=yMaxG;
    for (int x=0;x<W;x++) for (int y=0;y<H;y++){ int i=y*S+x*4;
      if (isLid[y*W+x]) {
        // soften the bottom 2px of the cap so the cut has no hard edge
        int dist = gold[x]-y; int a = px[i+3]; if (dist<2) a = a*(dist+1)/3;
        lp[i]=px[i];lp[i+1]=px[i+1];lp[i+2]=px[i+2];lp[i+3]=(byte)a;
        if (y>yMaxGold) yMaxGold=y;
      } else { bp[i]=px[i];bp[i+1]=px[i+1];bp[i+2]=px[i+2];bp[i+3]=px[i+3]; }
    }
    Marshal.Copy(bp,0,bd.Scan0,bp.Length); Marshal.Copy(lp,0,ld.Scan0,lp.Length);
    body.UnlockBits(bd); lid.UnlockBits(ld); img.UnlockBits(d);
    // crop lid to its bounding box (pad 4px)
    int cx0=Math.Max(0,x0-4), cy0=Math.Max(0,y0-4), cx1=Math.Min(W-1,x1+4), cy1=Math.Min(H-1,yMaxGold+4);
    Bitmap crop = lid.Clone(new Rectangle(cx0,cy0,cx1-cx0+1,cy1-cy0+1),PixelFormat.Format32bppArgb);
    body.Save(bodyPath,ImageFormat.Png); crop.Save(lidPath,ImageFormat.Png);
    int mid=(x0+x1)/2;
    return string.Format("size={0} lidBox x={1}-{2} y={3}-{4} goldEdge={5} goldMid={6} cropX={7} cropY={8} cropW={9} cropH={10}",W,x0,x1,y0,yMaxGold,gold[x0+8],gold[mid],cx0,cy0,cx1-cx0+1,cy1-cy0+1);
  }
}
"@
$dir = Join-Path $PSScriptRoot "..\src\Nuraherbex.UI\wwwroot\images"
[LidSplit]::Run((Join-Path $dir "$Name.png"), (Join-Path $dir "$Out-body.png"), (Join-Path $dir "$Out-lid.png"))
