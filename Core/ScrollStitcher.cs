using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lupik.Core;

internal enum ScrollAppendResult { Added, Unchanged, Backwards, NoOverlap, Limit }

/// <summary>Aligns overlapping vertical frames and appends only newly revealed rows.</summary>
internal sealed class ScrollStitcher
{
    private sealed record Frame(byte[] Pixels,int Width,int Height,int Stride);
    private Frame _last;
    private readonly List<byte[]> _parts=new();
    internal int Width { get; }
    internal int Height { get; private set; }
    internal int FrameCount { get; private set; }=1;
    private int HeightLimit => Math.Min(30000,Math.Max(_last.Height,48_000_000/Width));

    internal ScrollStitcher(BitmapSource first)
    {
        _last=Read(first); Width=_last.Width; Height=_last.Height; _parts.Add(_last.Pixels);
    }

    internal ScrollAppendResult Append(BitmapSource bitmap)
    {
        var next=Read(bitmap);
        if(next.Width!=Width || next.Height!=_last.Height) return ScrollAppendResult.NoOverlap;
        if(Score(_last,next,0,48,48)<0.4) return ScrollAppendResult.Unchanged;
        int maximum=Math.Max(1,(int)(next.Height*0.8));
        var candidates=new List<(int Offset,double Error)>();
        for(int offset=-maximum;offset<=maximum;offset++)
        {
            double error=Score(_last,next,offset,20,28);
            int index=candidates.FindIndex(item=>item.Error>error);
            if(index<0) index=candidates.Count;
            if(index<16) { candidates.Insert(index,(offset,error)); if(candidates.Count>16) candidates.RemoveAt(16); }
        }
        if(!candidates.Any(c=>c.Offset==0)) candidates.Add((0,0));
        var precise=candidates.Select(c=>(c.Offset,Error:Score(_last,next,c.Offset,64,48))).OrderBy(c=>c.Error).ToArray();
        var best=precise[0];
        if(best.Error>6) return ScrollAppendResult.NoOverlap;
        if(precise.Any(other=>Math.Abs(other.Offset-best.Offset)>3 && other.Error<=best.Error+0.35 && other.Error<1.5))
            return ScrollAppendResult.NoOverlap; // repeated/blank content: don't guess a seam
        if(best.Offset==0) return ScrollAppendResult.Unchanged;
        if(best.Offset<0) return ScrollAppendResult.Backwards;
        if(Height+best.Offset>HeightLimit) return ScrollAppendResult.Limit;
        var rows=new byte[checked(next.Stride*best.Offset)];
        Buffer.BlockCopy(next.Pixels,(next.Height-best.Offset)*next.Stride,rows,0,rows.Length);
        _parts.Add(rows); _last=next; Height+=best.Offset; FrameCount++;
        return ScrollAppendResult.Added;
    }

    internal BitmapSource Build()
    {
        var pixels=new byte[checked(_last.Stride*Height)]; int offset=0;
        foreach(var part in _parts) { Buffer.BlockCopy(part,0,pixels,offset,part.Length); offset+=part.Length; }
        var result=BitmapSource.Create(Width,Height,96,96,PixelFormats.Bgr32,null,pixels,_last.Stride);
        result.Freeze(); return result;
    }

    private static Frame Read(BitmapSource source)
    {
        var converted=new FormatConvertedBitmap(source,PixelFormats.Bgr32,null,0);
        int stride=checked(source.PixelWidth*4);
        var pixels=new byte[checked(stride*source.PixelHeight)]; converted.CopyPixels(pixels,stride,0);
        return new(pixels,source.PixelWidth,source.PixelHeight,stride);
    }

    private static double Score(Frame previous,Frame current,int offset,int rows,int columns)
    {
        int start=Math.Max(0,offset),end=Math.Min(previous.Height,previous.Height+offset),overlap=end-start;
        int trim=Math.Min(24,overlap/10); start+=trim; end-=trim;
        if(end<=start) return double.MaxValue;
        long total=0; int samples=0;
        int margin=Math.Min(20,previous.Width/10),available=Math.Max(1,previous.Width-2*margin);
        for(int row=0;row<rows;row++)
        {
            int y=start+(end-start-1)*row/Math.Max(1,rows-1);
            int a=y*previous.Stride,b=(y-offset)*current.Stride;
            for(int column=0;column<columns;column++)
            {
                int x=(margin+(available-1)*column/Math.Max(1,columns-1))*4;
                int difference=Math.Abs(previous.Pixels[a+x]-current.Pixels[b+x])+
                    Math.Abs(previous.Pixels[a+x+1]-current.Pixels[b+x+1])+
                    Math.Abs(previous.Pixels[a+x+2]-current.Pixels[b+x+2]);
                total+=Math.Min(96,difference); samples+=3;
            }
        }
        return (double)total/samples;
    }
}
