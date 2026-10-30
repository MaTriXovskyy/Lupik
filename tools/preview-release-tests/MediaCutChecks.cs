using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Lupik.Core;

/// <summary>Trimming and cutting audio / video through Lupik's own code: the bundled FFmpeg, the right length, quickly.</summary>
static class MediaCutChecks
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run(string samples)
    {
        Assert(Ffmpeg.IsInstalled && Ffmpeg.ExePath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "FFmpeg isn't bundled next to Lupik: " + Ffmpeg.ExePath);
        string folder = Path.Combine(Path.GetTempPath(), "Lupik-mediacut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // A 4-minute song: keep 0–100 s and 140–240 s (40 s cut from the middle)
            string song = Path.Combine(folder, "song.mp3");
            var (exit, _) = await Ffmpeg.RunAsync($"-hide_banner -y -f lavfi -i \"sine=frequency=440:duration=240\" -ac 2 -b:a 192k {Ffmpeg.Quote(song)}", CancellationToken.None);
            Assert(exit == 0, "Could not make the test song");
            await Check(song, Path.Combine(folder, "song-cut.tmp"), new[] { (0.0, 100.0), (140.0, 240.0) }, 200, maxSeconds: 10);

            // The sample video, trimmed at both ends
            string video = Path.Combine(folder, "video.mp4");
            File.Copy(Path.Combine(samples, "sample.mp4"), video);
            var info = await Ffmpeg.ProbeAsync(video);
            Assert(info.HasVideo, "sample.mp4 has no video stream");
            double length = await Duration(video);
            await Check(video, Path.Combine(folder, "video-cut.tmp"), new[] { (length * 0.2, length * 0.8) }, length * 0.6, maxSeconds: 30);
            Console.WriteLine("PASS media trim and cut (bundled FFmpeg, exact length, fast)");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    static async Task Check(string source, string target, (double, double)[] keep, double expected, double maxSeconds)
    {
        var watch = Stopwatch.StartNew();
        await MediaCut.ExportAsync(source, target, keep, new Progress<double>(), CancellationToken.None);
        double took = watch.Elapsed.TotalSeconds;
        // The muxer writes to a .tmp name in the real save, so the probe reads it under its own extension
        string renamed = Path.ChangeExtension(target, Path.GetExtension(source));
        File.Move(target, renamed);
        double length = await Duration(renamed);
        Console.WriteLine($"MEDIA {Path.GetFileName(source)}: {length:0.00} s (expected {expected:0.00}) in {took:0.0} s");
        Assert(Math.Abs(length - expected) < 0.25, $"{Path.GetFileName(source)}: length {length:0.00} s, expected {expected:0.00} s");
        Assert(took < maxSeconds, $"{Path.GetFileName(source)}: export took {took:0.0} s");
    }

    static async Task<double> Duration(string path)
    {
        var (_, log) = await Ffmpeg.RunAsync($"-hide_banner -i {Ffmpeg.Quote(path)}", CancellationToken.None);
        var m = Regex.Match(log, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success) throw new Exception("No duration for " + path);
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
               + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
