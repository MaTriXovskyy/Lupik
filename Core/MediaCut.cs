using System.Globalization;
using System.IO;
using System.Text;

namespace Lupik.Core;

/// <summary>
/// Trimming and cutting audio / video with FFmpeg: the kept parts are joined into a new file of the same type.
/// Everything is encoded again, so every cut lands exactly where it was set (copying the stream would snap cuts
/// to the nearest keyframe, seconds away).
/// </summary>
public static class MediaCut
{
    private sealed record Format(string Muxer, string[] Video, string[] Audio);

    // Per file type: the container, then the encoders to use, best first (builds differ in what they have)
    private static readonly Dictionary<string, Format> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = new("mp4", H264, Aac),
        [".m4v"] = new("mp4", H264, Aac),
        [".mov"] = new("mov", H264, Aac),
        [".mkv"] = new("matroska", H264, Aac),
        [".3gp"] = new("3gp", H264, Aac),
        [".webm"] = new("webm", new[] { "libvpx-vp9", "libvpx" }, new[] { "libopus", "libvorbis" }),
        [".avi"] = new("avi", new[] { "mpeg4" }, new[] { "libmp3lame", "mp3_mf", "ac3" }),
        [".wmv"] = new("asf", new[] { "wmv2" }, new[] { "wmav2" }),
        [".mp3"] = new("mp3", Array.Empty<string>(), new[] { "libmp3lame", "mp3_mf" }),
        [".wav"] = new("wav", Array.Empty<string>(), new[] { "pcm_s16le" }),
        [".m4a"] = new("ipod", Array.Empty<string>(), Aac),
        [".aac"] = new("adts", Array.Empty<string>(), Aac),
        [".flac"] = new("flac", Array.Empty<string>(), new[] { "flac" }),
        [".ogg"] = new("ogg", Array.Empty<string>(), new[] { "libvorbis", "libopus" }),
        [".opus"] = new("ogg", Array.Empty<string>(), new[] { "libopus", "opus" }),
        [".wma"] = new("asf", Array.Empty<string>(), new[] { "wmav2" }),
    };

    // H.264 without GPL code: Windows' own encoder (Media Foundation) first, then Cisco's OpenH264
    private static string[] H264 => new[] { "h264_mf", "libopenh264", "mpeg4" };
    private static string[] Aac => new[] { "aac", "aac_mf" };

    public static bool Supports(string path) => Formats.ContainsKey(Path.GetExtension(path));

    /// <summary>Writes <paramref name="source"/> with only the <paramref name="keep"/> parts (seconds) to <paramref name="target"/>.</summary>
    public static async Task ExportAsync(string source, string target, IReadOnlyList<(double Start, double End)> keep,
        IProgress<double> progress, CancellationToken token)
    {
        if (!Formats.TryGetValue(Path.GetExtension(source), out var format)) throw new NotSupportedException(Path.GetExtension(source));
        if (keep.Count == 0) throw new ArgumentException("Nothing left to keep.");
        var info = await Ffmpeg.ProbeAsync(source);
        var encoders = await Ffmpeg.EncodersAsync();
        bool video = info.HasVideo && format.Video.Length > 0, audio = info.HasAudio;
        string? videoEncoder = video ? format.Video.FirstOrDefault(encoders.Contains) ?? throw new NotSupportedException("No video encoder for " + format.Muxer) : null;
        string? audioEncoder = audio ? format.Audio.FirstOrDefault(encoders.Contains) ?? throw new NotSupportedException("No audio encoder for " + format.Muxer) : null;

        static string S(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);
        var graph = new StringBuilder();
        var joined = new StringBuilder();
        for (int i = 0; i < keep.Count; i++)
        {
            var (start, end) = keep[i];
            if (video) graph.Append($"[0:v:0]trim=start={S(start)}:end={S(end)},setpts=PTS-STARTPTS[v{i}];");
            if (audio) graph.Append($"[0:a:0]atrim=start={S(start)}:end={S(end)},asetpts=PTS-STARTPTS[a{i}];");
            if (video) joined.Append($"[v{i}]");
            if (audio) joined.Append($"[a{i}]");
        }
        graph.Append(joined).Append($"concat=n={keep.Count}:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}");
        if (video) graph.Append("[v]");
        if (audio) graph.Append("[a]");

        var args = new StringBuilder($"-hide_banner -y -i {Ffmpeg.Quote(source)} -filter_complex \"{graph}\"");
        if (video)
        {
            // Close to the original's quality: its bitrate, but never starved
            int kbps = Math.Max(2500, (int)(info.BitrateKbps * 1.1));
            args.Append($" -map \"[v]\" -c:v {videoEncoder} -b:v {kbps}k");
            if (videoEncoder is "h264_mf" or "libopenh264") args.Append(" -pix_fmt yuv420p");
            if (videoEncoder is "libvpx-vp9" or "libvpx") args.Append(" -deadline good -cpu-used 4");
        }
        if (audio)
        {
            args.Append($" -map \"[a]\" -c:a {audioEncoder}");
            if (audioEncoder is not ("pcm_s16le" or "flac")) args.Append(" -b:a 192k");
        }
        args.Append($" -map_metadata 0 -f {format.Muxer} {Ffmpeg.Quote(target)}");

        double total = keep.Sum(k => k.End - k.Start);
        var (exit, log) = await Ffmpeg.RunAsync(args.ToString(), token, total, progress);
        if (exit != 0 || !File.Exists(target))
        {
            string last = string.Join("\n", log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)).Trim();
            App.Log($"[MediaCut] ffmpeg failed ({exit}): {args}\n{log}");
            throw new IOException(last.Length > 0 ? last : $"ffmpeg exit code {exit}");
        }
        App.Log($"[MediaCut] '{source}' -> '{target}' ({keep.Count} parts, {total:0.0} s, {videoEncoder ?? "-"} / {audioEncoder ?? "-"})");
    }
}
