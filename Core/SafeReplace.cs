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
        bool originalGone = false;
        try
        {
            await write(temp);
            if (!File.Exists(temp)) throw new IOException("The new version wasn't written.");

            releaseOriginal?.Invoke();
            var created = File.GetCreationTimeUtc(path);
            await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
            originalGone = true;
            await MoveIntoPlaceAsync(temp, path);
            try { File.SetCreationTimeUtc(path, created); } catch { /* not important */ }
            App.Log($"[SafeReplace] Saved '{path}' (the original is in the Recycle Bin)");
        }
        finally
        {
            // Once the original is in the Recycle Bin, the new version is the only copy of the edits: never delete it
            if (!originalGone)
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The new version takes the original's name. A scanner or indexer may briefly hold the name, so this retries;
    /// if it still can't, the new version is kept under a visible name next to it and the error says where.
    /// </summary>
    private static async Task MoveIntoPlaceAsync(string temp, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                App.Log($"[SafeReplace] Could not put the new version in place: {ex.Message}");
                string aside = Path.Combine(Path.GetDirectoryName(path)!,
                    Path.GetFileNameWithoutExtension(path) + " (Lupik)" + Path.GetExtension(path));
                for (int i = 2; File.Exists(aside); i++)
                    aside = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} (Lupik {i}){Path.GetExtension(path)}");
                try { File.Move(temp, aside); } catch { aside = temp; }
                throw new IOException(Lupik.Localization.Loc.T("edit.keptAside", aside), ex);
            }
        }
    }
}
