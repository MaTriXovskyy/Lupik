using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Lupik;
using Lupik.Core;
using Lupik.Views;
using PixelRect=System.Drawing.Rectangle;
using PixelPoint=System.Drawing.Point;

static class ScrollingAndTextChecks
{
    static void Assert(bool value,string message) {if(!value) throw new Exception(message);}
    static BitmapSource RandomDocument(int seed)
    {
        const int width=320,height=1600;
        var pixels=new byte[width*height*4];new Random(seed).NextBytes(pixels);
        var result=BitmapSource.Create(width,height,96,96,PixelFormats.Bgr32,null,pixels,width*4);result.Freeze();return result;
    }
    static BitmapSource Frame(BitmapSource document,int y,int x=0)
    {
        var result=new CroppedBitmap(document,new Int32Rect(x,y,320,400));result.Freeze();return result;
    }
    static void SamePixels(BitmapSource a,BitmapSource b)
    {
        Assert(a.PixelWidth==b.PixelWidth && a.PixelHeight==b.PixelHeight,"Stitched dimensions differ from source content");
        int stride=a.PixelWidth*4;
        var one=new byte[stride*a.PixelHeight];var two=new byte[one.Length];
        new FormatConvertedBitmap(a,PixelFormats.Bgr32,null,0).CopyPixels(one,stride,0);
        new FormatConvertedBitmap(b,PixelFormats.Bgr32,null,0).CopyPixels(two,stride,0);
        for(int i=0;i<one.Length;i++) if(i%4!=3 && one[i]!=two[i]) throw new Exception($"Stitch contains a missing or repeated row at byte {i}");
    }
    static BitmapSource TextDocument(bool shortText=false)
    {
        int height=shortText ? 150 : 1600,width=shortText ? 700 : 320;
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
            int rows=shortText ? 1 : 50;
            for(int row=0;row<rows;row++)
            {
                var text=new FormattedText(shortText ? "SCREEN TEXT 123" : $"Row {row:00}: sample document text {row*173}",System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,new Typeface("Segoe UI"),shortText ? 32 : 13,Brushes.Black,1);
                dc.DrawText(text,new Point(12,shortText ? 45 : row*31+6));
                if(!shortText && row%7==0) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(row*11),90,140)),null,new Rect(260,row*31+5,40,18));
            }
        }
        var result=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
    }

    public static async Task Run()
    {
        Accent.ApplySaved();
        Lupik.Localization.Loc.Instance.SetLanguage(Settings.Current.Language);
        string folder=Path.Combine(Path.GetTempPath(),"Lupik-scroll-text-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        void SaveVisual(FrameworkElement view,string name,int width,int height)
        {
            var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
            string path=Path.Combine(folder,name+".png");using(var stream=File.Create(path)) png.Save(stream);
            Console.WriteLine("Visual check: "+path);
        }
        var document=RandomDocument(2810);
        var stitch=new ScrollStitcher(Frame(document,0));
        Assert(stitch.Append(Frame(document,0))==ScrollAppendResult.Unchanged,"Stationary content was duplicated");
        foreach(int offset in new[]{96,240,480}) Assert(stitch.Append(Frame(document,offset))==ScrollAppendResult.Added,"Overlapping content did not append");
        Assert(stitch.Append(Frame(document,360))==ScrollAppendResult.Backwards,"Upward scrolling was appended as new content");
        Assert(stitch.Append(Frame(RandomDocument(99),0))==ScrollAppendResult.NoOverlap,"Unrelated content was joined to a scrolling screenshot");
        Assert(stitch.Append(Frame(document,640))==ScrollAppendResult.Added,"Capture did not resume after scrolling back");
        SamePixels(stitch.Build(),new CroppedBitmap(document,new Int32Rect(0,0,320,1040)));
        var page=TextDocument();var textStitch=new ScrollStitcher(Frame(page,0));
        foreach(int offset in new[]{110,260,490}) Assert(textStitch.Append(Frame(page,offset))==ScrollAppendResult.Added,"Text-page overlap matching failed");
        SamePixels(textStitch.Build(),new CroppedBitmap(page,new Int32Rect(0,0,320,890)));
        Console.WriteLine("PASS pixel-exact scrolling seams on images and text pages, unchanged frames, backtracking and unrelated-frame rejection");

        var area=new PixelRect(100,100,320,400);
        int calls=0;
        using(var scrolling=new ScrollCaptureSession(area,null,_=>Frame(document,Interlocked.Increment(ref calls) switch {1=>0,2=>96,_=>240})))
        {
            IntPtr foreground=NativeMethods.GetForegroundWindow();
            var result=scrolling.RunAsync();
            await Task.Delay(1250);
            Assert(NativeMethods.GetForegroundWindow()==foreground,"Scrolling controls stole keyboard focus");
            var control=Application.Current.Windows.OfType<ScrollCaptureControl>().Single();control.UpdateLayout();
            SaveVisual(control,"scroll-control",(int)Math.Ceiling(control.ActualWidth),(int)Math.Ceiling(control.ActualHeight));
            scrolling.Complete();
            var joined=await result;
            Assert(joined!=null && calls>=3,"Scrolling session did not collect frames");
            SamePixels(joined!,new CroppedBitmap(document,new Int32Rect(0,0,320,640)));
        }
        using(var cancelled=new ScrollCaptureSession(area,null,_=>Frame(document,0)))
        {
            var result=cancelled.RunAsync();await Task.Delay(70);cancelled.Cancel();
            Assert(await result==null,"Cancelled scrolling capture produced an image");
        }
        Assert(!Application.Current.Windows.OfType<ScrollCaptureControl>().Any(),"Scrolling capture retained controls after finishing");
        Console.WriteLine("PASS scrolling controller collection, non-activating controls, finish, cancellation and cleanup");

        var picker=new ScreenshotSession(Frame(document,0),area,new[]{area},initialMode:ScreenshotMode.Freeform);
        picker.ToggleTextMode();Assert(picker.TextMode && picker.Mode==ScreenshotMode.Region,"T did not enable rectangular text capture");
        picker.ToggleTextMode();Assert(!picker.TextMode,"Second T did not restore image capture");
        var selection=picker.SelectAsync(activate:false);
        try
        {
            picker.Begin(new PixelPoint(120,120));picker.ToggleTextMode();picker.Complete(new PixelPoint(300,350));
            Assert((await selection)?.TextOnly==true,"Text mode was lost when completing a region");
        }
        finally {picker.Cancel();}
        var fresh=new ScreenshotSession(Frame(document,0),area,new[]{area});
        Assert(!fresh.TextMode,"Text mode leaked into the next screenshot session");fresh.Cancel();
        var toolbar=new ScreenshotToolbar(true);toolbar.SetMode(ScreenshotMode.Region,textMode:true);
        toolbar.Measure(new Size(1280,100));toolbar.Arrange(new Rect(0,0,1280,80));toolbar.UpdateLayout();
        SaveVisual(toolbar,"text-toolbar",1280,80);
        if(TextRecognition.IsAvailable)
        {
            string read=await ScreenshotText.ReadAsync(TextDocument(shortText:true));
            Assert(read.Contains("SCREEN") && read.Contains("TEXT") && read.Contains("123"),"Screenshot OCR did not recover its text");
        }
        int copied=0;
        var toast=new ScreenshotNotification("SCREEN TEXT 123",true,()=>{copied++;return Task.CompletedTask;});
        toast.Show();toast.UpdateLayout();SaveVisual(toast,"text-notification",(int)Math.Ceiling(toast.ActualWidth),(int)Math.Ceiling(toast.ActualHeight));
        await toast.OpenAsync();await toast.OpenAsync();
        Assert(copied==1,"Text notification copied twice for one activation");
        var preview=new MainWindow();using(var owner=new Owner(new ScreenshotService(preview)))
        {
            owner.Service.PresentText("SCREEN TEXT 123",true);
            Assert(!preview.IsVisible,"Text capture opened an image preview");
        }
        preview.Close();
        Console.WriteLine("PASS T toggle, image default for new captures, local OCR and text-only notification activation");
    }
    sealed class Owner(ScreenshotService service) : IDisposable {internal ScreenshotService Service=>service;public void Dispose()=>service.Dispose();}
}
