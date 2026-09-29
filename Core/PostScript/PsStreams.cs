using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lupik.Core.PostScript;

/// <summary>A byte stream PostScript reads from: the program itself, a string, a procedure or a decode filter.</summary>
internal interface IByteSource
{
    /// <summary>Next byte, or -1 at the end.</summary>
    int Read();
    int Peek();
    bool Closed { get; set; }
}

internal abstract class ByteSource : IByteSource
{
    private int _peeked = -2;
    public bool Closed { get; set; }

    protected abstract int ReadCore();

    public int Read()
    {
        if (_peeked != -2) { int b = _peeked; _peeked = -2; return b; }
        return Closed ? -1 : ReadCore();
    }

    public int Peek()
    {
        if (_peeked == -2) _peeked = Closed ? -1 : ReadCore();
        return _peeked;
    }
}

internal sealed class BytesSource : ByteSource
{
    private readonly byte[] _data;
    private readonly int _end;
    public int Position;

    public BytesSource(byte[] data, int start = 0, int end = -1)
    {
        _data = data;
        Position = start;
        _end = end < 0 ? data.Length : end;
    }

    protected override int ReadCore() => Position < _end ? _data[Position++] : -1;
}

/// <summary>Data source procedure: called again every time the previous string runs out; an empty string ends it.</summary>
internal sealed class ProcSource : ByteSource
{
    private readonly PsInterpreter _ps;
    private readonly PsArray _proc;
    private PsString? _chunk;
    private int _pos;
    private bool _done;

    public ProcSource(PsInterpreter ps, PsArray proc) { _ps = ps; _proc = proc; }

    protected override int ReadCore()
    {
        while (!_done && (_chunk == null || _pos >= _chunk.Length))
        {
            _ps.Execute(_proc);
            if (_ps.Pop() is not PsString s || s.Length == 0) { _done = true; break; }
            _chunk = s;
            _pos = 0;
        }
        return _done ? -1 : _chunk![_pos++];
    }
}

internal sealed class AsciiHexDecode : ByteSource
{
    private readonly IByteSource _src;
    private bool _eod;
    public AsciiHexDecode(IByteSource src) => _src = src;

    protected override int ReadCore()
    {
        if (_eod) return -1;
        int hi = NextDigit();
        if (hi < 0) { _eod = true; return -1; }
        int lo = NextDigit();
        if (lo < 0) { _eod = true; return hi << 4; }
        return (hi << 4) | lo;
    }

    private int NextDigit()
    {
        while (true)
        {
            int c = _src.Read();
            if (c < 0 || c == '>') return -1;
            int v = PsLexer.HexValue(c);
            if (v >= 0) return v;
        }
    }
}

internal sealed class Ascii85Decode : ByteSource
{
    private readonly IByteSource _src;
    private readonly byte[] _out = new byte[4];
    private int _outPos, _outLen;
    private bool _eod;

    public Ascii85Decode(IByteSource src)
    {
        _src = src;
        // "<~" may still be in front of the data when it comes from a string
        while (_src.Peek() is ' ' or '\n' or '\r' or '\t') _src.Read();
        if (_src.Peek() == '<') { _src.Read(); if (_src.Peek() == '~') _src.Read(); }
    }

    protected override int ReadCore()
    {
        if (_outPos < _outLen) return _out[_outPos++];
        if (_eod) return -1;

        Span<int> group = stackalloc int[5];
        int n = 0;
        while (n < 5)
        {
            int c = _src.Read();
            if (c < 0 || c == '~') { if (c == '~') _src.Read(); _eod = true; break; }
            if (c == 'z' && n == 0) { _out[0] = _out[1] = _out[2] = _out[3] = 0; _outPos = 0; _outLen = 4; return _out[_outPos++]; }
            if (c < '!' || c > 'u') continue; // whitespace
            group[n++] = c - '!';
        }
        if (n == 0) return -1;

        int produced = n - 1;
        for (int i = n; i < 5; i++) group[i] = 84; // pad with 'u'
        uint value = 0;
        for (int i = 0; i < 5; i++) value = unchecked(value * 85 + (uint)group[i]);
        _out[0] = (byte)(value >> 24); _out[1] = (byte)(value >> 16); _out[2] = (byte)(value >> 8); _out[3] = (byte)value;
        _outLen = n == 5 ? 4 : produced;
        _outPos = 0;

        // Like a buffering filter: an end marker right after this group is consumed now
        if (!_eod)
        {
            while (_src.Peek() is ' ' or '\n' or '\r' or '\t') _src.Read();
            if (_src.Peek() == '~') { _src.Read(); if (_src.Peek() == '>') _src.Read(); _eod = true; }
        }
        return _outLen > 0 ? _out[_outPos++] : -1;
    }
}

internal sealed class RunLengthDecode : ByteSource
{
    private readonly IByteSource _src;
    private int _literal, _repeat, _repeatByte;
    private bool _eod;
    public RunLengthDecode(IByteSource src) => _src = src;

    /// <summary>Real filters buffer ahead, so the EOD marker right after the last run is consumed with it.</summary>
    private void SwallowEod()
    {
        if (_src.Peek() == 128) { _src.Read(); _eod = true; }
    }

    protected override int ReadCore()
    {
        while (true)
        {
            if (_literal > 0) { _literal--; int b = _src.Read(); if (_literal == 0) SwallowEod(); return b; }
            if (_repeat > 0) { _repeat--; if (_repeat == 0) SwallowEod(); return _repeatByte; }
            if (_eod) return -1;
            int len = _src.Read();
            if (len < 0 || len == 128) { _eod = true; return -1; }
            if (len < 128) _literal = len + 1;
            else { _repeat = 257 - len; _repeatByte = _src.Read(); }
        }
    }
}

internal sealed class LzwDecode : ByteSource
{
    private readonly IByteSource _src;
    private readonly bool _earlyChange;
    private readonly List<byte[]> _table = new();
    private byte[]? _prev;
    private byte[] _current = Array.Empty<byte>();
    private int _pos;
    private int _bitBuf, _bitCount, _codeLen = 9;
    private bool _eod;

    public LzwDecode(IByteSource src, bool earlyChange = true)
    {
        _src = src;
        _earlyChange = earlyChange;
        Reset();
    }

    private void Reset()
    {
        _table.Clear();
        for (int i = 0; i < 256; i++) _table.Add(new[] { (byte)i });
        _table.Add(Array.Empty<byte>()); // 256 clear
        _table.Add(Array.Empty<byte>()); // 257 EOD
        _codeLen = 9;
        _prev = null;
    }

    private int NextCode()
    {
        while (_bitCount < _codeLen)
        {
            int b = _src.Read();
            if (b < 0) return -1;
            _bitBuf = (_bitBuf << 8) | b;
            _bitCount += 8;
        }
        int code = (_bitBuf >> (_bitCount - _codeLen)) & ((1 << _codeLen) - 1);
        _bitCount -= _codeLen;
        return code;
    }

    protected override int ReadCore()
    {
        while (_pos >= _current.Length)
        {
            if (_eod) return -1;
            int code = NextCode();
            if (code < 0 || code == 257) { _eod = true; return -1; }
            if (code == 256) { Reset(); continue; }

            byte[] entry;
            if (code < _table.Count) entry = _table[code];
            else if (_prev != null) entry = [.. _prev, _prev[0]];
            else { _eod = true; return -1; }

            if (_prev != null) _table.Add([.. _prev, entry[0]]);
            _prev = entry;

            int limit = _table.Count + (_earlyChange ? 1 : 0);
            if (limit >= 512 && _codeLen < 10) _codeLen = 10;
            if (limit >= 1024 && _codeLen < 11) _codeLen = 11;
            if (limit >= 2048 && _codeLen < 12) _codeLen = 12;

            _current = entry;
            _pos = 0;
        }
        return _current[_pos++];
    }
}

/// <summary>Adapts a byte source to a .NET stream (for the zlib decoder).</summary>
internal sealed class SourceStream : Stream
{
    private readonly IByteSource _src;
    public SourceStream(IByteSource src) => _src = src;
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = 0;
        while (n < count)
        {
            int b = _src.Read();
            if (b < 0) break;
            buffer[offset + n++] = (byte)b;
        }
        return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class StreamSource : ByteSource
{
    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[8192];
    private int _pos, _len;
    public StreamSource(Stream stream) => _stream = stream;

    protected override int ReadCore()
    {
        if (_pos >= _len)
        {
            try { _len = _stream.Read(_buf, 0, _buf.Length); }
            catch (InvalidDataException) { _len = 0; } // truncated / damaged deflate data
            _pos = 0;
            if (_len <= 0) return -1;
        }
        return _buf[_pos++];
    }
}

/// <summary>
/// JPEG data inside the program: read up to the end-of-image marker, decode it with Windows' own JPEG codec,
/// and hand out the raw samples (gray, RGB or CMYK) as the image operator expects them.
/// </summary>
internal sealed class DctDecode : ByteSource
{
    private readonly byte[] _pixels;
    private int _pos;

    public DctDecode(IByteSource src)
    {
        var jpeg = new MemoryStream();
        int prev = -1;
        while (true)
        {
            int b = src.Read();
            if (b < 0) break;
            jpeg.WriteByte((byte)b);
            if (prev == 0xFF && b == 0xD9) break;
            prev = b;
        }
        jpeg.Position = 0;
        try
        {
            var frame = BitmapDecoder.Create(jpeg, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            BitmapSource bmp = frame;
            int comps;
            if (bmp.Format == PixelFormats.Gray8) comps = 1;
            else if (bmp.Format == PixelFormats.Cmyk32) comps = 4;
            else { bmp = new FormatConvertedBitmap(bmp, PixelFormats.Rgb24, null, 0); comps = 3; }
            int stride = bmp.PixelWidth * comps;
            _pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(_pixels, stride, 0);
        }
        catch
        {
            _pixels = Array.Empty<byte>();
        }
    }

    protected override int ReadCore() => _pos < _pixels.Length ? _pixels[_pos++] : -1;
}

/// <summary>SubFileDecode: passes bytes through until an end string (or a byte count) is reached.</summary>
internal sealed class SubFileDecode : ByteSource
{
    private readonly IByteSource _src;
    private readonly byte[] _eod;
    private long _remaining;
    private int _eodCount;
    private readonly Queue<int> _pending = new();

    public SubFileDecode(IByteSource src, long count, byte[] eod, int eodCount)
    {
        _src = src;
        _remaining = count <= 0 ? long.MaxValue : count;
        _eod = eod;
        _eodCount = eodCount;
    }

    protected override int ReadCore()
    {
        if (_eod.Length == 0)
        {
            if (_remaining-- <= 0) return -1;
            return _src.Read();
        }
        if (_pending.Count > 0) return _pending.Dequeue();

        // Match the end string byte by byte; a partial match that fails is passed through
        int matched = 0;
        var buffer = new List<int>();
        while (true)
        {
            int b = _src.Read();
            if (b < 0) { foreach (var x in buffer) _pending.Enqueue(x); return _pending.Count > 0 ? _pending.Dequeue() : -1; }
            if (b == _eod[matched])
            {
                buffer.Add(b);
                if (++matched == _eod.Length)
                {
                    if (_eodCount-- <= 0) return -1;
                    foreach (var x in buffer) _pending.Enqueue(x);
                    return _pending.Dequeue();
                }
                continue;
            }
            if (buffer.Count == 0) return b;
            buffer.Add(b);
            foreach (var x in buffer) _pending.Enqueue(x);
            return _pending.Dequeue();
        }
    }
}

/// <summary>
/// eexec: Type 1 font programs hide their glyph outlines behind this simple cipher (key 55665),
/// either as binary or as hex text. The first 4 decrypted bytes are random padding.
/// </summary>
internal sealed class EexecDecode : ByteSource
{
    private readonly IByteSource _src;
    private readonly bool _hex;
    private ushort _r = 55665;

    public EexecDecode(IByteSource src)
    {
        _src = src;
        while (_src.Peek() is ' ' or '\t' or '\r' or '\n') _src.Read();
        // Hex if the first 4 bytes are all hex digits
        _hex = true;
        var first = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            int b = _src.Read();
            first.Add(b);
            if (PsLexer.HexValue(b) < 0) _hex = false;
        }
        _prefetched = new Queue<int>(first);
        for (int i = 0; i < 4; i++) ReadCore(); // drop the padding
    }

    private readonly Queue<int> _prefetched;

    private int RawByte()
    {
        if (_hex)
        {
            int hi = NextHex(), lo = NextHex();
            return hi < 0 || lo < 0 ? -1 : (hi << 4) | lo;
        }
        return _prefetched.Count > 0 ? _prefetched.Dequeue() : _src.Read();
    }

    private int NextHex()
    {
        while (true)
        {
            int c = _prefetched.Count > 0 ? _prefetched.Dequeue() : _src.Read();
            if (c < 0) return -1;
            int v = PsLexer.HexValue(c);
            if (v >= 0) return v;
        }
    }

    protected override int ReadCore()
    {
        int c = RawByte();
        if (c < 0) return -1;
        int p = c ^ (_r >> 8);
        _r = (ushort)((c + _r) * 52845 + 22719);
        return p;
    }
}

internal static class PsFilters
{
    public static IByteSource Create(string name, IByteSource src, PsDict? parms)
    {
        return name switch
        {
            "ASCIIHexDecode" => new AsciiHexDecode(src),
            "ASCII85Decode" => new Ascii85Decode(src),
            "RunLengthDecode" => new RunLengthDecode(src),
            "LZWDecode" => new LzwDecode(src, parms?.Get("EarlyChange") is not long ec || ec != 0),
            "FlateDecode" => new StreamSource(new ZLibStream(new SourceStream(src), CompressionMode.Decompress)),
            "DCTDecode" => new DctDecode(src),
            "SubFileDecode" => SubFile(src, parms),
            "NullEncode" or "ASCIIHexEncode" or "ASCII85Encode" => src, // writing: irrelevant for rendering
            _ => throw new PsError("undefined", $"filter {name}"),
        };
    }

    private static IByteSource SubFile(IByteSource src, PsDict? parms) =>
        new SubFileDecode(src, 0, (parms?.Get("EODString") as PsString)?.Span.ToArray() ?? Array.Empty<byte>(),
            parms?.Get("EODCount") is long c ? (int)c : 0);
}
