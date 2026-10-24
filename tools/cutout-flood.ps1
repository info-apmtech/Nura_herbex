<#
 Cuts a subject out of a flat light backdrop by flood-filling inwards from the border (opaque result, soft 1px edge).
 Usage: powershell -File tools/cutout-flood.ps1 -In photo.png -Out out.png [-Tol 7]
#>
param([string]$In, [string]$Out, [int]$Tol = 7)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Flood {
  public static string Run(string src, string dst, int tol) {
    Bitmap b = new Bitmap(src); int W=b.Width,H=b.Height;
    Bitmap img = new Bitmap(W,H,PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(img)) g.DrawImage(b,0,0,W,H);
    BitmapData d = img.LockBits(new Rectangle(0,0,W,H),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
    byte[] px = new byte[d.Stride*H]; Marshal.Copy(d.Scan0,px,0,px.Length); int S=d.Stride;
    Color c0 = Color.FromArgb(px[2+0*S+0*4], px[1], px[0]); int br=px[2],bg=px[1],bb=px[0];
    bool[] bgm = new bool[W*H]; Queue<int> q = new Queue<int>();
    Func<int,int,bool> isBg = (x,y)=>{ int i=y*S+x*4; return Math.Abs(px[i+2]-br)<=tol && Math.Abs(px[i+1]-bg)<=tol && Math.Abs(px[i]-bb)<=tol; };
    for (int x=0;x<W;x++){ foreach (int y in new[]{0,H-1}) if(!bgm[y*W+x]&&isBg(x,y)){bgm[y*W+x]=true;q.Enqueue(y*W+x);} }
    for (int y=0;y<H;y++){ foreach (int x in new[]{0,W-1}) if(!bgm[y*W+x]&&isBg(x,y)){bgm[y*W+x]=true;q.Enqueue(y*W+x);} }
    int[] dx={1,-1,0,0}, dy={0,0,1,-1};
    while(q.Count>0){ int p=q.Dequeue(); int x=p%W,y=p/W; for(int k=0;k<4;k++){ int nx=x+dx[k],ny=y+dy[k]; if(nx<0||ny<0||nx>=W||ny>=H) continue; int np=ny*W+nx; if(bgm[np]) continue; if(isBg(nx,ny)){bgm[np]=true;q.Enqueue(np);} } }
    for (int y=0;y<H;y++) for (int x=0;x<W;x++){ int i=y*S+x*4;
      if (bgm[y*W+x]) { px[i]=px[i+1]=px[i+2]=0; px[i+3]=0; continue; }
      bool edge=false; for(int k=0;k<4&&!edge;k++){ int nx=x+dx[k],ny=y+dy[k]; if(nx>=0&&ny>=0&&nx<W&&ny<H&&bgm[ny*W+nx]) edge=true; }
      px[i+3]=(byte)(edge?150:255);
    }
    Marshal.Copy(px,0,d.Scan0,px.Length); img.UnlockBits(d); img.Save(dst,ImageFormat.Png);
    return string.Format("{0}x{1} backdrop={2},{3},{4}",W,H,br,bg,bb);
  }
}
"@
[Flood]::Run((Resolve-Path $In).Path, [System.IO.Path]::GetFullPath($Out), $Tol)
