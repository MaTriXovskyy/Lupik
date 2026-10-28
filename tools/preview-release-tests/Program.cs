using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Lupik;

class Check {
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
 static void AssertOnTop(MainWindow w) {
  if(w.Topmost || (GetWindowLong(w.Hwnd,-20)&8)!=0) throw new Exception("Preview incorrectly stays always-on-top");
  if((GetWindowLong(w.Hwnd,-20)&0x08000000)!=0) throw new Exception("Preview rejects click activation");
 }
 static object Field(object o,string n) => o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.GetValue(o)!;
 [STAThread] static int Main(string[] args) {
  var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Lupik;component/Theme.xaml", UriKind.Relative) });
  typeof(MainWindow).GetField("SuppressActivationForTests",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,true);
  int result=0; var samples = System.IO.Path.Combine(args[0], "test_samples");
  app.Dispatcher.InvokeAsync(async () => {
   try {
    var w = new MainWindow();
    var source = new Window { ShowActivated=false, Width=100, Height=100 };
    source.Show();
    var sourceHandle = new System.Windows.Interop.WindowInteropHelper(source).Handle;
    w.SourceWindowField = sourceHandle;
    w.ShowWelcome();
    AssertOnTop(w);
    source.WindowState = WindowState.Minimized;
    var foregroundChanged=typeof(MainWindow).GetMethod("OnForegroundChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
    foregroundChanged.Invoke(w,new object[]{GetDesktopWindow()});
    AssertOnTop(w);
    foregroundChanged.Invoke(w,new object[]{sourceHandle});
    AssertOnTop(w);
    w.WindowState=WindowState.Minimized;
    typeof(MainWindow).GetMethod("BringToFront",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,null);
    if(w.WindowState!=WindowState.Normal) throw new Exception("Preview did not restore independently");
    AssertOnTop(w);
    w.HideWindow(); source.Close();
    Console.WriteLine("PASS normal activatable window with minimized source, foreground changes and restore");
    foreach(var file in new[]{"sample.pdf","sample.png","sample.txt","sample.docx","sample.xlsx","sample.mp3","sample.mp4"}) {
     var path=System.IO.Path.Combine(samples,file);
     await w.ShowFile(path);
     w.HideWindow();
     using(var stream = new System.IO.FileStream(path,System.IO.FileMode.Open,System.IO.FileAccess.ReadWrite,System.IO.FileShare.None)) {}
     if(Field(Field(w,"PdfViewerControl"),"_pdfDoc") != null) throw new Exception("PDF retained");
     if(((System.Windows.Controls.Image)Field(Field(w,"ImageViewerControl"),"PreviewImage")).Source != null) throw new Exception("Image retained");
     Console.WriteLine("PASS immediate exclusive access + cleanup: "+file);
    }
    var pending=w.ShowFile(System.IO.Path.Combine(samples,"sample.docx"));
    w.HideWindow(); await pending;
    if(w.IsVisible) throw new Exception("Closed preview reopened");
    Console.WriteLine("PASS close during background load");
    foreach(var file in new[]{"sample.pdf","sample.png","sample.xlsx","sample.mp3","sample.mp4"}) {
     var loading=w.ShowFile(System.IO.Path.Combine(samples,file));
     w.HideWindow(); await loading;
     if(w.IsVisible) throw new Exception("Closed preview reopened: "+file);
     using(var stream = new System.IO.FileStream(System.IO.Path.Combine(samples,file),System.IO.FileMode.Open,System.IO.FileAccess.ReadWrite,System.IO.FileShare.None)) {}
     Console.WriteLine("PASS close during load: "+file);
    }
    var pinned=(MainWindow)typeof(MainWindow).GetMethod("CreatePinned",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
    var pdf=System.IO.Path.Combine(samples,"sample.pdf");
    await pinned.ShowFile(pdf); pinned.Close();
    using(var stream = new System.IO.FileStream(pdf,System.IO.FileMode.Open,System.IO.FileAccess.ReadWrite,System.IO.FileShare.None)) {}
    if(Field(Field(pinned,"PdfViewerControl"),"_pdfDoc") != null) throw new Exception("Pinned PDF retained");
    Console.WriteLine("PASS pinned close releases PDF");
    await EditAndWindowChecks.Run(samples);
    await LoadingAndScrollingChecks.Run(samples);
    w.Close();
   } catch(Exception ex) { Console.WriteLine(ex); result=1; }
   finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
  });
  Dispatcher.Run(); return result;
 }
}
