using System.Globalization;
using System.Text;

namespace QuickPeek.Core.PostScript;

/// <summary>Turns PostScript source bytes into objects: numbers, names, strings, procedures.</summary>
internal sealed class PsLexer
{
    private readonly IByteSource _src;
    public PsLexer(IByteSource src) => _src = src;

    public IByteSource Source => _src;

    public static int HexValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    public static bool IsWhite(int c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or 0;
    public static bool IsDelim(int c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    /// <summary>Next object, or null at the end. Procedures come back as executable arrays (not executed).</summary>
    public object? Next()
    {
        while (true)
        {
            int c = _src.Read();
            if (c < 0) return null;
            if (IsWhite(c)) continue;

            switch (c)
            {
                case '%':
                    while ((c = _src.Read()) >= 0 && c != '\n' && c != '\r') { }
                    continue;
                case '(':
                    return ReadString();
                case '<':
                    if (_src.Peek() == '<') { _src.Read(); return new PsName("<<", true); }
                    if (_src.Peek() == '~') { _src.Read(); return ReadAscii85String(); }
                    return ReadHexString();
                case '>':
                    if (_src.Peek() == '>') _src.Read();
                    return new PsName(">>", true);
                case '[':
                    return new PsName("[", true);
                case ']':
                    return new PsName("]", true);
                case '{':
                    return ReadProcedure();
                case '}':
                    throw new PsError("syntaxerror", "unexpected }");
                case '/':
                    if (_src.Peek() == '/')
                    {
                        _src.Read();
                        return new PsName(ReadRegular(-1), true) { }; // immediately evaluated name: treated as a plain executable name
                    }
                    return new PsName(ReadRegular(-1), false);
                default:
                    string token = ReadRegular(c);
                    return ParseNumber(token) ?? new PsName(token, true);
            }
        }
    }

    private object ReadProcedure()
    {
        var items = new List<object>();
        while (true)
        {
            int c = _src.Peek();
            while (c >= 0 && IsWhite(c)) { _src.Read(); c = _src.Peek(); }
            if (c < 0) break;
            if (c == '}') { _src.Read(); break; }
            var item = Next();
            if (item == null) break;
            items.Add(item);
        }
        return new PsArray(items.ToArray(), exec: true);
    }

    private string ReadRegular(int first)
    {
        var sb = new StringBuilder();
        if (first >= 0) sb.Append((char)first);
        while (true)
        {
            int c = _src.Peek();
            if (c < 0 || IsWhite(c) || IsDelim(c)) break;
            sb.Append((char)_src.Read());
        }
        // A single whitespace after a token belongs to it (matters for data read with currentfile right after)
        if (_src.Peek() is ' ' or '\t' or '\n') _src.Read();
        else if (_src.Peek() == '\r') { _src.Read(); if (_src.Peek() == '\n') _src.Read(); }
        return sb.ToString();
    }

    private PsString ReadString()
    {
        var bytes = new List<byte>();
        int depth = 1;
        while (true)
        {
            int c = _src.Read();
            if (c < 0) break;
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
            else if (c == '\\')
            {
                c = _src.Read();
                switch (c)
                {
                    case 'n': bytes.Add((byte)'\n'); continue;
                    case 'r': bytes.Add((byte)'\r'); continue;
                    case 't': bytes.Add((byte)'\t'); continue;
                    case 'b': bytes.Add(8); continue;
                    case 'f': bytes.Add(12); continue;
                    case '\r': if (_src.Peek() == '\n') _src.Read(); continue; // line continuation
                    case '\n': continue;
                    case >= '0' and <= '7':
                        int v = c - '0';
                        for (int i = 0; i < 2 && _src.Peek() is >= '0' and <= '7'; i++) v = v * 8 + (_src.Read() - '0');
                        bytes.Add((byte)v);
                        continue;
                    case < 0: return new PsString(bytes.ToArray());
                    default: bytes.Add((byte)c); continue;
                }
            }
            bytes.Add((byte)c);
        }
        return new PsString(bytes.ToArray());
    }

    private PsString ReadHexString()
    {
        var bytes = new List<byte>();
        int hi = -1;
        while (true)
        {
            int c = _src.Read();
            if (c < 0 || c == '>') break;
            int v = HexValue(c);
            if (v < 0) continue;
            if (hi < 0) hi = v;
            else { bytes.Add((byte)((hi << 4) | v)); hi = -1; }
        }
        if (hi >= 0) bytes.Add((byte)(hi << 4));
        return new PsString(bytes.ToArray());
    }

    private PsString ReadAscii85String()
    {
        var dec = new Ascii85Decode(_src);
        var bytes = new List<byte>();
        int b;
        while ((b = dec.Read()) >= 0) bytes.Add((byte)b);
        return new PsString(bytes.ToArray());
    }

    public static object? ParseNumber(string s)
    {
        if (s.Length == 0) return null;
        char f = s[0];
        if (!(char.IsDigit(f) || f is '-' or '+' or '.')) return null;

        int hash = s.IndexOf('#');
        if (hash > 0)
        {
            if (!int.TryParse(s.AsSpan(0, hash), out int radix) || radix < 2 || radix > 36) return null;
            long v = 0;
            foreach (char ch in s.AsSpan(hash + 1))
            {
                int digit = char.IsDigit(ch) ? ch - '0' : char.IsLetter(ch) ? char.ToLowerInvariant(ch) - 'a' + 10 : 99;
                if (digit >= radix) return null;
                v = v * radix + digit;
            }
            return v;
        }

        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        return null;
    }
}
