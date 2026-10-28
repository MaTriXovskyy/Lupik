using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lupik;
using Lupik.Core;
using Lupik.Views;
using PixelPoint = System.Drawing.Point;
using PixelRect = System.Drawing.Rectangle;

static class ScreenshotChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static object Get(object o, string field) => o.GetType().GetField(field, Private)!.GetValue(o)!;

    public static async Task Run(string samples)
    {
        Accent.ApplySaved();
        Lupik.Localization.Loc.Instance.SetLanguage(Settings.Current.Language);
        ScreenshotShortcut.Reset();
        int calls = 0;
        Action requested = () => calls++;
        var chord = ModifierKeys.Windows | ModifierKeys.Shift;
        for (int modifiers = 0; modifiers < 16; modifiers++)
            if ((ModifierKeys)modifiers != chord)
                Assert(!ScreenshotShortcut.Process(0x53, true, (ModifierKeys)modifiers, true, requested), "Screenshot took an unrelated S chord");
        for (uint key = 1; key < 255; key++)
            if (key != 0x53) Assert(!ScreenshotShortcut.Process(key, true, chord, true, requested), "Screenshot took an unrelated key");
        Assert(!ScreenshotShortcut.Process(0x53, true, chord, false, requested), "Disabled capture intercepted Windows snipping");
        Assert(ScreenshotShortcut.Process(0x53, true, chord, true, requested) && calls == 1, "Win+Shift+S did not request a capture");
        Assert(ScreenshotShortcut.Process(0x53, true, ModifierKeys.None, false, requested) && calls == 1, "Held S repeated capture or leaked after modifiers changed");
        Assert(ScreenshotShortcut.Process(0x53, false, ModifierKeys.None, false, requested), "Capture's S release leaked after modifiers were released");
        Assert(!ScreenshotShortcut.Process(0x53, true, ModifierKeys.None, true, requested), "Ordinary typing was swallowed after capture");
        Assert(ScreenshotShortcut.Process(0x53, true, chord, true, requested) && calls == 2, "Second physical capture press was ignored");
        ScreenshotShortcut.Process(0x53, false, chord, true, requested);
        ScreenshotShortcut.Reset();
        Console.WriteLine("PASS exact Win+Shift+S routing, all other modifier/key combinations, repeats, release ordering and disable/restore");

        var desktop = new PixelRect(-500, -100, 800, 400);
        var expected = new PixelRect(-450, -50, 500, 200);
        foreach (var (a, b) in new[]
        {
            (new PixelPoint(-450,-50), new PixelPoint(50,150)),
            (new PixelPoint(50,150), new PixelPoint(-450,-50)),
            (new PixelPoint(-450,150), new PixelPoint(50,-50)),
            (new PixelPoint(50,-50), new PixelPoint(-450,150))
        }) Assert(ScreenshotSession.SelectionBetween(a,b,desktop) == expected, "Reverse/cross-monitor selection coordinates are incorrect");
        Assert(ScreenshotSession.SelectionBetween(new(-1000,-1000), new(2000,2000), desktop) == desktop, "Selection escaped virtual desktop bounds");

        var pixels = new byte[800 * 400 * 4];
        for (int y = 0; y < 400; y++) for (int x = 0; x < 800; x++)
        {
            int p = (y * 800 + x) * 4;
            pixels[p] = 77; pixels[p+1] = (byte)y; pixels[p+2] = (byte)x; pixels[p+3] = 255;
        }
        var image = BitmapSource.Create(800,400,96,96,PixelFormats.Bgra32,null,pixels,800*4); image.Freeze();
        var topWindow = new ScreenshotWindow(new IntPtr(1),1,new(-300,-50,300,200),new(-300,-50,300,200));
        var behind = new ScreenshotWindow(new IntPtr(2),1,new(-500,-100,700,300),new(-500,-100,700,300));
        var choosing = new ScreenshotSession(image,desktop,new[]{new PixelRect(-500,-100,400,400),new PixelRect(-100,-100,400,400)},new[]{topWindow,behind});
        choosing.SetMode(ScreenshotMode.Window,remember:false); choosing.Move(new(-250,0));
        Assert(choosing.Selection == topWindow.Bounds,"Window selection ignored the topmost window under the pointer");
        choosing.Move(new(-450,0)); Assert(choosing.Selection == behind.Bounds,"Window selection did not follow the pointer");
        choosing.SetMode(ScreenshotMode.Monitor,remember:false); choosing.Move(new(-450,0));
        Assert(choosing.Selection == new PixelRect(-500,-100,400,400),"Screen mode did not select the pointed monitor");
        choosing.Move(new(100,0)); Assert(choosing.Selection == new PixelRect(-100,-100,400,400),"Screen mode did not switch monitors");
        choosing.SetMode(ScreenshotMode.Desktop,remember:false); Assert(choosing.Selection == desktop,"All-monitor capture omitted part of the virtual desktop");
        choosing.Cancel();
        Console.WriteLine("PASS window hit-testing and z-order, individual monitor selection and all-monitor coverage");
        var cropped = ScreenshotService.Crop(image, desktop, expected);
        Assert(cropped.IsFrozen && cropped.PixelWidth == 500 && cropped.PixelHeight == 200, "Crop dimensions/freeze are incorrect");
        var sample = new byte[4]; cropped.CopyPixels(new Int32Rect(0,0,1,1),sample,4,0);
        Assert(sample.SequenceEqual(new byte[]{77,50,50,255}), "Negative desktop origin shifted screenshot pixels");
        string path = await ScreenshotStore.SaveAsync(cropped);
        string second = await ScreenshotStore.SaveAsync(cropped);
        Assert(path != second, "Two captures overwrote each other");
        using (var file = File.OpenRead(path))
        {
            var saved = BitmapDecoder.Create(file,BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0];
            Assert(saved.PixelWidth == 500 && saved.PixelHeight == 200, "Temporary PNG did not preserve exact pixel dimensions");
        }
        Console.WriteLine("PASS physical-pixel crops with negative monitor coordinates, all drag directions, clipping and unique lossless PNGs");

        var region = new PixelRect(100,100,800,400);
        var session = new ScreenshotSession(image,region,new[]{region});
        var selectionTask = session.SelectAsync(activate:false);
        string folder = Path.Combine(Path.GetTempPath(), "Lupik-screenshot-check-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            await Task.Delay(180);
            var overlay = Application.Current.Windows.OfType<ScreenshotOverlay>().Single();
            GetWindowRect(overlay.Handle,out var actual);
            Assert(actual.Left == region.Left && actual.Top == region.Top && actual.Right == region.Right && actual.Bottom == region.Bottom,
                "Overlay's native bounds do not match physical monitor pixels");
            session.Begin(new(250,200)); session.Move(new(650,400));
            overlay.UpdateLayout();
            var surface = (FrameworkElement)overlay.Content;
            var captureToolbar=((System.Windows.Controls.Grid)surface).Children.OfType<ScreenshotToolbar>().Single();
            var modeButtons=(FrameworkElement)captureToolbar.FindName("Modes");
            var modeCenter=modeButtons.TranslatePoint(new Point(modeButtons.ActualWidth/2,modeButtons.ActualHeight/2),surface);
            Assert(Math.Abs(modeCenter.X-surface.ActualWidth/2)<0.6,"Capture modes are not centered on the monitor independently of the logo");
            var preview = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32);
            preview.Render(surface);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(preview));
            string visual = Path.Combine(folder,"overlay.png"); using(var stream=File.Create(visual)) png.Save(stream);
            Console.WriteLine("Visual check: "+visual);
            session.Complete(new(650,400));
            Assert((await selectionTask)?.Area == new PixelRect(250,200,400,200), "Overlay completion returned the wrong selection");
            Assert(!Application.Current.Windows.OfType<ScreenshotOverlay>().Any(), "Completed capture retained overlay windows");
        }
        finally { session.Cancel(); }
        var cancelled = new ScreenshotSession(image,region,new[]{region});
        var cancelTask = cancelled.SelectAsync(activate:false);
        cancelled.Cancel(); cancelled.Cancel();
        Assert(await cancelTask == null && !Application.Current.Windows.OfType<ScreenshotOverlay>().Any(), "Cancel retained a capture or overlay");
        Console.WriteLine("PASS native overlay placement, selection completion, cancellation and immediate overlay cleanup");

        foreach(var mode in new[]{ScreenshotMode.Monitor,ScreenshotMode.Desktop})
        {
            var monitors=mode==ScreenshotMode.Monitor ? new[]{region} : new[]{new PixelRect(100,100,400,400),new PixelRect(500,100,400,400)};
            var instant=new ScreenshotSession(image,region,monitors,initialMode:mode);
            var completed=instant.SelectAsync(activate:false);
            try
            {
                var overlay=Application.Current.Windows.OfType<ScreenshotOverlay>().First();
                var toolbar=((System.Windows.Controls.Grid)overlay.Content).Children.OfType<ScreenshotToolbar>().Single();
                var button=(System.Windows.Controls.RadioButton)toolbar.FindName(mode==ScreenshotMode.Monitor ? "MonitorMode" : "DesktopMode");
                Assert(button.IsChecked==true,"Instant-capture regression must also cover an already selected mode");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert(completed.IsCompleted,"Fullscreen still requires a second click after choosing its toolbar button");
                var shot=await completed;
                Assert(shot?.Area==(mode==ScreenshotMode.Monitor ? monitors[0] : region) && shot.Mode==mode,"Instant capture selected the wrong screen area");
            }
            finally { instant.Cancel(); }
        }
        Console.WriteLine("PASS single-click screen/all-screen capture, including modes that are already selected");

        var window = new MainWindow();
        var screenshots = new ScreenshotService(window);
        try
        {
            IntPtr foreground=NativeMethods.GetForegroundWindow();
            screenshots.PresentCapture(path,cropped,IntPtr.Zero,true);
            await Task.Delay(100);
            Assert(!window.IsVisible,"Capture opened the preview before the notification was clicked");
            Assert(NativeMethods.GetForegroundWindow()==foreground,"Screenshot notification stole keyboard focus");
            Assert(screenshots.HasLast,"A capture cannot be recovered after the notification disappears");
            var notification=Application.Current.Windows.OfType<ScreenshotNotification>().Single();
            var toastPreview=new RenderTargetBitmap((int)Math.Ceiling(notification.ActualWidth),(int)Math.Ceiling(notification.ActualHeight),96,96,PixelFormats.Pbgra32);
            toastPreview.Render(notification);
            var toastPng=new PngBitmapEncoder(); toastPng.Frames.Add(BitmapFrame.Create(toastPreview));
            string toastVisual=Path.Combine(folder,"notification.png"); using(var output=File.Create(toastVisual)) toastPng.Save(output);
            Console.WriteLine("Visual check: "+toastVisual);
            await notification.OpenAsync();
            Assert((string)Get(window,"_currentFilePath") == path && window.IsVisible, "Screenshot did not open in the image preview");
            var imageViewer = (ImageViewer)Get(window,"ImageViewerControl");
            Assert(imageViewer.NaturalWidth == 500 && imageViewer.NaturalHeight == 200, "Preview rescaled the stored screenshot dimensions");
            window.HideWindow();
            screenshots.PresentCapture(second,cropped,IntPtr.Zero,true);
            Application.Current.Windows.OfType<ScreenshotNotification>().Single().Dismiss();
            Assert(!window.IsVisible,"Dismissing a notification opened the screenshot");
            await screenshots.OpenLastAsync();
            Assert((string)Get(window,"_currentFilePath")==second && window.IsVisible,"Last screenshot did not reopen the dismissed capture");
            string original = Path.Combine(samples,"sample.txt");
            await window.ShowFile(original);
            window.EditMode = true;
            await window.ShowScreenshotAsync(second,IntPtr.Zero,true);
            Assert(window.EditMode && (string)Get(window,"_currentFilePath") == original, "Screenshot replaced an active editor");
            var pinned = Application.Current.Windows.OfType<MainWindow>().Single(w=>w.Pinned);
            Assert((string)Get(pinned,"_currentFilePath") == second, "Screenshot was lost while preserving the editor");
            pinned.Close(); window.EditMode=false;
        }
        finally { screenshots.Dispose(); window.EditMode=false; window.HideWindow(); window.Close(); ScreenshotStore.CleanCurrentSession(); }
        Console.WriteLine("PASS non-activating thumbnail notification, preview only on click, dismiss/reopen-last and preservation of an active editor");
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
}
