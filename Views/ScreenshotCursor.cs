using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lupik.Views;

/// <summary>Opaque white arms with a black outline; never XOR/invert the pixels underneath.</summary>
internal sealed class ScreenshotCursor : IDisposable
{
    internal Cursor Value { get; }
    internal ScreenshotCursor(double scale)
    {
        var bitmap = Draw(scale);
        int width=bitmap.PixelWidth, height=bitmap.PixelHeight, stride=width*4;
        var pixels=new byte[stride*height]; bitmap.CopyPixels(pixels,stride,0);
        int maskStride=(width+31)/32*4, imageSize=40+pixels.Length+maskStride*height;
        using var stream=new MemoryStream();
        using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,leaveOpen:true))
        {
            writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)1); // CUR directory
            writer.Write((byte)width); writer.Write((byte)height); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)(width/2)); writer.Write((ushort)(height/2));
            writer.Write(imageSize); writer.Write(22);
            writer.Write(40); writer.Write(width); writer.Write(height*2);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(pixels.Length);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for(int y=height-1;y>=0;y--) writer.Write(pixels,y*stride,stride);
            for(int y=height-1;y>=0;y--)
            {
                var mask=new byte[maskStride];
                for(int x=0;x<width;x++) if(pixels[y*stride+x*4+3]==0) mask[x/8]|=(byte)(0x80>>(x%8));
                writer.Write(mask);
            }
        }
        stream.Position=0;
        Value=new Cursor(stream,scaleWithDpi:false);
    }

    internal static BitmapSource Draw(double scale)
    {
        int size=Math.Clamp((int)Math.Round(32*scale),24,192),center=size/2;
        var pixels=new byte[size*size*4];
        for(int y=0;y<size;y++) for(int x=0;x<size;x++)
        {
            double dx=Math.Abs(x-center)/scale,dy=Math.Abs(y-center)/scale;
            bool outer=dx<=2.2 && dy>=2.5 && dy<=12 || dy<=2.2 && dx>=2.5 && dx<=12;
            if(!outer) continue;
            bool white=dx<=0.65 && dy>=3.8 && dy<=10.8 || dy<=0.65 && dx>=3.8 && dx<=10.8;
            int p=(y*size+x)*4;
            pixels[p]=pixels[p+1]=pixels[p+2]=(byte)(white?255:0); pixels[p+3]=255;
        }
        var result=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4);
        result.Freeze(); return result;
    }
    public void Dispose() => Value.Dispose();
}
