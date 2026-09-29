using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace QuickPeek.Core;

/// <summary>
/// Tracks half-written outputs (the "*.part" zip / extraction folder) of jobs still running.
/// On exit the jobs are cancelled and their leftovers deleted; the list is also kept on disk,
/// so leftovers of a crash / killed process are removed on the next start.
/// </summary>
public static class PendingCleanup
{
    private static readonly string ListPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPeek", "pending.txt");

    private static readonly Dictionary<string, CancellationTokenSource> Jobs = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string partPath, CancellationTokenSource cts)
    {
        lock (Jobs) { Jobs[partPath] = cts; Save(); }
    }

    public static void Unregister(string partPath)
    {
        lock (Jobs) { if (Jobs.Remove(partPath)) Save(); }
    }

    /// <summary>App exit: cancel running jobs, then delete what they left (retrying while their workers let go of the files).</summary>
    public static void CancelAllAndClean()
    {
        string[] paths;
        lock (Jobs)
        {
            foreach (var cts in Jobs.Values) { try { cts.Cancel(); } catch { } }
            paths = Jobs.Keys.ToArray();
        }
        if (paths.Length == 0) return;

        var deadline = Environment.TickCount64 + 3000;
        var left = paths.ToList();
        while (left.Count > 0 && Environment.TickCount64 < deadline)
        {
            left.RemoveAll(TryDelete);
            if (left.Count > 0) Thread.Sleep(100);
        }
        App.Log($"[PendingCleanup] Exit: removed {paths.Length - left.Count}/{paths.Length} unfinished outputs");
        lock (Jobs) { Jobs.Clear(); foreach (var p in left) Jobs[p] = new CancellationTokenSource(); Save(); } // retried next start
    }

    /// <summary>Start-up: remove leftovers of a previous run that didn't exit cleanly.</summary>
    public static void CleanLeftovers()
    {
        try
        {
            if (!File.Exists(ListPath)) return;
            var paths = File.ReadAllLines(ListPath).Where(p => p.Length > 0).ToArray();
            var left = paths.Where(p => !TryDelete(p)).ToArray();
            if (paths.Length > 0) App.Log($"[PendingCleanup] Start: removed {paths.Length - left.Length}/{paths.Length} leftovers of the previous run");
            if (left.Length == 0) File.Delete(ListPath); else File.WriteAllLines(ListPath, left);
        }
        catch (Exception ex) { App.Log($"[PendingCleanup] Start cleanup failed: {ex.Message}"); }
    }

    /// <summary>Only ever deletes "*.part" paths, so a corrupted list can't remove anything else.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
            return true;
        }
        catch { return false; }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ListPath)!);
            if (Jobs.Count == 0) File.Delete(ListPath);
            else File.WriteAllLines(ListPath, Jobs.Keys);
        }
        catch { /* best effort */ }
    }
}
