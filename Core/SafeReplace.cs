using System;
using System.IO;
using System.Threading.Tasks;

namespace Lupik.Core;

/// <summary>
/// Saving an edit over the original: the new version is written next to it first, then the original goes to the
/// Recycle Bin (so it can be restored), and the new file takes its name. If anything fails, the original stays.
/// </summary>
public static class SafeReplace
{
    /// <param name="write">Writes the new content to the path it's given.</param>
    /// <param name="releaseOriginal">Closes anything still holding the original open (called before it's moved).</param>
    public static async Task ReplaceAsync(string path, Func<string, Task> write, Action? releaseOriginal = null)
    {
        string dir = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(dir, "." + Path.GetFileName(path) + ".lupik-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await write(temp);
            if (!File.Exists(temp)) throw new IOException("The new version wasn't written.");

            releaseOriginal?.Invoke();
            var created = File.GetCreationTimeUtc(path);
            await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
            File.Move(temp, path);
            try { File.SetCreationTimeUtc(path, created); } catch { /* not important */ }
            App.Log($"[SafeReplace] Saved '{path}' (the original is in the Recycle Bin)");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
