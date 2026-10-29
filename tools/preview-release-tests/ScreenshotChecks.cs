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
        foreach(double scale in new[]{1.0,1.5,2.0})
        {
            using var cursor=new ScreenshotCursor(scale);
            var cursorBitmap=ScreenshotCursor.Draw(scale);
            var cursorPixels=new byte[cursorBitmap.PixelWidth*cursorBitmap.PixelHeight*4]; cursorBitmap.CopyPixels(cursorPixels,cursorBitmap.PixelWidth*4,0);
            Assert(Enumerable.Range(0,cursorPixels.Length/4).Any(i=>cursorPixels[i*4+3]==255 && cursorPixels[i*4]==0),"Crosshair lacks an opaque black outline");
            Assert(Enumerable.Range(0,cursorPixels.Length/4).Any(i=>cursorPixels[i*4+3]==255 && cursorPixels[i*4]==255),"Crosshair lacks an opaque white core");
        }
        Console.WriteLine("PASS opaque black/white native crosshair at 100%, 150% and 200% DPI");

        var pixels = new byte[800 * 400 * 4];
        for (int y = 0; y < 400; y++) for (int x = 0; x < 800; x++)
        {
            int p = (y * 800 + x) * 4;
            pixels[p] = 77; pixels[p+1] = (byte)y; pixels[p+2] = (byte)x; pixels[p+3] = 255;
        }
        var image = BitmapSource.Create(800,400,96,96,PixelFormats.Bgra32,null,pixels,800*4); image.Freeze();
        string previousMode=Settings.Current.ScreenCaptureMode;
        try
        {
            foreach(var remembered in new[]{ScreenshotMode.Region,ScreenshotMode.Window})
            {
                Settings.Update(s=>s.ScreenCaptureMode=remembered.ToString());
                foreach(var temporary in new[]{ScreenshotMode.Monitor,ScreenshotMode.Desktop,ScreenshotMode.Freeform,ScreenshotMode.Scroll})
                {
                    var once=new ScreenshotSession(image,desktop,new[]{desktop},initialMode:remembered);
                    once.InvokeMode(temporary,desktop);
                    Assert(Settings.Current.ScreenCaptureMode==remembered.ToString(),"One-shot capture overwrote the last window/rectangle mode");
                    Assert(ScreenshotModes.Restore(Settings.Current.ScreenCaptureMode)==remembered,"Next capture would reopen a one-shot mode");
                    once.Cancel();
                }
            }
            var persistent=new ScreenshotSession(image,desktop,new[]{desktop},initialMode:ScreenshotMode.Window);
            persistent.SetMode(ScreenshotMode.Region);
            Assert(Settings.Current.ScreenCaptureMode=="Region","Rectangle capture was not remembered");
            persistent.SetMode(ScreenshotMode.Window);
            Assert(Settings.Current.ScreenCaptureMode=="Window","Window capture was not remembered");
            persistent.ToggleTextMode();
            Assert(Settings.Current.ScreenCaptureMode=="Window","Temporary text selection replaced the remembered window mode");
            persistent.Cancel();
            foreach(string legacy in new[]{"Monitor","Desktop","Freeform","Scroll","invalid"})
                Assert(ScreenshotModes.Restore(legacy)==ScreenshotMode.Region,"Legacy temporary setting was restored as the default");
        }
        finally {Settings.Update(s=>s.ScreenCaptureMode=previousMode);}
        Console.WriteLine("PASS one-shot fullscreen/lasso/scroll modes, remembered rectangle/window modes and temporary OCR selection");
        var triangle=new[]{new PixelPoint(-450,-50),new PixelPoint(-250,-50),new PixelPoint(-450,150)};
        var mask=ScreenshotFreeform.Crop(image,desktop,new PixelRect(-450,-50,200,200),triangle);
        byte[] Pixel(BitmapSource source,int x,int y)
        {
            var rgba=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            var value=new byte[4]; rgba.CopyPixels(new Int32Rect(x,y,1,1),value,4,0); return value;
        }
        Assert(Pixel(mask,20,20).SequenceEqual(new byte[]{77,70,70,255}),"Lasso shifted the image inside a negative-origin desktop");
        Assert(Pixel(mask,180,180)[3]==0,"Lasso kept pixels outside its contour");
        var pngBytes=await Task.Run(()=>ScreenshotFreeform.Png(mask));
        string freeformFile=await ScreenshotStore.SaveAsync(mask);
        using(var stored=File.OpenRead(freeformFile))
            Assert(Pixel(BitmapDecoder.Create(stored,BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0],180,180)[3]==0,"Background PNG save lost lasso transparency");
        var clipboardData=ScreenshotFreeform.ClipboardData(mask,pngBytes);
        Assert(clipboardData.GetDataPresent(DataFormats.Bitmap) && clipboardData.GetDataPresent("PNG"),"Freeform clipboard payload lacks bitmap or lossless PNG data");
        using(var payload=(Stream?)clipboardData.GetData("PNG") ?? throw new Exception("Missing PNG stream"))
        {
            var frame=BitmapDecoder.Create(payload,BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0];
            Assert(Pixel(frame,180,180)[3]==0 && Pixel(frame,20,20)[3]==255,"PNG clipboard payload lost lasso transparency");
        }
        var bow=new[]{new PixelPoint(-450,-50),new PixelPoint(-250,150),new PixelPoint(-450,150),new PixelPoint(-250,-50)};
        Assert(ScreenshotFreeform.Geometry(bow).GetArea()>100,"A self-intersecting lasso was incorrectly treated as empty");
        Console.WriteLine("PASS freeform mask, negative desktop origin, transparent exterior and PNG clipboard round-trip");
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
            overlay.UpdateLayout();
            var surface = (FrameworkElement)overlay.Content;
            var captureToolbar=((System.Windows.Controls.Grid)surface).Children.OfType<ScreenshotToolbar>().Single();
            var modeButtons=(FrameworkElement)captureToolbar.FindName("Modes");
            var modeCenter=modeButtons.TranslatePoint(new Point(modeButtons.ActualWidth/2,modeButtons.ActualHeight/2),surface);
            Assert(Math.Abs(modeCenter.X-surface.ActualWidth/2)<0.6,"Capture modes are not centered on the monitor independently of the logo");
            session.Begin(new(250,200));
            Assert(!captureToolbar.IsVisible,"Capture toolbar did not hide as soon as selection started");
            session.Complete(new(250,200));
            Assert(captureToolbar.IsVisible,"Empty selection did not restore capture controls for retry");
            session.Begin(new(250,200)); session.Move(new(650,400));overlay.UpdateLayout();
            Assert(!captureToolbar.IsVisible,"Capture toolbar reappeared during a drag");
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

        var freehand=new ScreenshotSession(image,region,new[]{region},initialMode:ScreenshotMode.Freeform);
        var freehandResult=freehand.SelectAsync(activate:false);
        try
        {
            freehand.Begin(new(220,290)); freehand.Move(new(260,290)); freehand.Complete(new(280,290));
            Assert(!freehandResult.IsCompleted && !freehand.Dragging,"An empty lasso produced a screenshot");
            freehand.Begin(new(250,280)); freehand.Move(new(640,300)); freehand.Move(new(700,430)); freehand.Move(new(300,450));
            await Task.Delay(80);
            var overlay=Application.Current.Windows.OfType<ScreenshotOverlay>().Single(); overlay.UpdateLayout();
            Assert(!((System.Windows.Controls.Grid)overlay.Content).Children.OfType<ScreenshotToolbar>().Single().IsVisible,"Toolbar obscured the lasso contour");
            var surface=(FrameworkElement)overlay.Content;
            var preview=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32); preview.Render(surface);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(preview));
            string visual=Path.Combine(folder,"lasso.png"); using(var output=File.Create(visual)) png.Save(output);
            Console.WriteLine("Visual check: "+visual);
            freehand.Complete(new(300,450));
            var shot=await freehandResult ?? throw new Exception("Valid lasso returned no capture");
            Assert(shot is { Mode:ScreenshotMode.Freeform,Outline.Count:>=3 } && shot.Area==new PixelRect(250,280,450,170),"Lasso completion lost its contour or bounding box");
            var extracted=ScreenshotFreeform.Crop(image,region,shot.Area,shot.Outline!);
            Assert(Pixel(extracted,440,5)[3]==0 && Pixel(extracted,200,80)[3]==255,"Closed lasso did not match its preview shape");
        }
        finally { freehand.Cancel(); }
        var cursorVisual=new DrawingVisual();
        using(var drawing=cursorVisual.RenderOpen())
        {
            for(int i=0;i<5;i++)
            {
                byte shade=(byte)(i*255/4);
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(shade,shade,shade)),null,new Rect(i*80,0,80,80));
                drawing.DrawImage(ScreenshotCursor.Draw(1.5),new Rect(i*80+16,16,48,48));
            }
        }
        var cursorPreview=new RenderTargetBitmap(400,80,96,96,PixelFormats.Pbgra32); cursorPreview.Render(cursorVisual);
        var cursorPng=new PngBitmapEncoder(); cursorPng.Frames.Add(BitmapFrame.Create(cursorPreview));
        string cursorFile=Path.Combine(folder,"crosshair.png"); using(var output=File.Create(cursorFile)) cursorPng.Save(output);
        Console.WriteLine("Visual check: "+cursorFile);
        Console.WriteLine("PASS automatic lasso closure, empty-gesture rejection and preview/output shape agreement");

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
