using System.IO;

namespace Lupik.Core;

/// <summary>Shared by a cached decode and its viewer, including decodes started by neighbor preloading.</summary>
internal sealed class DecodeProgress
{
    private double _value = double.NaN;
    public double Value => Volatile.Read(ref _value);

    // Decoder phases may restart their counters; the visible bar only advances.
    public void Report(double value)
    {
        double previous = Value;
        if (double.IsNaN(previous) || value > previous) Volatile.Write(ref _value, Math.Clamp(value, 0, 0.98));
    }
}

/// <summary>
/// Counts unique file bytes read by a seekable decoder. TIFF often reads metadata near EOF first,
/// so stream position alone would wrongly show a nearly complete image before decoding its pixels.
/// Reading is only one stage: leave room for pixel conversion, orientation and the UI swap.
/// </summary>
internal sealed class DecodeReadStream(string path, DecodeProgress? progress) : Stream
{
    private readonly FileStream _source = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    private readonly List<(long Start, long End)> _ranges = new();
    private long _covered;

    private void ReadRange(long start, int count)
    {
        if (progress == null || count <= 0 || Length == 0) return;
        long end = start + count;
        int i = 0;
        while (i < _ranges.Count && _ranges[i].End < start) i++;
        int first = i;
        while (i < _ranges.Count && _ranges[i].Start <= end)
        {
            var range = _ranges[i++];
            start = Math.Min(start, range.Start);
            end = Math.Max(end, range.End);
            _covered -= range.End - range.Start;
        }
        if (i > first) _ranges.RemoveRange(first, i - first);
        _ranges.Insert(first, (start, end));
        _covered += end - start;
        progress.Report(0.8 * _covered / Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long start = Position;
        int read = _source.Read(buffer, offset, count);
        ReadRange(start, read);
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        long start = Position;
        int read = _source.Read(buffer);
        ReadRange(start, read);
        return read;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position { get => _source.Position; set => _source.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _source.Dispose(); base.Dispose(disposing); }
}
