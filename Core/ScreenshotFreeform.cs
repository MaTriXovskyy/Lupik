using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelPoint = System.Drawing.Point;
using Point = System.Windows.Point;

namespace Lupik.Core;

internal static class ScreenshotFreeform
{
    internal static StreamGeometry Geometry(IReadOnlyList<PixelPoint> outline)
    {
        var geometry=new StreamGeometry { FillRule=FillRule.EvenOdd };
        if(outline.Count>0)
        {
            using var drawing=geometry.Open();
            drawing.BeginFigure(new Point(outline[0].X,outline[0].Y),isFilled:true,isClosed:true);
            drawing.PolyLineTo(outline.Skip(1).Select(p=>new Point(p.X,p.Y)).ToArray(),isStroked:true,isSmoothJoin:false);
        }
        geometry.Freeze(); return geometry;
    }

    internal static BitmapSource Crop(BitmapSource desktop,Rectangle desktopBounds,Rectangle area,IReadOnlyList<PixelPoint> outline)
    {
        area.Intersect(desktopBounds);
        var cropped=ScreenshotService.Crop(desktop,desktopBounds,area);
        var shape=new GeometryGroup { Transform=new TranslateTransform(-area.X,-area.Y) };
        shape.Children.Add(Geometry(outline));
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            drawing.PushClip(shape);
            drawing.DrawImage(cropped,new Rect(0,0,area.Width,area.Height));
            drawing.Pop();
        }
        var result=new RenderTargetBitmap(area.Width,area.Height,96,96,PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }

    internal static byte[] Png(BitmapSource bitmap)
    {
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    internal static DataObject ClipboardData(BitmapSource bitmap,byte[]? png=null)
    {
        var data=new DataObject(); data.SetImage(bitmap);
        if(png!=null) data.SetData("PNG",new MemoryStream(png),autoConvert:false);
        return data;
    }
}
