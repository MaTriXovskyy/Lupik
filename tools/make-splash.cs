#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false
// Builds the splash shown by Lupik-win-Setup.exe while it installs (vpk pack --splashImage): the gold logo in a soft
// glow on Lupik's graphite, its name under it. Velopack draws its progress bar along the bottom, so that strip stays
// empty. Drawn at 1.5x: Velopack shows the picture at its pixel size, so this is a middle ground between
// normal and high-DPI screens.
// Usage: dotnet run --file tools/make-splash.cs -- tools/splash.png
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

const double W = 520, H = 300, Scale = 1.5;
string outPath = args.Length > 0 ? args[0] : "splash.png";
Exception? error = null;
var thread = new Thread(() =>
{
    try
    {
        var gold = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0xF5, 0xD2, 0x7A), 0), new(Color.FromRgb(0xE3, 0xB3, 0x41), 0.55), new(Color.FromRgb(0xB8, 0x86, 0x1F), 1),
        }, new Point(0, 0), new Point(1, 1));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Graphite, a hairline frame, and a warm glow where the logo sits
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x13, 0x12, 0x11)), new Pen(new SolidColorBrush(Color.FromRgb(0x2F, 0x2B, 0x27)), 1),
                new Rect(0.5, 0.5, W - 1, H - 1));
            var glow = new RadialGradientBrush(Color.FromArgb(0x55, 0xE3, 0xB3, 0x41), Color.FromArgb(0x00, 0xE3, 0xB3, 0x41));
            dc.DrawEllipse(glow, null, new Point(W / 2, 112), 120, 120);

            // The logo: rounded gold square with the graphite eye (same shapes as app.ico / AppLogo)
            double size = 84, x = (W - size) / 2, y = 112 - size / 2;
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(size / 100, size / 100));
            dc.DrawGeometry(gold, null, Geometry.Parse("M22,0 H78 A22,22 0 0 1 100,22 V78 A22,22 0 0 1 78,100 H22 A22,22 0 0 1 0,78 V22 A22,22 0 0 1 22,0 Z"));
            var eye = Geometry.Parse("M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0");
            dc.PushTransform(new MatrixTransform(2.583, 0, 0, 2.583, 19, 20));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x36, 0x32)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, eye);
            dc.Pop(); dc.Pop(); dc.Pop();

            var name = new FormattedText("Lupik", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                30, new SolidColorBrush(Color.FromRgb(0xFB, 0xF8, 0xF2)), Scale);
            dc.DrawText(name, new Point((W - name.Width) / 2, 176));

            // A gold hairline under the name, fading out at both ends (like the preview's title bar)
            var line = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, 0xA8, 0x84, 0x3A), 0), new(Color.FromRgb(0xA8, 0x84, 0x3A), 0.5), new(Color.FromArgb(0, 0xA8, 0x84, 0x3A), 1),
            }, new Point(0, 0), new Point(1, 0));
            dc.DrawRectangle(line, null, new Rect(W / 2 - 90, 226, 180, 1));
        }

        var bmp = new RenderTargetBitmap((int)(W * Scale), (int)(H * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(outPath);
        encoder.Save(file);
    }
    catch (Exception e) { error = e; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (error != null) throw error;
Console.WriteLine($"Wrote {outPath}");
