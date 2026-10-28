using System.IO;
using System.Reflection;
using System.Windows;
using Lupik;

static class FileDialogChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Type Service = typeof(MainWindow).Assembly.GetType("Lupik.Core.FileDialogService")!;
    static object? Call(string method, params object?[] args) => Service.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(null, args);
    static object Get(object value, string field) => value.GetType().GetField(field, Private)!.GetValue(value)!;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static string? Resolve(string? folder, string name, params string[] typed) => (string?)Call("ResolveItem", folder, name, typed);
    static object Selection(IntPtr source, params string[] paths) => Activator.CreateInstance(
        typeof(MainWindow).Assembly.GetType("Lupik.Core.DialogSelection")!, source, paths)!;
    static void Preview(MainWindow window, object selection) => window.GetType().GetMethod("ShowDialogSelection", Private)!.Invoke(window, new[] { selection });

    public static async Task Run(string samples)
    {
        var hookType = typeof(MainWindow).Assembly.GetType("Lupik.Core.KeyboardHook")!;
        var press = hookType.GetMethod("HandlePreviewPress", BindingFlags.Static | BindingFlags.NonPublic)!;
        int presses = 0;
        Func<bool> preview = () => { presses++; return true; };
        bool Key(bool down, Func<bool>? action = null) => (bool)press.Invoke(null, new object[] { (uint)32, down, action ?? preview })!;
        Assert(Key(true) && Key(true) && Key(true) && presses == 1, "Held preview key reopened/closed repeatedly");
        Key(false);
        Assert(Key(true) && presses == 2, "First press after releasing the key failed to close the preview");
        Key(false);
        Assert(!Key(true, () => false) && !Key(true) && presses == 2, "Text-field key was swallowed or retriggered by a repeat");
        Key(false);
        Console.WriteLine("PASS physical preview-key press/release, held-key suppression and untouched text-field repeats");
        string folder = Path.Combine(Path.GetTempPath(), "Lupik-file-dialog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string photo = Path.Combine(folder, "Łóżko kremowe.jpg");
        File.Copy(Path.Combine(samples, "sample.jpg"), photo);
        Assert(Resolve(folder, "Łóżko kremowe.jpg") == photo, "Visible extension/unicode/spaces were not resolved");
        Assert(Resolve(folder, "Łóżko kremowe") == photo, "Hidden extension was not resolved");
        Assert(Resolve(null, "Łóżko kremowe", photo) == photo, "Absolute filename from a virtual view was not resolved");
        string alternate = Path.Combine(folder, "Łóżko kremowe.png");
        File.Copy(Path.Combine(samples, "sample.png"), alternate);
        Assert(Resolve(folder, "Łóżko kremowe") == null, "Ambiguous visible name picked the wrong file");
        Assert(Resolve(folder, "Łóżko kremowe", "Łóżko kremowe.jpg") == photo, "Filename field did not disambiguate hidden extensions");
        Assert(Resolve(folder, "Łóżko kremowe", "Łóżko kremowe.jpg", "Łóżko kremowe.png") == null,
            "Ambiguous multiselection picked an unrelated file");
        Assert(Resolve(folder, "missing", "Łóżko kremowe.jpg") == null, "Stale filename text replaced the actual selection");
        string other = Path.Combine(folder, "other"); Directory.CreateDirectory(other);
        string otherPhoto = Path.Combine(other, "Łóżko kremowe.jpg"); File.Copy(photo, otherPhoto);
        Assert(Resolve(folder, "Łóżko kremowe.jpg", otherPhoto) == photo, "Filename text from another folder overrode the visible selection");
        Assert(Resolve(folder, "does-not-exist.jpg") == null, "Nonexistent Save-as filename was previewed");
        string subfolder = Path.Combine(folder, "Folder do wyboru"); Directory.CreateDirectory(subfolder);
        Assert(Resolve(folder, "Folder do wyboru") == subfolder, "Selected folder was not resolved");
        Console.WriteLine("PASS dialog path resolution: Unicode, spaces, hidden extensions, folders, ambiguity and stale filename rejection");

        var source = new Window { ShowActivated = false, Width = 160, Height = 100 };
        source.Show();
        var handle = new System.Windows.Interop.WindowInteropHelper(source).Handle;
        Assert(!(bool)Call("CanPreview", handle)!, "Non-file window captured the preview key");
        Assert(((IReadOnlyList<string>)Call("ReadSelection", handle)!).Count == 0, "Non-dialog read a background Explorer selection");
        var window = new MainWindow();
        Preview(window, Selection(handle, photo));
        for (int i = 0; i < 100 && !window.IsVisible; i++) await Task.Delay(20);
        Assert(window.IsVisible && (string)Get(window, "_currentFilePath") == photo && window.SourceWindow == handle,
            "Dialog preview failed to show the exact selected file or remember its source");
        Preview(window, Selection(handle, alternate));
        for (int i = 0; i < 100 && (string)Get(window, "_currentFilePath") != alternate; i++) await Task.Delay(20);
        Assert(window.IsVisible && (string)Get(window, "_currentFilePath") == alternate, "New dialog selection did not replace the preview");
        Preview(window, Selection(handle, alternate));
        Assert(!window.IsVisible, "Second preview key did not close the unchanged selection");
        Preview(window, Selection(handle, Path.Combine(folder, "missing.jpg")));
        Assert(!window.IsVisible, "Missing dialog selection opened an unrelated preview");
        window.Close(); source.Close();
        Console.WriteLine("PASS exact dialog selection routing, source tracking, file switch, same-selection close and no unrelated fallback");
    }
}
