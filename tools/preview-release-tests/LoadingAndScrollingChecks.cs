using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lupik;
using Lupik.Core;
using Lupik.Views;

static class LoadingAndScrollingChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static object Get(object obj, string name) => obj.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(obj)!;
    static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Private)!.Invoke(obj, args);
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static object Progress() => Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("Lupik.Core.DecodeProgress")!, true)!;
    static void Report(object progress, double value) => progress.GetType().GetMethod("Report")!.Invoke(progress, new object[] { value });
    static double Value(object progress) => (double)progress.GetType().GetProperty("Value")!.GetValue(progress)!;
    static readonly BitmapSource Pixel = MakeBitmap(1, 1);

    static BitmapSource MakeBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 120; pixels[i + 2] = 220; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    static TaskCompletionSource<(BitmapSource, long, int, int)> Pending(ImageViewer viewer, double value)
    {
        var gate = new TaskCompletionSource<(BitmapSource, long, int, int)>();
        var progress = Progress(); Report(progress, value);
        var entryType = typeof(ImageViewer).GetNestedType("CacheEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, DateTime.UtcNow, gate.Task, progress)!;
        Call(viewer, "TrackDecode", entry);
        return gate;
    }

    public static async Task Run(string samples)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "Lupik-loading-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string bytes = Path.Combine(scratch, "bytes.bin");
        File.WriteAllBytes(bytes, new byte[1000]);
        object progress = Progress();
        var streamType = typeof(MainWindow).Assembly.GetType("Lupik.Core.DecodeReadStream")!;
        using (var stream = (Stream)Activator.CreateInstance(streamType, bytes, progress)!)
        {
            stream.Seek(990, SeekOrigin.Begin); stream.ReadExactly(new byte[10]);
            Assert(Math.Abs(Value(progress) - 0.008) < 0.00001, "TIFF EOF metadata falsely counted unread pixels");
            stream.Position = 0; stream.ReadExactly(new byte[500]);
            stream.Position = 0; stream.ReadExactly(new byte[500]);
            Assert(Math.Abs(Value(progress) - 0.408) < 0.00001, "Repeated reads counted twice");
            stream.Position = 400; stream.ReadExactly(new byte[600]);
            Assert(Math.Abs(Value(progress) - 0.8) < 0.00001, "Overlapping reads did not merge");
            Report(progress, 0.2);
            Assert(Value(progress) == 0.8, "Decoder fallback moved progress backwards");
        }
        Console.WriteLine("PASS seekable TIFF byte progress, overlapping/repeated reads and monotonic fallback");

        var w = new MainWindow();
        await w.ShowFile(Path.Combine(samples, "sample.png"));
        var viewer = (ImageViewer)Get(w, "ImageViewerControl");
        var title = (FrameworkElement)Get(w, "TitleProgress");
        var fill = (FrameworkElement)Get(w, "TitleProgressFill");
        Call(w, "BeginLoading", "pending.tif", ".tif", Get(w, "_showToken"));
        var old = Pending(viewer, 0.25);
        Call(w, "EndLoading"); // a thumbnail is now visible, but decoding isn't done
        await Task.Delay(300);
        Assert(title.Opacity > 0.9 && fill.ActualWidth > 0, "Loading bar disappeared at the thumbnail stage");
        double previousWidth = fill.ActualWidth;
        Report(Get(Get(viewer, "_activeDecode"), "<Progress>k__BackingField"), 0.65);
        await Task.Delay(300);
        Assert(fill.ActualWidth > previousWidth, "Loading bar did not advance left to right");
        try
        {
            Accent.Apply(Color.FromRgb(40, 180, 110));
            Assert(((SolidColorBrush)((Border)fill).Background).Color == Accent.Color, "Loading line did not follow the chosen accent");
            w.UpdateLayout();
            var snapshot = new RenderTargetBitmap((int)w.ActualWidth, 80, 96, 96, PixelFormats.Pbgra32);
            snapshot.Render(w);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(snapshot));
            string preview = Path.Combine(scratch, "loading-title.png");
            using (var output = File.Create(preview)) png.Save(output);
            Console.WriteLine("Visual check: " + preview);
        }
        finally { Accent.ApplySaved(); }

        Call(w, "BeginLoading", "next.tif", ".tif", Get(w, "_showToken"));
        var current = Pending(viewer, 0.3);
        Call(w, "EndLoading");
        old.SetResult((Pixel, 4, 1, 1));
        await Task.Delay(250);
        Assert(title.Opacity > 0.9 && ((DispatcherTimer)Get(w, "_titleProgressTimer")).IsEnabled,
            "Old decode completion stopped a newer image's progress");
        current.SetResult((Pixel, 4, 1, 1));
        await Task.Delay(650);
        Assert(title.Opacity == 0 && !((DispatcherTimer)Get(w, "_titleProgressTimer")).IsEnabled,
            "Completion did not finish/fade the loading line");
        var closing = Pending(viewer, 0.2);
        w.HideWindow(); closing.SetResult((Pixel, 4, 1, 1));
        await Task.Delay(200);
        Assert(!w.IsVisible && title.Opacity == 0 && !((DispatcherTimer)Get(w, "_titleProgressTimer")).IsEnabled,
            "Closing retained progress or reopened the window");
        Console.WriteLine("PASS thumbnail/detailed decode progress, next-file race, completion fade and close cleanup");

        // Actual WIC TIFF decode, plus Magick and vector paths; no image may keep its file locked.
        string tif = Path.Combine(scratch, "large.tif");
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.None };
        encoder.Frames.Add(BitmapFrame.Create(MakeBitmap(6000, 3000)));
        using (var output = File.Create(tif)) encoder.Save(output);
        foreach (string path in new[] { tif, Path.Combine(samples, "sample.heic"), Path.Combine(samples, "sample.psd"), Path.Combine(samples, "sample.svg") })
        {
            await w.ShowFile(path);
            while (viewer.LoadingProgress != null) await Task.Delay(20);
            await Task.Delay(80);
            Assert(((Image)Get(viewer, "PreviewImage")).Source is BitmapSource, "Image decode failed: " + path);
            w.HideWindow();
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Console.WriteLine("PASS large TIFF, HEIC, PSD and SVG decoding + immediate file release");

        // Exercise the same ancestor routing with actual table/PDF scroll hosts.
        string csv = Path.Combine(scratch, "wide.csv");
        File.WriteAllText(csv, string.Join(",", Enumerable.Range(0, 40).Select(i => "Wide_column_" + i)) + "\n" +
            string.Join(",", Enumerable.Range(0, 40).Select(i => "value")));
        await w.ShowFile(csv);
        var csvViewer = (CsvViewer)Get(w, "CsvViewerControl");
        await CheckScroll(w, (ScrollViewer)Get(csvViewer, "TableScroller"), (DependencyObject)Get(csvViewer, "Table"));
        await w.ShowFile(Path.Combine(samples, "sample.pdf"));
        var pdf = (PdfViewer)Get(w, "PdfViewerControl");
        pdf.GetType().GetMethod("SetZoom", Private)!.Invoke(pdf, new object[] { 3.0 });
        await Task.Delay(200);
        var pdfScroller = (ScrollViewer)Get(pdf, "PagesScrollViewer");
        await CheckScroll(w, pdfScroller, (DependencyObject)pdfScroller.Content);
        Console.WriteLine("PASS horizontal wheel routing in actual CSV/PDF views, direction, boundaries and vertical offset");
        w.HideWindow(); w.Close();

        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lupik", "settings.json");
        byte[]? savedSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        bool oldSetting = Settings.Current.PinnedOnTop;
        try
        {
            var pinned = (MainWindow)typeof(MainWindow).GetMethod("CreatePinned", Static)!.Invoke(null, null)!;
            await pinned.ShowFile(Path.Combine(samples, "sample.png"));
            pinned.Topmost = true;
            Call(pinned, "UpdatePinButton");
            Assert(((Button)Get(pinned, "AlwaysOnTopButton")).IsVisible, "No visible always-on-top control");
            ((Button)Get(pinned, "AlwaysOnTopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(pinned.Pinned && !pinned.Topmost && !Settings.Current.PinnedOnTop && pinned.IsVisible,
                "Disabling always-on-top closed/unpinned the preview or forgot the preference");
            var ordinary = new MainWindow();
            await ordinary.ShowFile(Path.Combine(samples, "sample.png"));
            Call(ordinary, "TogglePin");
            var nextPinned = Application.Current.Windows.OfType<MainWindow>().Single(candidate => candidate.Pinned && candidate != pinned);
            Assert(!nextPinned.Topmost && pinned.IsVisible && nextPinned.IsVisible,
                "New pinned preview ignored the remembered on-top mode or closed the existing one");
            nextPinned.Close(); ordinary.Close();
            Call(pinned, "TogglePin");
            Assert(pinned.Topmost && Settings.Current.PinnedOnTop, "P no longer toggles always-on-top");
            pinned.Close();
        }
        finally
        {
            Settings.Current.PinnedOnTop = oldSetting;
            if (savedSettings != null) File.WriteAllBytes(settingsPath, savedSettings);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
        Console.WriteLine("PASS visible pinned-window control, independent pin/on-top states and saved preference");
    }

    static async Task CheckScroll(MainWindow window, ScrollViewer scroller, DependencyObject source)
    {
        window.UpdateLayout();
        Assert(scroller.ScrollableWidth > 0, "Test view has no horizontal overflow");
        double top = scroller.VerticalOffset;
        var method = typeof(MainWindow).GetMethod("TryHorizontalScroll", Static)!;
        Assert((bool)method.Invoke(null, new object[] { source, -120 })!, "Horizontal scrolling was not handled");
        await Task.Delay(80);
        Assert(scroller.HorizontalOffset > 0 && scroller.VerticalOffset == top, "Wheel did not scroll horizontally only");
        method.Invoke(null, new object[] { source, 120 });
        await Task.Delay(80);
        Assert(scroller.HorizontalOffset == 0, "Reverse wheel did not return left");
        Assert((bool)method.Invoke(null, new object[] { source, 120 })!, "Boundary wheel leaked to vertical scrolling");
    }
}
