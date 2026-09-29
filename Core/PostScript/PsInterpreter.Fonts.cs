using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Lupik.Core.PostScript;

/// <summary>
/// Text. Fonts embedded in the file (Type 1 with eexec-encrypted outlines, Type 3 procedures) are drawn from their
/// own glyph programs; fonts the file only names (Helvetica, ArialMT, MyriadPro-Regular...) fall back to
/// the closest font installed in Windows.
/// </summary>
internal sealed partial class PsInterpreter
{
    private double _type3Wx, _type3Wy;
    private readonly Dictionary<(PsDict, string), (Geometry?, double)> _type1Cache = new();
    private readonly Dictionary<(string, char), (Geometry, double)> _systemGlyphCache = new();
    private static HashSet<string>? _installedFamilies;

    private void RegisterFonts()
    {
        Op("findfont", () => Push(FindFont(Pop())));
        Op("definefont", () => { var font = PopDict(); var key = Pop(); DefineFont(key, font); Push(font); });
        Op("undefinefont", () => _fontDirectory.Remove(Pop()));
        Op("composefont", () => { Pop(); Pop(); var key = Pop(); var font = FindFont(key); Push(font); });
        Op("scalefont", () => { double s = PopNum(); Push(TransformFont(PopDict(), new Matrix(s, 0, 0, s, 0, 0))); });
        Op("makefont", () => { var m = ToMatrix(PopArray()); Push(TransformFont(PopDict(), m)); });
        Op("setfont", () => _gs.Font = PopDict());
        Op("currentfont", () => Push(_gs.Font ?? FindFont(Name("Helvetica"))));
        Op("rootfont", () => Push(_gs.Font ?? FindFont(Name("Helvetica"))));
        Op("selectfont", () =>
        {
            var size = Pop();
            var font = FindFont(Pop());
            var m = size is PsArray a ? ToMatrix(a) : new Matrix(Ps.Num(size), 0, 0, Ps.Num(size), 0, 0);
            _gs.Font = TransformFont(font, m);
        });
        Op("findencoding", () =>
        {
            string n = Ps.ToText(Pop());
            Push(n == "ISOLatin1Encoding" ? PsEncodings.IsoLatin1Array() : PsEncodings.StandardArray());
        });
        Op("setcachedevice", () => { for (int i = 0; i < 4; i++) Pop(); _type3Wy = PopNum(); _type3Wx = PopNum(); });
        Op("setcachedevice2", () => { for (int i = 0; i < 8; i++) Pop(); _type3Wy = PopNum(); _type3Wx = PopNum(); });
        Op("setcharwidth", () => { _type3Wy = PopNum(); _type3Wx = PopNum(); });

        Op("show", () => ShowString(PopString(), null, null));
        Op("ashow", () =>
        {
            var s = PopString(); double ay = PopNum(), ax = PopNum();
            ShowString(s, _ => new Vector(ax, ay), null);
        });
        Op("widthshow", () =>
        {
            var s = PopString(); long ch = PopInt(); double cy = PopNum(), cx = PopNum();
            ShowString(s, code => code == ch ? new Vector(cx, cy) : default, null);
        });
        Op("awidthshow", () =>
        {
            var s = PopString(); double ay = PopNum(), ax = PopNum(); long ch = PopInt(); double cy = PopNum(), cx = PopNum();
            ShowString(s, code => new Vector(ax, ay) + (code == ch ? new Vector(cx, cy) : default), null);
        });
        Op("kshow", () =>
        {
            var s = PopString(); var proc = PopProc();
            ShowString(s, null, (a, b) => { Push((long)a); Push((long)b); Execute(proc); });
        });
        Op("cshow", () =>
        {
            var s = PopString(); var proc = PopProc();
            for (int i = 0; i < s.Length; i++)
            {
                var (dx, dy) = MeasureCode(s[i]);
                Push((long)s[i]); Push(dx); Push(dy);
                Execute(proc);
            }
        });
        Op("xshow", () => PositionedShow(x: true, y: false));
        Op("yshow", () => PositionedShow(x: false, y: true));
        Op("xyshow", () => PositionedShow(x: true, y: true));
        Op("glyphshow", () =>
        {
            var name = Ps.ToText(Pop());
            var adv = DrawGlyph(name, -1, charPath: false, measureOnly: false);
            AdvanceCurrentPoint(adv);
        });
        Op("stringwidth", () =>
        {
            var s = PopString();
            double wx = 0, wy = 0;
            for (int i = 0; i < s.Length; i++) { var (dx, dy) = MeasureCode(s[i]); wx += dx; wy += dy; }
            Push(wx); Push(wy);
        });
        Op("charpath", () =>
        {
            PopBool();
            var s = PopString();
            for (int i = 0; i < s.Length; i++)
            {
                var adv = DrawGlyph(GlyphName(s[i]), s[i], charPath: true, measureOnly: false);
                AdvanceCurrentPoint(adv);
            }
        });
    }

    // --- Font dictionaries ---

    private void DefineFont(object key, PsDict font)
    {
        font.Put("FID", new PsName("fid", false));
        if (!font.Has("FontName")) font.Put("FontName", key is PsName n ? n : Name(Ps.ToText(key)));
        _fontDirectory.Put(key, font);
    }

    private PsDict FindFont(object key)
    {
        if (key is PsDict d) return d;
        if (_fontDirectory.TryGet(key, out var f) && f is PsDict font) return font;

        // Not in the file: a stand-in that draws with an installed font
        string name = Ps.ToText(key);
        var synth = new PsDict(12);
        synth.Put("FontName", Name(name));
        synth.Put("FontType", 1L);
        synth.Put("FontMatrix", new PsArray(new object[] { 0.001, 0.0, 0.0, 0.001, 0.0, 0.0 }));
        synth.Put("FontBBox", new PsArray(new object[] { -200L, -250L, 1200L, 950L }));
        synth.Put("Encoding", PsEncodings.StandardArray());
        synth.Put("PaintType", 0L);
        synth.Put("__System", new PsString(name));
        synth.Put("FID", Name("fid"));
        _fontDirectory.Put(key, synth);
        return synth;
    }

    private static PsDict TransformFont(PsDict font, Matrix m)
    {
        var copy = new PsDict(font.Map.Count + 1);
        foreach (var kv in font.Map) copy.Map[kv.Key] = kv.Value;
        var fm = font.Get("FontMatrix") is PsArray a ? ToMatrix(a) : new Matrix(0.001, 0, 0, 0.001, 0, 0);
        copy.Put("FontMatrix", NewMatrix(Matrix.Multiply(fm, m)));
        return copy;
    }

    private PsDict CurrentFont => _gs.Font ?? throw new PsError("invalidfont", "no current font");

    private string GlyphName(int code)
    {
        var font = CurrentFont;
        if (font.Get("Encoding") is PsArray enc && code < enc.Length) return Ps.ToText(enc[code]);
        return PsEncodings.Standard[code];
    }

    // --- Showing text ---

    private void ShowString(PsString s, Func<int, Vector>? extra, Action<int, int>? between)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int code = s[i];
            var adv = DrawGlyph(GlyphName(code), code, charPath: false, measureOnly: false);
            if (extra != null) adv += extra(code);
            AdvanceCurrentPoint(adv);
            if (between != null && i + 1 < s.Length) between(code, s[i + 1]);
        }
    }

    private void PositionedShow(bool x, bool y)
    {
        var numbers = Pop();
        var s = PopString();
        var values = numbers is PsArray a ? Enumerable.Range(0, a.Length).Select(i => Ps.Num(a[i])).ToArray() : Array.Empty<double>();
        int k = 0;
        for (int i = 0; i < s.Length; i++)
        {
            DrawGlyph(GlyphName(s[i]), s[i], charPath: false, measureOnly: false);
            double dx = x && k < values.Length ? values[k++] : 0;
            double dy = y && k < values.Length ? values[k++] : 0;
            AdvanceCurrentPoint(new Vector(dx, dy));
        }
    }

    private (double, double) MeasureCode(int code)
    {
        var v = DrawGlyph(GlyphName(code), code, charPath: false, measureOnly: true);
        return (v.X, v.Y);
    }

    private void AdvanceCurrentPoint(Vector userAdvance)
    {
        var p = CurrentUserPoint();
        _gs.Path.Current = UserToDevice(p.X + userAdvance.X, p.Y + userAdvance.Y);
        if (_gs.Path.Figures.Count > 0 && _gs.Path.Figures[^1].Segments.Count == 0 && !_gs.Path.Figures[^1].Closed)
            _gs.Path.Figures[^1].Start = _gs.Path.Current.Value;
    }

    /// <summary>Draws (or measures / adds to the path) one glyph at the current point; returns the advance in user space.</summary>
    private Vector DrawGlyph(string glyphName, int code, bool charPath, bool measureOnly)
    {
        var font = CurrentFont;
        var fontMatrix = font.Get("FontMatrix") is PsArray fm ? ToMatrix(fm) : new Matrix(0.001, 0, 0, 0.001, 0, 0);
        var origin = _gs.Path.Current is Point cp ? Inverse(_gs.Ctm).Transform(cp) : new Point(0, 0);
        if (_gs.Path.Current == null && !measureOnly) throw new PsError("nocurrentpoint");

        long fontType = font.Get("FontType") is long ft ? ft : 1;
        Geometry? outline = null;
        Vector glyphAdvance;

        if (fontType == 3)
        {
            glyphAdvance = RunType3(font, fontMatrix, origin, glyphName, code, measureOnly || charPath);
            return fontMatrix.Transform(glyphAdvance);
        }

        if (font.Get("CharStrings") is PsDict charStrings && charStrings.TryGet(glyphName, out var csObj) && csObj is PsString cs && font.Get("__System") == null)
        {
            var key = (charStrings, glyphName);
            if (!_type1Cache.TryGetValue(key, out var cached))
            {
                try { cached = Type1Glyph.Decode(font, cs); }
                catch (Exception) { cached = (null, 0); }
                _type1Cache[key] = cached;
            }
            outline = cached.Item1;
            glyphAdvance = new Vector(cached.Item2, 0);
        }
        else
        {
            char ch = PsEncodings.CharFor(glyphName) ?? (code >= 32 ? (char)code : ' ');
            (outline, double w) = SystemGlyph(font, ch);
            glyphAdvance = new Vector(w, 0);
        }

        if (!measureOnly && outline != null && !outline.IsEmpty())
        {
            var toDevice = Matrix.Multiply(Matrix.Multiply(fontMatrix, new Matrix(1, 0, 0, 1, origin.X, origin.Y)), _gs.Ctm);
            if (charPath)
            {
                var keep = _gs.Path.Current;
                _gs.Path.Append(outline, toDevice);
                _gs.Path.Current = keep;
            }
            else
            {
                var g = outline.Clone();
                g.Transform = new MatrixTransform(toDevice);
                g.Freeze();
                Paint(g);
            }
        }
        return fontMatrix.Transform(glyphAdvance);
    }

    private Vector RunType3(PsDict font, Matrix fontMatrix, Point origin, string glyphName, int code, bool measureOnly)
    {
        _type3Wx = _type3Wy = 0;
        var outerDc = _dc;
        DrawingGroup? scratch = null;
        DrawingContext? scratchDc = null;
        if (measureOnly) { scratch = new DrawingGroup(); scratchDc = scratch.Open(); _dc = scratchDc; }
        SaveGState();
        try
        {
            _gs.Ctm = Matrix.Multiply(Matrix.Multiply(fontMatrix, new Matrix(1, 0, 0, 1, origin.X, origin.Y)), _gs.Ctm);
            _gs.Path = new PsPath { Current = _gs.Ctm.Transform(new Point(0, 0)) };
            if (font.Get("BuildGlyph") is PsArray buildGlyph)
            {
                Push(font); Push(Name(glyphName));
                Execute(buildGlyph);
            }
            else if (font.Get("BuildChar") is PsArray buildChar)
            {
                Push(font); Push((long)Math.Max(code, 0));
                Execute(buildChar);
            }
        }
        catch (PsError) { }
        finally
        {
            GRestore();
            if (scratchDc != null) { scratchDc.Close(); _dc = outerDc; }
        }
        _ = scratch;
        return new Vector(_type3Wx, _type3Wy);
    }

    // --- System fonts ---

    private (Geometry, double) SystemGlyph(PsDict font, char ch)
    {
        string psName = (font.Get("__System") as PsString)?.Text ?? Ps.ToText(font.Get("FontName") ?? Name("Helvetica"));
        var key = (psName, ch);
        if (_systemGlyphCache.TryGetValue(key, out var cached)) return cached;

        var typeface = TypefaceFor(psName);
        var text = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 1000, Brushes.Black, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        // WPF: y down, origin at the top; PostScript glyph space: y up, origin on the baseline
        var flip = new Matrix(1, 0, 0, -1, 0, text.Baseline);
        var g = geometry.Clone();
        g.Transform = new MatrixTransform(flip);
        var flat = PathGeometry.CreateFromGeometry(g);
        flat.Freeze();
        var result = ((Geometry)flat, text.WidthIncludingTrailingWhitespace);
        _systemGlyphCache[key] = result;
        return result;
    }

    /// <summary>PostScript font name → an installed Windows font (Helvetica → Arial, MyriadPro-Bold → Myriad Pro Bold...).</summary>
    internal static Typeface TypefaceFor(string psName)
    {
        string name = psName.TrimStart('_', '*', '/');
        int plus = name.IndexOf('+');
        if (plus == 6) name = name[7..]; // subset prefix "ABCDEF+"
        string style = "";
        int dash = name.IndexOf('-');
        string family = dash > 0 ? name[..dash] : name;
        if (dash > 0) style = name[(dash + 1)..];
        string lower = (family + " " + style).ToLowerInvariant();

        var weight = lower.Contains("black") || lower.Contains("heavy") ? FontWeights.Black
            : lower.Contains("semibold") || lower.Contains("demi") ? FontWeights.SemiBold
            : lower.Contains("bold") ? FontWeights.Bold
            : lower.Contains("light") ? FontWeights.Light
            : FontWeights.Normal;
        var fontStyle = lower.Contains("italic") || lower.Contains("oblique") ? FontStyles.Italic : FontStyles.Normal;

        foreach (var suffix in new[] { "PSMT", "MT", "PS", "Std", "LT" })
            if (family.EndsWith(suffix, StringComparison.Ordinal) && family.Length > suffix.Length + 2) family = family[..^suffix.Length];
        family = family switch
        {
            "Helvetica" or "HelveticaNeue" or "Arial" => "Arial",
            "Times" or "TimesNewRoman" or "TimesRoman" => "Times New Roman",
            "Courier" or "CourierNew" => "Courier New",
            "Symbol" => "Symbol",
            "ZapfDingbats" => "Wingdings",
            _ => family,
        };

        var installed = _installedFamilies ??= new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        string spaced = System.Text.RegularExpressions.Regex.Replace(family, "(?<=[a-z])(?=[A-Z])", " ");
        string chosen = installed.Contains(family) ? family : installed.Contains(spaced) ? spaced : "Arial";
        return new Typeface(new FontFamily(chosen), fontStyle, weight, FontStretches.Normal);
    }
}

/// <summary>
/// Type 1 charstrings (Adobe Type 1 Font Format, ch. 6): an encrypted little stack language that draws each glyph.
/// Hints are ignored (they only matter for tiny sizes on printers); flex and seac (accented letters) are handled.
/// </summary>
internal static class Type1Glyph
{
    public static (Geometry?, double) Decode(PsDict font, PsString charString)
    {
        var priv = font.Get("Private") as PsDict;
        int lenIV = priv?.Get("lenIV") is long l ? (int)l : 4;
        var subrs = priv?.Get("Subrs") as PsArray;
        var charStrings = font.Get("CharStrings") as PsDict;

        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        var state = new State(geometry);
        Run(Decrypt(charString.Span, lenIV), subrs, lenIV, state, charStrings, 0);
        state.Finish();
        geometry.Freeze();
        return (geometry, state.Width);
    }

    private sealed class State
    {
        public readonly PathGeometry Geometry;
        public PathFigure? Figure;
        public Point Current;
        public double Width;
        public bool Flexing;
        public readonly List<Point> FlexPoints = new();
        public readonly Stack<double> PsStack = new();
        public Vector Offset; // seac accent shift
        public bool Done;

        public State(PathGeometry g) => Geometry = g;

        public void MoveTo(Point p)
        {
            Current = p;
            if (Flexing) { FlexPoints.Add(p); return; }
            Finish();
        }

        private void Ensure()
        {
            if (Figure == null)
            {
                Figure = new PathFigure { StartPoint = Current + Offset, IsFilled = true };
            }
        }

        public void LineTo(Point p)
        {
            Ensure();
            Figure!.Segments.Add(new LineSegment(p + Offset, true));
            Current = p;
        }

        public void CurveTo(Point a, Point b, Point c)
        {
            Ensure();
            Figure!.Segments.Add(new BezierSegment(a + Offset, b + Offset, c + Offset, true));
            Current = c;
        }

        public void Close()
        {
            if (Figure != null) Figure.IsClosed = true;
            Finish();
        }

        public void Finish()
        {
            if (Figure != null && Figure.Segments.Count > 0) { Figure.IsClosed = true; Geometry.Figures.Add(Figure); }
            Figure = null;
        }
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> data, int lenIV)
    {
        if (lenIV < 0) return data.ToArray();
        ushort r = 4330;
        var plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            plain[i] = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
        }
        return plain.Length > lenIV ? plain[lenIV..] : Array.Empty<byte>();
    }

    private static void Run(byte[] code, PsArray? subrs, int lenIV, State s, PsDict? charStrings, int depth) =>
        Run(code, subrs, lenIV, s, charStrings, depth, new List<double>());

    /// <summary>
    /// Subroutines share the caller's operand stack (they often leave numbers for it), so the stack is passed down;
    /// "return" just ends the nested call.
    /// </summary>
    private static void Run(byte[] code, PsArray? subrs, int lenIV, State s, PsDict? charStrings, int depth, List<double> stack)
    {
        if (depth > 12) return;
        int i = 0;
        while (i < code.Length && !s.Done)
        {
            int v = code[i++];
            if (v >= 32)
            {
                double num;
                if (v <= 246) num = v - 139;
                else if (v <= 250) num = (v - 247) * 256 + code[i++] + 108;
                else if (v <= 254) num = -(v - 251) * 256 - code[i++] - 108;
                else { num = (code[i] << 24) | (code[i + 1] << 16) | (code[i + 2] << 8) | code[i + 3]; i += 4; }
                stack.Add(num);
                continue;
            }

            double Arg(int k) => k >= 0 && k < stack.Count ? stack[k] : 0;
            switch (v)
            {
                case 1: case 3: break; // hstem, vstem
                case 4: s.MoveTo(s.Current + new Vector(0, Arg(stack.Count - 1))); break; // vmoveto
                case 5: s.LineTo(s.Current + new Vector(Arg(0), Arg(1))); break; // rlineto
                case 6: s.LineTo(s.Current + new Vector(Arg(0), 0)); break; // hlineto
                case 7: s.LineTo(s.Current + new Vector(0, Arg(0))); break; // vlineto
                case 8: // rrcurveto
                    {
                        var a = s.Current + new Vector(Arg(0), Arg(1));
                        var b = a + new Vector(Arg(2), Arg(3));
                        var c = b + new Vector(Arg(4), Arg(5));
                        s.CurveTo(a, b, c);
                        break;
                    }
                case 9: s.Close(); break; // closepath
                case 10: // callsubr
                    {
                        int n = (int)Arg(stack.Count - 1);
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        if (subrs != null && n >= 0 && n < subrs.Length && subrs[n] is PsString sub)
                            Run(Decrypt(sub.Span, lenIV), subrs, lenIV, s, charStrings, depth + 1, stack);
                        continue;
                    }
                case 11: return; // return
                case 13: // hsbw
                    s.Current = new Point(Arg(0), 0);
                    s.Width = Arg(1);
                    break;
                case 14: s.Finish(); s.Done = true; return; // endchar
                case 21: s.MoveTo(s.Current + new Vector(Arg(stack.Count - 2), Arg(stack.Count - 1))); break; // rmoveto
                case 22: s.MoveTo(s.Current + new Vector(Arg(stack.Count - 1), 0)); break; // hmoveto
                case 30: // vhcurveto
                    {
                        var a = s.Current + new Vector(0, Arg(0));
                        var b = a + new Vector(Arg(1), Arg(2));
                        var c = b + new Vector(Arg(3), 0);
                        s.CurveTo(a, b, c);
                        break;
                    }
                case 31: // hvcurveto
                    {
                        var a = s.Current + new Vector(Arg(0), 0);
                        var b = a + new Vector(Arg(1), Arg(2));
                        var c = b + new Vector(0, Arg(3));
                        s.CurveTo(a, b, c);
                        break;
                    }
                case 12:
                    {
                        int esc = i < code.Length ? code[i++] : 0;
                        switch (esc)
                        {
                            case 6: // seac
                                Seac(Arg(0), Arg(1), Arg(2), (int)Arg(3), (int)Arg(4), s, charStrings, subrs, lenIV, depth);
                                s.Done = true;
                                return;
                            case 7: // sbw
                                s.Current = new Point(Arg(0), Arg(1));
                                s.Width = Arg(2);
                                break;
                            case 12: // div
                                {
                                    double b = Arg(stack.Count - 1), a = Arg(stack.Count - 2);
                                    stack.RemoveRange(Math.Max(0, stack.Count - 2), Math.Min(2, stack.Count));
                                    stack.Add(b == 0 ? 0 : a / b);
                                    continue;
                                }
                            case 16: // callothersubr
                                {
                                    int other = (int)Arg(stack.Count - 1);
                                    int n = Math.Clamp((int)Arg(stack.Count - 2), 0, Math.Max(0, stack.Count - 2));
                                    stack.RemoveRange(Math.Max(0, stack.Count - 2), Math.Min(2, stack.Count));
                                    var args = stack.GetRange(stack.Count - n, n);
                                    stack.RemoveRange(stack.Count - n, n);
                                    OtherSubr(other, args, s);
                                    continue;
                                }
                            case 17: // pop: an OtherSubr result back onto the charstring stack
                                stack.Add(s.PsStack.Count > 0 ? s.PsStack.Pop() : 0);
                                continue;
                            case 33: // setcurrentpoint
                                s.Current = new Point(Arg(stack.Count - 2), Arg(stack.Count - 1));
                                break;
                        }
                        break; // dotsection, vstem3, hstem3
                    }
            }
            stack.Clear();
        }
    }
    private static void OtherSubr(int other, List<double> args, State s)
    {
        switch (other)
        {
            case 1: // start flex
                s.Flexing = true;
                s.FlexPoints.Clear();
                break;
            case 2: // flex point (already recorded by rmoveto)
                break;
            case 0: // end flex: 7 points collected, draw two curves
                s.Flexing = false;
                if (s.FlexPoints.Count >= 7)
                {
                    var p = s.FlexPoints;
                    s.Current = p[0];
                    s.CurveTo(p[1], p[2], p[3]);
                    s.CurveTo(p[4], p[5], p[6]);
                    s.PsStack.Push(p[6].Y);
                    s.PsStack.Push(p[6].X);
                }
                else
                {
                    s.PsStack.Push(s.Current.Y);
                    s.PsStack.Push(s.Current.X);
                }
                break;
            default: // 3 = hint replacement: return the arguments for "pop"
                for (int k = args.Count - 1; k >= 0; k--) s.PsStack.Push(args[k]);
                break;
        }
    }

    private static void Seac(double asb, double adx, double ady, int bchar, int achar, State s, PsDict? charStrings, PsArray? subrs, int lenIV, int depth)
    {
        if (charStrings == null) return;
        string baseName = PsEncodings.Standard[Math.Clamp(bchar, 0, 255)];
        string accentName = PsEncodings.Standard[Math.Clamp(achar, 0, 255)];
        double width = s.Width;

        if (charStrings.TryGet(baseName, out var b) && b is PsString bs)
        {
            s.Offset = default;
            s.Done = false;
            Run(Decrypt(bs.Span, lenIV), subrs, lenIV, s, charStrings, depth + 1);
            width = s.Width;
        }
        if (charStrings.TryGet(accentName, out var a) && a is PsString accent)
        {
            s.Finish();
            s.Offset = new Vector(adx - asb, ady);
            s.Done = false;
            Run(Decrypt(accent.Span, lenIV), subrs, lenIV, s, charStrings, depth + 1);
            s.Offset = default;
        }
        s.Finish();
        s.Width = width;
    }
}
