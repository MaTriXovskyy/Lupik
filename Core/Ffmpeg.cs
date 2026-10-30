using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lupik.Core;

/// <summary>
/// FFmpeg for trimming and cutting audio / video. Not shipped with Lupik (it's bigger than Lupik itself): the first
/// edit downloads the LGPL build from BtbN's FFmpeg-Builds on GitHub, checks it against the release's SHA-256 list,
/// and keeps only ffmpeg.exe in %LocalAppData%\Lupik\ffmpeg.
/// </summary>
public static class Ffmpeg
{
    private const string Release = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lupik", "ffmpeg");

    /// <summary>Shipped next to Lupik (ffmpeg\ffmpeg.exe); a copy downloaded earlier only if that's missing.</summary>
    public static string ExePath => File.Exists(Bundled) ? Bundled : Path.Combine(Folder, "ffmpeg.exe");

    private static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    private static Task? _warmUp;

    /// <summary>
    /// Starts ffmpeg once in the background (it lists its encoders, needed for saving anyway). The first start of the
    /// big exe is what takes long, while Windows scans it; by the time the edit is saved it's done.
    /// </summary>
    public static void WarmUp()
    {
        if (!IsInstalled || _warmUp != null) return;
        _warmUp = Task.Run(async () =>
        {
            try { await EncodersAsync(); }
            catch (Exception ex) { App.Log($"[Ffmpeg] Warm-up failed: {ex.Message}"); _warmUp = null; }
        });
    }

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>Downloads and verifies FFmpeg. Progress is 0..1 of the download.</summary>
    public static async Task InstallAsync(IProgress<double> progress, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Lupik");

        // The newest release branch (stable), from the checksum list; the master build if none matches
        string checksums = await http.GetStringAsync(Release + "checksums.sha256", token);
        var builds = Regex.Matches(checksums, @"^([0-9a-f]{64})\s+(ffmpeg-n(\d+)\.(\d+)-latest-win64-lgpl-\3\.\4\.zip)\s*$", RegexOptions.Multiline)
            .Select(m => (Hash: m.Groups[1].Value, Name: m.Groups[2].Value, Major: int.Parse(m.Groups[3].Value), Minor: int.Parse(m.Groups[4].Value)))
            .OrderByDescending(b => b.Major).ThenByDescending(b => b.Minor).ToList();
        var master = Regex.Match(checksums, @"^([0-9a-f]{64})\s+(ffmpeg-master-latest-win64-lgpl\.zip)\s*$", RegexOptions.Multiline);
        (string hash, string name) = builds.Count > 0 ? (builds[0].Hash, builds[0].Name)
            : master.Success ? (master.Groups[1].Value, master.Groups[2].Value)
            : throw new InvalidDataException("No FFmpeg build in the release.");
        App.Log($"[Ffmpeg] Downloading {name}");

        Directory.CreateDirectory(Folder);
        string zipPath = Path.Combine(Folder, "download.part");
        try
        {
            using (var response = await http.GetAsync(Release + name, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (total > 0) progress.Report((double)done / total);
                }
            }

            string actual;
            await using (var zipStream = File.OpenRead(zipPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(zipStream, token));
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The FFmpeg download doesn't match its checksum.");

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var exe = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidDataException("ffmpeg.exe is missing from the download.");
                string temp = ExePath + ".part";
                exe.ExtractToFile(temp, overwrite: true);
                File.Move(temp, ExePath, overwrite: true);
            }
            _encoders = null;
            App.Log($"[Ffmpeg] Installed {name}");
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    private static volatile HashSet<string>? _encoders;

    /// <summary>The encoders this ffmpeg.exe has (builds differ: hardware and optional libraries).</summary>
    public static async Task<HashSet<string>> EncodersAsync()
    {
        if (_encoders != null) return _encoders;
        var (_, output) = await RunAsync("-hide_banner -encoders", CancellationToken.None);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(output, @"^\s*[VAS][\w.]{5}\s+(\S+)", RegexOptions.Multiline)) set.Add(m.Groups[1].Value);
        return _encoders = set;
    }

    public sealed record MediaInfo(bool HasVideo, bool HasAudio, int BitrateKbps);

    /// <summary>What's in the file: read from ffmpeg's own description of the input.</summary>
    public static async Task<MediaInfo> ProbeAsync(string path)
    {
        var (_, output) = await RunAsync($"-hide_banner -i {Quote(path)}", CancellationToken.None);
        var streams = Regex.Matches(output, @"Stream #0:\d+.*?: (Video|Audio): (.*)").Select(m => (Kind: m.Groups[1].Value, Detail: m.Groups[2].Value)).ToList();
        // A cover picture in an MP3 / M4A is a "video" stream too: not a real one
        bool video = streams.Any(s => s.Kind == "Video" && !s.Detail.Contains("attached pic"));
        bool audio = streams.Any(s => s.Kind == "Audio");
        var bitrate = Regex.Match(output, @"bitrate: (\d+) kb/s");
        return new MediaInfo(video, audio, bitrate.Success ? int.Parse(bitrate.Groups[1].Value) : 0);
    }

    /// <summary>
    /// Runs ffmpeg. With <paramref name="totalSeconds"/>, "-progress" output is turned into 0..1 progress.
    /// Returns the exit code and the error output (where ffmpeg describes what went wrong).
    /// </summary>
    public static async Task<(int ExitCode, string Log)> RunAsync(string arguments, CancellationToken token, double totalSeconds = 0, IProgress<double>? progress = null)
    {
        var info = new ProcessStartInfo(ExePath, (progress != null ? "-progress pipe:1 -nostats " : "") + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg didn't start.");
        var log = new StringBuilder();
        var errors = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
                lock (log) { log.AppendLine(line); if (log.Length > 200_000) log.Remove(0, 100_000); }
        });
        var outputs = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                if (progress == null) { lock (log) log.AppendLine(line); continue; }
                if (totalSeconds > 0 && line.StartsWith("out_time_us=") &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
                    progress.Report(Math.Clamp(us / 1_000_000.0 / totalSeconds, 0, 1));
            }
        });
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        await Task.WhenAll(errors, outputs);
        lock (log) return (process.ExitCode, log.ToString());
    }

    public static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}
