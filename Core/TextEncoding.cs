using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Lupik.Core;

/// <summary>
/// How a text file is encoded, so it can be shown right and saved back exactly the same way: a BOM if it has one,
/// otherwise UTF-8 if the bytes are valid UTF-8, otherwise the Windows ANSI code page (Windows-1250 on Polish
/// Windows: what Notepad and Excel used for years).
/// </summary>
public static class TextEncoding
{
    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The encoding and whether the file starts with a BOM. Leaves the stream at the start.</summary>
    public static (Encoding Encoding, bool Bom) Detect(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        int read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = 0;

        if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return (new UTF8Encoding(true), true);
        if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return (new UnicodeEncoding(false, true), true);
        if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return (new UnicodeEncoding(true, true), true);

        // The whole file (up to a limit), not just its start: a long file can be plain ASCII for pages and only
        // then have its first "ą" in the ANSI code page. Read as UTF-8, that letter would become "�" and a save
        // would write the "�" back.
        bool utf8 = IsValidUtf8(stream, buffer);
        stream.Position = 0;
        return utf8 ? (new UTF8Encoding(false), false) : (Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage), false);
    }

    private const long MaxScan = 64L * 1024 * 1024;

    private static bool IsValidUtf8(Stream stream, byte[] buffer)
    {
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        var chars = new char[buffer.Length + 1];
        long scanned = 0;
        try
        {
            int read;
            while (scanned < MaxScan && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                scanned += read;
                decoder.GetChars(buffer, 0, read, chars, 0, flush: false); // a character split between reads waits for the next one
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Writes <paramref name="text"/> the way the original was encoded (BOM only if it had one).</summary>
    public static void Write(string path, string text, Encoding encoding, bool bom)
    {
        var writeAs = encoding switch
        {
            UTF8Encoding => new UTF8Encoding(bom),
            UnicodeEncoding u => new UnicodeEncoding(u.GetPreamble().Length > 0 && u.GetPreamble()[0] == 0xFE, bom),
            _ => encoding,
        };
        File.WriteAllText(path, text, writeAs);
    }
}
