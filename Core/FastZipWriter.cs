using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Lupik.Core;

/// <summary>
/// ZIP writer that compresses each file on all CPU cores at once (the "pigz" technique):
/// the file is cut into 1 MB chunks, each chunk is deflated on its own thread and ended with a
/// sync flush, so the chunks concatenate into one valid Deflate stream that any unzip tool reads.
/// Files that don't shrink (JPG, video, zips…) are stored as-is. Supports ZIP64 (&gt;4 GB) and UTF-8 names.
/// </summary>
public sealed class FastZipWriter : IDisposable
{
    private const int ChunkSize = 1 << 20;
    private const double StoreIfRatioAbove = 0.97; // compressed to >97%: not worth it, store instead

    private readonly Stream _out;
    private readonly CompressionLevel _level;
    private readonly int _parallelism = Math.Max(1, Environment.ProcessorCount);
    private readonly List<CentralEntry> _entries = new();

    private sealed record CentralEntry(byte[] Name, ushort Method, uint Crc, long Compressed, long Uncompressed,
        long HeaderOffset, uint DosTime, bool IsDirectory);

    public FastZipWriter(Stream output, CompressionLevel level = CompressionLevel.Fastest)
    {
        if (!output.CanSeek) throw new ArgumentException("Output must be seekable (sizes are patched in afterwards).");
        _out = output;
        _level = level;
    }

    public void AddDirectory(string entryName, DateTime modified)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName.TrimEnd('/') + "/");
        long offset = _out.Position;
        WriteLocalHeader(name, 0, 0, 0, 0, ToDos(modified), zip64: false);
        _entries.Add(new CentralEntry(name, 0, 0, 0, 0, offset, ToDos(modified), true));
    }

    /// <param name="progress">Called with the number of source bytes processed so far (from worker threads).</param>
    public async Task AddFileAsync(string entryName, string sourcePath, Action<long>? progress, CancellationToken token)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        var info = new FileInfo(sourcePath);
        long length = info.Length;
        uint dosTime = ToDos(info.LastWriteTime);
        bool zip64 = length >= 0xFFFF0000L; // decided up front from the known size

        long headerOffset = _out.Position;
        WriteLocalHeader(name, 8, 0, 0, 0, dosTime, zip64);
        long dataStart = _out.Position;

        var crc = new Crc32();
        long compressed = 0, processed = 0;

        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
        {
            // Chunks are compressed in parallel but written strictly in order
            var inFlight = new Queue<Task<byte[]>>();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var chunk = new byte[ChunkSize];
                int read = await input.ReadAtLeastAsync(chunk, ChunkSize, throwOnEndOfStream: false, token);
                if (read == 0) break;
                crc.Append(chunk.AsSpan(0, read));
                int n = read;
                inFlight.Enqueue(Task.Run(() => DeflateChunk(chunk, n), token));
                processed += read;

                while (inFlight.Count >= _parallelism * 2)
                    compressed += await WriteNextAsync(inFlight, token);
                progress?.Invoke(processed);
            }
            while (inFlight.Count > 0) compressed += await WriteNextAsync(inFlight, token);
        }

        // Final empty block (BFINAL=1, fixed Huffman, end-of-block) closes the concatenated stream
        _out.Write(stackalloc byte[] { 0x03, 0x00 });
        compressed += 2;
        ushort method = 8;

        if (length > 0 && compressed > length * StoreIfRatioAbove)
        {
            // Didn't shrink: rewrite as stored (the file is in the OS cache now, so re-reading is cheap)
            _out.Position = dataStart;
            await using var raw = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            await raw.CopyToAsync(_out, 1 << 20, token);
            _out.SetLength(_out.Position); // drop the tail of the longer deflate data, if any
            compressed = length;
            method = 0;
        }
        else if (length == 0)
        {
            _out.Position = dataStart;
            _out.SetLength(dataStart);
            compressed = 0;
            method = 0;
        }

        uint crcValue = crc.GetCurrentHashAsUInt32();
        long end = _out.Position;
        _out.Position = headerOffset;
        WriteLocalHeader(name, method, crcValue, compressed, length, dosTime, zip64);
        _out.Position = end;

        _entries.Add(new CentralEntry(name, method, crcValue, compressed, length, headerOffset, dosTime, false));
    }

    private async Task<long> WriteNextAsync(Queue<Task<byte[]>> inFlight, CancellationToken token)
    {
        byte[] data = await inFlight.Dequeue();
        await _out.WriteAsync(data, token);
        return data.Length;
    }

    private byte[] DeflateChunk(byte[] data, int count)
    {
        var ms = new MemoryStream(count / 2 + 1024);
        var deflate = new DeflateStream(ms, _level, leaveOpen: true);
        deflate.Write(data, 0, count);
        deflate.Flush();            // sync flush: byte-aligned, *not* final, so chunks can be concatenated
        byte[] result = ms.ToArray(); // taken before Dispose, which would append a final block
        deflate.Dispose();
        return result;
    }

    /// <summary>Writes the central directory; call once after all entries.</summary>
    public void Finish()
    {
        long cdStart = _out.Position;
        foreach (var e in _entries) WriteCentralEntry(e);
        long cdSize = _out.Position - cdStart;

        bool zip64 = _entries.Count >= 0xFFFF || cdStart >= 0xFFFFFFFFL || cdSize >= 0xFFFFFFFFL;
        if (zip64)
        {
            long zip64EocdOffset = _out.Position;
            Span<byte> z = stackalloc byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(z, 0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(z[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(z[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(z[14..], 45);
            BinaryPrimitives.WriteUInt64LittleEndian(z[24..], (ulong)_entries.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(z[32..], (ulong)_entries.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(z[40..], (ulong)cdSize);
            BinaryPrimitives.WriteUInt64LittleEndian(z[48..], (ulong)cdStart);
            _out.Write(z);

            Span<byte> loc = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(loc, 0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(loc[8..], (ulong)zip64EocdOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(loc[16..], 1);
            _out.Write(loc);
        }

        Span<byte> eocd = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        ushort count = (ushort)Math.Min(_entries.Count, 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], (uint)Math.Min(cdSize, 0xFFFFFFFFL));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], (uint)Math.Min(cdStart, 0xFFFFFFFFL));
        _out.Write(eocd);
        _out.Flush();
    }

    private void WriteLocalHeader(byte[] name, ushort method, uint crc, long compressed, long uncompressed, uint dosTime, bool zip64)
    {
        Span<byte> h = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], (ushort)(zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 0x0800); // UTF-8 names
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], method);
        BinaryPrimitives.WriteUInt32LittleEndian(h[10..], dosTime);
        BinaryPrimitives.WriteUInt32LittleEndian(h[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[18..], zip64 ? 0xFFFFFFFF : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(h[22..], zip64 ? 0xFFFFFFFF : (uint)uncompressed);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)(zip64 ? 20 : 0));
        _out.Write(h);
        _out.Write(name);
        if (zip64)
        {
            Span<byte> x = stackalloc byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(x, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(x[2..], 16);
            BinaryPrimitives.WriteUInt64LittleEndian(x[4..], (ulong)uncompressed);
            BinaryPrimitives.WriteUInt64LittleEndian(x[12..], (ulong)compressed);
            _out.Write(x);
        }
    }

    private void WriteCentralEntry(CentralEntry e)
    {
        // Same threshold as the local header, so both headers agree on ZIP64 for a given file
        bool sizes64 = e.Uncompressed >= 0xFFFF0000L || e.Compressed >= 0xFFFFFFFFL;
        bool offset64 = e.HeaderOffset >= 0xFFFFFFFFL;
        var extra = new List<byte>();
        if (sizes64 || offset64)
        {
            var data = new List<byte>();
            if (sizes64) { data.AddRange(BitConverter.GetBytes((ulong)e.Uncompressed)); data.AddRange(BitConverter.GetBytes((ulong)e.Compressed)); }
            if (offset64) data.AddRange(BitConverter.GetBytes((ulong)e.HeaderOffset));
            extra.AddRange(BitConverter.GetBytes((ushort)0x0001));
            extra.AddRange(BitConverter.GetBytes((ushort)data.Count));
            extra.AddRange(data);
        }

        Span<byte> h = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 45);          // made by: MS-DOS/Windows, spec 4.5
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], (ushort)(extra.Count > 0 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], e.Method);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], e.DosTime);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], e.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], sizes64 ? 0xFFFFFFFF : (uint)e.Compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], sizes64 ? 0xFFFFFFFF : (uint)e.Uncompressed);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)e.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[30..], (ushort)extra.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(h[38..], e.IsDirectory ? 0x10u : 0x20u); // DOS attributes
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], offset64 ? 0xFFFFFFFF : (uint)e.HeaderOffset);
        _out.Write(h);
        _out.Write(e.Name);
        _out.Write(extra.ToArray());
    }

    private static uint ToDos(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        uint date = (uint)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        uint time = (uint)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
        return (date << 16) | time;
    }

    public void Dispose() => _out.Dispose();
}
