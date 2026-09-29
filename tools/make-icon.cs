#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false
// Builds app.ico: a gold rounded square with a black eye and a soft shadow under it.
// Every size is drawn from vectors (not scaled down), with thicker strokes at small sizes.
// Usage: dotnet run --file tools/make-icon.cs -- app.ico [preview.png]
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var pngs = new List<byte[]>();
Exception? error = null;
var thread = new Thread(() =>
{
    try { foreach (int s in sizes) pngs.Add(Render(s)); }
    catch (Exception e) { error = e; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (error != null) throw error;

string outPath = args.Length > 0 ? args[0] : "app.ico";
using (var ico = new BinaryWriter(File.Create(outPath)))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)0); ico.Write((byte)0); ico.Write((short)1); ico.Write((short)32);
        ico.Write(pngs[i].Length); ico.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs) ico.Write(png);
}
if (args.Length > 1) File.WriteAllBytes(args[1], pngs[^1]);
Console.WriteLine($"{outPath}: {sizes.Length} sizes");

static byte[] Render(int size)
{
    double s = size;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        // Rounded square, gold gradient (same stops as GoldGradient in Theme.xaml)
        var gold = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0xF5, 0xD2, 0x7A), 0),
            new GradientStop(Color.FromRgb(0xE3, 0xB3, 0x41), 0.55),
            new GradientStop(Color.FromRgb(0xB8, 0x86, 0x1F), 1),
        }, new Point(0, 0), new Point(1, 1));
        double inset = size <= 24 ? 0 : s * 0.06, radius = s * 0.22; // tiny sizes use every pixel
        dc.DrawRoundedRectangle(gold, null, new Rect(inset, inset, s - 2 * inset, s - 2 * inset), radius, radius);
    }

    // The eye (Lucide "eye", 24x24 units), black, with a soft brown shadow below it
    var eye = Geometry.Parse("M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0");
    double eyeScale = s * (size <= 24 ? 0.84 : 0.62) / 24;
    double stroke = size <= 16 ? 3.2 : size <= 24 ? 2.9 : size <= 48 ? 2.3 : 2.0; // bolder where pixels are scarce
    var eyeVisual = new DrawingVisual
    {
        Effect = size >= 24 ? new DropShadowEffect
        {
            Color = Color.FromRgb(0x5A, 0x3A, 0x05), Direction = 270, ShadowDepth = s * 0.03,
            BlurRadius = s * 0.08, Opacity = 0.35,
        } : null,
    };
    using (var dc = eyeVisual.RenderOpen())
    {
        dc.PushTransform(new TranslateTransform((s - 24 * eyeScale) / 2, (s - 24 * eyeScale) / 2 + s * 0.01));
        dc.PushTransform(new ScaleTransform(eyeScale, eyeScale));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x08)), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, eye);
    }

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    bmp.Render(eyeVisual);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}
