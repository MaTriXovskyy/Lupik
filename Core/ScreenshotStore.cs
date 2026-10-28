using System.IO;
using System.Windows.Media.Imaging;

namespace Lupik.Core;

/// <summary>Private temporary PNGs let captures use all existing image actions, including pinning.</summary>
internal static class ScreenshotStore
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Lupik", "Screenshots");
    private static readonly string Session = Path.Combine(Root, Guid.NewGuid().ToString("N"));
    private static int _sequence;

    internal static Task<string> SaveAsync(BitmapSource bitmap) => Task.Run(() =>
    {
        Directory.CreateDirectory(Session);
        string path = Path.Combine(Session, $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Interlocked.Increment(ref _sequence):D3}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            return path;
        }
        catch { try { File.Delete(path); } catch { } throw; }
    });

    internal static void CleanOldSessions()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (string folder in Directory.EnumerateDirectories(Root))
                if (folder != Session && Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) Clean(folder);
        }
        catch (Exception ex) { App.Log($"[Screenshot] Cache cleanup: {ex.Message}"); }
    }

    internal static void CleanCurrentSession() => Clean(Session);

    private static void Clean(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
            // Only our PNGs, never recurse into a directory or delete unknown files.
            foreach (string path in Directory.EnumerateFiles(folder, "*.png")) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) { App.Log($"[Screenshot] Cache cleanup: {ex.Message}"); }
    }
}
