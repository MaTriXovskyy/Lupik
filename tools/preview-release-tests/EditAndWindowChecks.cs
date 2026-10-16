using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Lupik;
using Lupik.Views;
using System.Runtime.InteropServices;

static class EditAndWindowChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Get(object obj, string name) => obj.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(obj)!;
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Private | BindingFlags.Public)!.SetValue(obj, value);
    static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Private)!.Invoke(obj, args)!;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static MainWindow Pinned() => (MainWindow)typeof(MainWindow).GetMethod("CreatePinned", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
    static TextEditor Editor(CodeViewer code) => (TextEditor)Get(code, "TextEditorControl");
    static Task Save(MainWindow w) => (Task)Call(w, "SaveEditAsync", false, null!);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static void AnswerNextDialog(UnsavedChoice choice)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<UnsavedChangesDialog>().FirstOrDefault();
            if (dialog == null) return;
            timer.Stop();
            typeof(UnsavedChangesDialog).GetMethod("Close", Private, new[] { typeof(UnsavedChoice) })!.Invoke(dialog, new object[] { choice });
        };
        timer.Start();
    }

    public static async Task Run(string samples)
    {
        string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lupik-edit-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(scratch);
        string path = System.IO.Path.Combine(scratch, "original.txt");
        System.IO.File.WriteAllText(path, "original");

        var code = new CodeViewer();
        await code.LoadFileAsync(path);
        code.BeginEdit();
        Editor(code).Text = "snapshot";
        string snapshot = System.IO.Path.Combine(scratch, "snapshot.txt");
        var write = code.SaveToAsync(snapshot);
        Editor(code).AppendText(" + later changes");
        await write;
        await code.ReloadAsync(snapshot);
        Assert(code.IsDirty, "Later edits were marked saved");
        Assert(System.IO.File.ReadAllText(snapshot) == "snapshot", "Unexpected saved snapshot");
        code.EndEdit();
        Console.WriteLine("PASS later changes cannot become the clean saved snapshot");

        var busyWindow = Pinned();
        await busyWindow.ShowFile(path);
        var gate = new GateEditor();
        Set(busyWindow, "_editing", gate);
        Set(busyWindow, "_editingPath", path);
        busyWindow.EditMode = true;
        var saving = Save(busyWindow);
        Assert(!gate.IsEnabled, "Editor stays enabled while saving");
        busyWindow.Close();
        busyWindow.HideWindow();
        await busyWindow.ShowFile(System.IO.Path.Combine(samples, "sample.png"));
        Assert(busyWindow.IsVisible && busyWindow.EditMode, "Save allowed closing or navigation");
        Assert((string)Get(busyWindow, "_currentFilePath") == path, "Save allowed switching documents");
        gate.Completion.SetException(new System.IO.IOException("Simulated write failure"));
        await saving;
        Assert(gate.IsEnabled && gate.IsDirty && gate.ReloadCalls == 0 && busyWindow.EditMode, "Failed save discarded/marked edits clean");
        gate.IsDirty = false;
        busyWindow.Close();
        Console.WriteLine("PASS save freezes editing/navigation/close, failure keeps dirty state and re-enables editor");

        var pinned = Pinned();
        await pinned.ShowFile(path);
        var pc = (CodeViewer)Get(pinned, "CodeViewerControl");
        Call(pinned, "EnterEdit");
        Editor(pc).AppendText("-unsaved");
        AnswerNextDialog(UnsavedChoice.Cancel);
        pinned.Close();
        Assert(pinned.IsVisible && pinned.EditMode && pc.IsDirty, "Cancel did not prevent native close");
        AnswerNextDialog(UnsavedChoice.Discard);
        pinned.Close();
        Assert(!pinned.IsVisible && System.IO.File.ReadAllText(path) == "original", "Discard saved edits or left window open");
        Console.WriteLine("PASS native pinned close asks, Cancel keeps edits, Discard closes without changing source");

        var saveClose = Pinned();
        await saveClose.ShowFile(path);
        var sc = (CodeViewer)Get(saveClose, "CodeViewerControl");
        Call(saveClose, "EnterEdit");
        Editor(sc).AppendText("-saved");
        var closed = new TaskCompletionSource();
        saveClose.Closed += (_, _) => closed.TrySetResult();
        AnswerNextDialog(UnsavedChoice.Save);
        saveClose.Close();
        Assert(await Task.WhenAny(closed.Task, Task.Delay(10000)) == closed.Task, "Save then close did not finish");
        Assert(System.IO.File.ReadAllText(path) == "original-saved", "Save then close lost content");
        Console.WriteLine("PASS native close Save commits content before closing");

        string pdfPath = System.IO.Path.Combine(scratch, "edited.pdf");
        System.IO.File.Copy(System.IO.Path.Combine(samples, "sample.pdf"), pdfPath);
        var pdfWindow = Pinned();
        await pdfWindow.ShowFile(pdfPath);
        Call(pdfWindow, "EnterEdit");
        var pdfViewer = (PdfViewer)Get(pdfWindow, "PdfViewerControl");
        var pdfDoc = (Lupik.Core.PdfDocument)Get(pdfViewer, "_pdfDoc");
        var before = pdfDoc.PageSizePoints(0);
        pdfDoc.RotatePage(0, 1);
        Set(pdfViewer, "_dirty", true);
        pdfViewer.ReleaseFile();
        Assert(ReferenceEquals(Get(pdfViewer, "_pdfDoc"), pdfDoc) && pdfViewer.IsDirty, "Releasing source discarded unsaved PDF");
        await Save(pdfWindow);
        Assert(!pdfViewer.IsDirty && Get(pdfViewer, "_pdfDoc") != null, "PDF save/reload did not complete");
        var after = ((Lupik.Core.PdfDocument)Get(pdfViewer, "_pdfDoc")).PageSizePoints(0);
        Assert(Math.Abs(after.Item1 - before.Item2) < 1 && Math.Abs(after.Item2 - before.Item1) < 1, "PDF rotation not persisted");
        pdfWindow.Close();
        Console.WriteLine("PASS edited PDF retained until successful replace/reload, saved pages preserved");

        var ordinary = new MainWindow();
        await ordinary.ShowFile(path);
        Call(ordinary, "EnterEdit");
        var oc = (CodeViewer)Get(ordinary, "CodeViewerControl");
        Editor(oc).AppendText("-normal");
        await Save(ordinary);
        Assert(!oc.IsDirty && System.IO.File.ReadAllText(path).EndsWith("-normal"), "Normal save failed");
        Call(ordinary, "ExitEdit", null!, true);
        Assert((GetWindowLong(ordinary.Hwnd, -20) & 0x08000000) == 0, "Leaving edit restored NOACTIVATE");
        Call(ordinary, "OpenSearch");
        var input = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice,
            new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, ordinary, "ł"));
        input.RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent;
        Call(ordinary, "OnWindowTextInput", ordinary, input);
        Assert((string)Get(ordinary, "_query") == "ł", "Focused ordinary preview cannot type search text");
        Call(ordinary, "CloseSearch");

        var other = new Window { ShowActivated = false, Width = 100, Height = 100 };
        other.Show();
        IntPtr otherHandle = new System.Windows.Interop.WindowInteropHelper(other).Handle;
        SetWindowPos(otherHandle, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
        Call(ordinary, "OnForegroundChanged", otherHandle);
        await ordinary.ShowFile(path);
        bool otherAbove = false;
        for (IntPtr hwnd = GetWindow(ordinary.Hwnd, 3); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, 3))
            if (hwnd == otherHandle) { otherAbove = true; break; }
        Assert(otherAbove && !ordinary.Topmost && (GetWindowLong(ordinary.Hwnd, -20) & 8) == 0, "Loading/reopening visible preview forced it above another window");
        ordinary.Close();
        Assert(!ordinary.IsVisible, "Ordinary native close did not hide preview");
        await ordinary.ShowFile(path);
        Assert(ordinary.IsVisible, "Ordinary native close destroyed reusable preview");
        var oldShortcut = Lupik.Core.Settings.Current.PreviewKey;
        try
        {
            Lupik.Core.Settings.Current.PreviewKey = new Lupik.Core.KeyCombo(0x51, System.Windows.Input.ModifierKeys.None);
            var keyEvent = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(ordinary), 0, System.Windows.Input.Key.Q)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            Call(ordinary, "OnWindowKeyDown", ordinary, keyEvent);
            Assert(keyEvent.Handled && !ordinary.IsVisible, "Focused preview ignores configured preview shortcut");
        }
        finally { Lupik.Core.Settings.Current.PreviewKey = oldShortcut; }
        await ordinary.ShowFile(path);
        other.Close();
        QueueSlowFinalizer();
        GC.Collect();
        Assert(SlowFinalizer.Started.Wait(3000), "Finalizer test did not start");
        _ = Task.Run(async () => { await Task.Delay(3000); SlowFinalizer.Released.Set(); });
        var closeTime = System.Diagnostics.Stopwatch.StartNew();
        try { ordinary.HideWindow(); }
        finally { SlowFinalizer.Released.Set(); }
        Assert(closeTime.ElapsedMilliseconds < 1000, "Closing waits for unrelated finalizers");
        Console.WriteLine("PASS close releases resources without waiting for an unrelated blocked finalizer");
        Console.WriteLine("PASS ordinary edit/search focus, other windows cover preview, native close allows reopening");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void QueueSlowFinalizer() => GC.KeepAlive(new SlowFinalizer());
    sealed class SlowFinalizer
    {
        public static readonly ManualResetEventSlim Started = new(false), Released = new(false);
        ~SlowFinalizer() { Started.Set(); Released.Wait(5000); }
    }

    sealed class GateEditor : UserControl, IEditable
    {
        public readonly TaskCompletionSource Completion = new();
        public int ReloadCalls;
        public bool IsDirty { get; set; } = true;
        public event Action? DirtyChanged { add { } remove { } }
        public string? WhyNotEditable() => null;
        public void BeginEdit() { }
        public void EndEdit() { }
        public Task SaveToAsync(string path) => Completion.Task;
        public void ReleaseFile() { }
        public Task ReloadAsync(string path) { ReloadCalls++; IsDirty = false; return Task.CompletedTask; }
        public bool HandleEditKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers) => false;
    }
}
