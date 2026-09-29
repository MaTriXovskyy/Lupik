using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Lupik.Core;

/// <summary>
/// Unpacks an archive next to itself, as fast as the format allows:
/// ZIP entries are independent, so they are inflated on all CPU cores at once (one ZipArchive per worker);
/// other formats (RAR/7z/TAR/GZ…) go through SharpCompress in a single streaming pass,
/// which is also the only correct way for solid archives.
/// </summary>
public static class ArchiveExtractor
{
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// Names in ZIPs without the UTF-8 flag (Windows' own tar/Explorer, old packers) are in the OEM code page
    /// (852 on Polish Windows) — decoding them as UTF-8/437 mangles "ąęł".
    /// </summary>
    public static readonly System.Text.Encoding LegacyNameEncoding = CreateLegacyEncoding();

    private static System.Text.Encoding CreateLegacyEncoding()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        try { return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return System.Text.Encoding.GetEncoding(437); }
    }

    public static SharpCompress.Readers.ReaderOptions ReaderOptions() =>
        new() { ArchiveEncoding = new ArchiveEncoding { Default = LegacyNameEncoding } };

    /// <summary>
    /// Extracts into a temporary "*.part" folder, then moves it into place:
    /// a single top-level folder in the archive lands directly next to it (like macOS), otherwise the
    /// content goes into a folder named after the archive. Returns the final folder.
    /// </summary>
    public static async Task<string> ExtractAsync(string archivePath, IProgress<(long done, long total, int files)> progress, CancellationToken token, Action<string>? tempCreated = null)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        string stem = ArchiveStem(archivePath);
        string temp = Path.Combine(parent, stem + ".part");
        for (int i = 2; Directory.Exists(temp) || File.Exists(temp); i++)
            temp = Path.Combine(parent, $"{stem} ({i}).part"); // keeps the ".part" suffix PendingCleanup relies on
        Directory.CreateDirectory(temp);
        tempCreated?.Invoke(temp);

        try
        {
            bool done = false;
            if (IsZip(archivePath))
            {
                try
                {
                    await Task.Run(() => ExtractZipParallel(archivePath, temp, progress, token), token);
                    done = true;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // Methods the BCL can't read (LZMA, bzip2, encrypted…): start over with SharpCompress
                    App.Log($"[ArchiveExtractor] BCL zip failed ({ex.Message}), falling back to SharpCompress");
                    Directory.Delete(temp, true);
                    Directory.CreateDirectory(temp);
                }
            }
            if (!done)
                await Task.Run(() => ExtractSequential(archivePath, temp, progress, token), token);

            return MoveIntoPlace(temp, parent, stem);
        }
        catch
        {
            try { Directory.Delete(temp, true); } catch { /* best effort */ }
            throw;
        }
    }

    // ---------- ZIP: parallel ----------

    private static void ExtractZipParallel(string archivePath, string dest, IProgress<(long, long, int)> progress, CancellationToken token)
    {
        List<(int index, long size)> work;
        long total;
        using (var probe = ZipFile.Open(archivePath, ZipArchiveMode.Read, LegacyNameEncoding))
        {
            if (probe.Entries.Any(e => e.IsEncrypted))
                throw new NotSupportedException("encrypted entries");
            // Biggest first: one huge file can't be split, so start it before the small ones
            work = probe.Entries.Select((e, i) => (i, e.Length)).OrderByDescending(w => w.Length).ToList();
            total = work.Sum(w => w.size);
        }

        var reporter = new Reporter(progress, total);
        int next = -1;
        int workers = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, work.Count));
        var directories = new DirectoryCache();

        try
        {
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, _ => ExtractWorker());
        }
        catch (AggregateException ae)
        {
            // Surface the real error (so the caller's fallback / cancel handling sees it)
            var inner = ae.Flatten().InnerExceptions;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(
                inner.FirstOrDefault(e => e is not OperationCanceledException) ?? inner[0]);
        }
        reporter.Flush();

        void ExtractWorker()
        {
            // Every worker has its own file handle + ZipArchive: entry streams can't be shared between threads
            using var archive = new ZipArchive(new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), ZipArchiveMode.Read, false, LegacyNameEncoding);
            var entries = archive.Entries;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < work.Count)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = entries[work[i].index];
                    string? target = SafeTarget(dest, entry.FullName);
                    if (target == null) continue;

                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        directories.Ensure(target);
                        continue;
                    }
                    directories.Ensure(Path.GetDirectoryName(target)!);
                    using (var input = entry.Open())
                        WriteFile(target, input, entry.Length, buffer, reporter, token);
                    TrySetTime(target, entry.LastWriteTime.LocalDateTime);
                    reporter.FileDone();
                }
            }
            catch
            {
                Volatile.Write(ref next, int.MaxValue / 2); // stop the other workers too
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    // ---------- Everything else: one streaming pass ----------

    private static void ExtractSequential(string archivePath, string dest, IProgress<(long, long, int)> progress, CancellationToken token)
    {
        // Progress = how far we've read through the archive file (works for .tar.gz, where sizes aren't known upfront)
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        var reporter = new Reporter(progress, file.Length, () => file.Position);
        var directories = new DirectoryCache();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        // Streaming reader handles ZIP/RAR/TAR(.gz/.bz2/.xz)/GZ; 7z needs random access → archive API
        IArchive? archive = null;
        SharpCompress.Readers.IReader reader;
        try
        {
            reader = SharpCompress.Readers.ReaderFactory.OpenReader(file, ReaderOptions() with { LeaveStreamOpen = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            file.Position = 0;
            archive = ArchiveFactory.OpenArchive(archivePath, ReaderOptions());
            reporter = new Reporter(progress, archive.Entries.Where(e => !e.IsDirectory).Sum(e => Math.Max(0, e.Size)));
            reader = archive.ExtractAllEntries();
        }

        try
        {
            while (reader.MoveToNextEntry())
            {
                token.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                string key = entry.Key ?? ArchiveStem(archivePath); // bare .gz has no stored name
                string? target = SafeTarget(dest, key);
                if (target == null) continue;
                if (entry.IsEncrypted) throw new NotSupportedException(Lupik.Localization.Loc.T("archive.passwordProtected"));

                if (entry.IsDirectory)
                {
                    directories.Ensure(target);
                    continue;
                }
                directories.Ensure(Path.GetDirectoryName(target)!);
                using (var input = reader.OpenEntryStream())
                    WriteFile(target, input, entry.Size, buffer, reporter, token);
                if (entry.LastModifiedTime is DateTime time) TrySetTime(target, time);
                reporter.FileDone();
            }
        }
        finally
        {
            reader.Dispose();
            archive?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
        reporter.Flush();
    }

    // ---------- Helpers ----------

    private static void WriteFile(string path, Stream input, long size, byte[] buffer, Reporter reporter, CancellationToken token)
    {
        // Unbuffered FileStream + our 1 MB buffer; preallocating avoids fragmentation of big files
        using var output = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
            PreallocationSize = size > 0 ? size : 0,
        });
        int read;
        while ((read = input.Read(buffer, 0, BufferSize)) > 0)
        {
            output.Write(buffer, 0, read);
            reporter.Add(read);
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Entry name → path inside <paramref name="dest"/>; null for names that would escape it ("zip slip").</summary>
    private static string? SafeTarget(string dest, string entryName)
    {
        var parts = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".")
            .Select(Sanitize)
            .ToArray();
        if (parts.Length == 0 || parts.Contains("..")) return null;

        string full = Path.GetFullPath(Path.Combine(dest, Path.Combine(parts)));
        string root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>Names from Mac/Linux archives may hold ':' '?' etc. and trailing dots/spaces Windows rejects.</summary>
    private static string Sanitize(string name)
    {
        if (name == "..") return name;
        var chars = name.Select(c => Array.IndexOf(InvalidNameChars, c) >= 0 ? '_' : c).ToArray();
        string clean = new string(chars).TrimEnd('.', ' ');
        return clean.Length == 0 ? "_" : clean;
    }

    private static void TrySetTime(string path, DateTime time)
    {
        try { if (time.Year > 1980) File.SetLastWriteTime(path, time); } catch { /* cosmetic */ }
    }

    /// <summary>One archive-wide top folder → move it next to the archive; otherwise rename the temp folder after the archive.</summary>
    private static string MoveIntoPlace(string temp, string parent, string stem)
    {
        var dirs = Directory.GetDirectories(temp);
        bool singleFolder = dirs.Length == 1 && Directory.GetFiles(temp).Length == 0;
        if (singleFolder)
        {
            string target = UniquePath(Path.Combine(parent, Path.GetFileName(dirs[0])));
            Directory.Move(dirs[0], target);
            Directory.Delete(temp, false);
            return target;
        }
        string final = UniquePath(Path.Combine(parent, stem));
        Directory.Move(temp, final);
        return final;
    }

    /// <summary>"Name", or "Name (2)", "Name (3)"… if a file or folder already has that name.</summary>
    private static string UniquePath(string path)
    {
        string candidate = path;
        for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = $"{path} ({i})";
        return candidate;
    }

    /// <summary>"photos.tar.gz" → "photos".</summary>
    private static string ArchiveStem(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 ? "archiwum" : name;
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == 'P' && sig[1] == 'K' && sig[2] is 3 or 5 or 7;
        }
        catch { return false; }
    }

    /// <summary>Creates each folder once, even when many workers need it at the same time.</summary>
    private sealed class DirectoryCache
    {
        private readonly HashSet<string> _made = new(StringComparer.OrdinalIgnoreCase);

        public void Ensure(string dir)
        {
            lock (_made) { if (_made.Contains(dir)) return; }
            Directory.CreateDirectory(dir);
            lock (_made) _made.Add(dir);
        }
    }

    /// <summary>Thread-safe byte/file counters, reported at most every 100 ms.</summary>
    private sealed class Reporter(IProgress<(long, long, int)> progress, long total, Func<long>? position = null)
    {
        private long _done, _last;
        private int _files;

        public void Add(long bytes)
        {
            Interlocked.Add(ref _done, bytes);
            long now = Environment.TickCount64, last = Interlocked.Read(ref _last);
            if (now - last >= 100 && Interlocked.CompareExchange(ref _last, now, last) == last)
                progress.Report((position?.Invoke() ?? Interlocked.Read(ref _done), total, Volatile.Read(ref _files)));
        }

        public void FileDone() => Interlocked.Increment(ref _files);

        public void Flush() => progress.Report((total, total, _files));
    }
}
