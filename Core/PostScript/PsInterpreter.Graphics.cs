using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickPeek.Core.PostScript;

internal struct PsSegment
{
    public bool Curve;
    public Point P1, P2, P3; // line: P3 only
}

internal sealed class PsFigure
{
    public Point Start;
    public readonly List<PsSegment> Segments = new();
    public bool Closed;
}

/// <summary>Current path, in device coordinates (as PostScript defines it: the CTM applies when points are added).</summary>
internal sealed class PsPath
{
    public readonly List<PsFigure> Figures = new();
    public Point? Current;

    public PsPath Clone()
    {
        var p = new PsPath { Current = Current };
        foreach (var f in Figures)
        {
            var nf = new PsFigure { Start = f.Start, Closed = f.Closed };
            nf.Segments.AddRange(f.Segments);
            p.Figures.Add(nf);
        }
        return p;
    }

    public bool IsEmpty => Figures.Count == 0;

    public Geometry ToGeometry(FillRule rule)
    {
        var g = new StreamGeometry { FillRule = rule };
        using (var ctx = g.Open())
        {
            foreach (var f in Figures)
            {
                ctx.BeginFigure(f.Start, isFilled: true, isClosed: f.Closed);
                foreach (var s in f.Segments)
                {
                    if (s.Curve) ctx.BezierTo(s.P1, s.P2, s.P3, isStroked: true, isSmoothJoin: false);
                    else ctx.LineTo(s.P3, isStroked: true, isSmoothJoin: false);
                }
            }
        }
        g.Freeze();
        return g;
    }

    public void Append(Geometry geometry, Matrix transform)
    {
        var pg = PathGeometry.CreateFromGeometry(geometry);
        foreach (var fig in pg.Figures)
        {
            var nf = new PsFigure { Start = transform.Transform(fig.StartPoint), Closed = fig.IsClosed };
            Point last = fig.StartPoint;
            foreach (var seg in fig.Segments)
            {
                switch (seg)
                {
                    case LineSegment l: nf.Segments.Add(new PsSegment { P3 = transform.Transform(l.Point) }); last = l.Point; break;
                    case PolyLineSegment pl: foreach (var p in pl.Points) nf.Segments.Add(new PsSegment { P3 = transform.Transform(p) }); if (pl.Points.Count > 0) last = pl.Points[^1]; break;
                    case BezierSegment b: nf.Segments.Add(new PsSegment { Curve = true, P1 = transform.Transform(b.Point1), P2 = transform.Transform(b.Point2), P3 = transform.Transform(b.Point3) }); last = b.Point3; break;
                    case PolyBezierSegment pb:
                        for (int i = 0; i + 2 < pb.Points.Count; i += 3)
                            nf.Segments.Add(new PsSegment { Curve = true, P1 = transform.Transform(pb.Points[i]), P2 = transform.Transform(pb.Points[i + 1]), P3 = transform.Transform(pb.Points[i + 2]) });
                        if (pb.Points.Count > 0) last = pb.Points[^1];
                        break;
                    case QuadraticBezierSegment q:
                        {
                            var c1 = last + (q.Point1 - last) * (2.0 / 3);
                            var c2 = q.Point2 + (q.Point1 - q.Point2) * (2.0 / 3);
                            nf.Segments.Add(new PsSegment { Curve = true, P1 = transform.Transform(c1), P2 = transform.Transform(c2), P3 = transform.Transform(q.Point2) });
                            last = q.Point2;
                            break;
                        }
                    case PolyQuadraticBezierSegment pq:
                        for (int i = 0; i + 1 < pq.Points.Count; i += 2)
                        {
                            var c = pq.Points[i]; var e = pq.Points[i + 1];
                            nf.Segments.Add(new PsSegment { Curve = true, P1 = transform.Transform(last + (c - last) * (2.0 / 3)), P2 = transform.Transform(e + (c - e) * (2.0 / 3)), P3 = transform.Transform(e) });
                            last = e;
                        }
                        break;
                    case ArcSegment a:
                        nf.Segments.Add(new PsSegment { P3 = transform.Transform(a.Point) }); last = a.Point; break;
                }
            }
            Figures.Add(nf);
            Current = nf.Segments.Count > 0 ? nf.Segments[^1].P3 : nf.Start;
        }
    }

    public Rect Bounds()
    {
        var r = Rect.Empty;
        foreach (var f in Figures)
        {
            r.Union(f.Start);
            foreach (var s in f.Segments)
            {
                if (s.Curve) { r.Union(s.P1); r.Union(s.P2); }
                r.Union(s.P3);
            }
        }
        return r;
    }
}

internal sealed class GState
{
    public Matrix Ctm = Matrix.Identity;
    public PsPath Path = new();
    public Color Color = Colors.Black;
    public object ColorSpace = "DeviceGray";
    public int ColorComponents = 1;
    public double[] ColorValues = { 0 };
    public PsDict? Pattern;
    public double LineWidth = 1;
    public PenLineCap Cap = PenLineCap.Flat;
    public PenLineJoin Join = PenLineJoin.Miter;
    public double MiterLimit = 10;
    public double[] Dash = Array.Empty<double>();
    public double DashOffset;
    public PsDict? Font;
    public int ClipPushes;
    public Geometry? Clip; // device space, for clippath / pathbbox of the clip

    public GState Clone()
    {
        var g = (GState)MemberwiseClone();
        g.Path = Path.Clone();
        g.ClipPushes = 0;
        return g;
    }
}

internal sealed partial class PsInterpreter
{
    private GState _gs = new();
    private readonly Stack<GState> _gstack = new();
    private DrawingContext _dc = null!;
    private Matrix _defaultMatrix = Matrix.Identity;
    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private readonly Stack<(int Level, int Pushes, Geometry? Clip)> _clipSaves = new();

    /// <summary>Sets up the page: device coordinates are pixels of the output, y pointing down.</summary>
    public void BeginPage(DrawingContext dc, Matrix deviceMatrix)
    {
        _dc = dc;
        _defaultMatrix = deviceMatrix;
        _gs = new GState { Ctm = deviceMatrix };
    }

    /// <summary>Pops everything still pushed on the drawing context (clips of unbalanced gsaves).</summary>
    public void EndPage()
    {
        RestoreGState(0);
        for (int i = 0; i < _gs.ClipPushes; i++) _dc.Pop();
        _gs.ClipPushes = 0;
    }

    private int SaveGState()
    {
        _gstack.Push(_gs);
        _gs = _gs.Clone();
        return _gstack.Count - 1;
    }

    private void GRestore()
    {
        if (_gstack.Count == 0) return;
        for (int i = 0; i < _gs.ClipPushes; i++) _dc.Pop();
        _gs = _gstack.Pop();
    }

    private void RestoreGState(int depth)
    {
        while (_gstack.Count > depth) GRestore();
    }

    private Brush BrushFor(Color c)
    {
        if (!_brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushes[c] = b;
        }
        return b;
    }

    // --- Matrices ---

    private static Matrix ToMatrix(PsArray a)
    {
        if (a.Length < 6) throw new PsError("rangecheck", "matrix");
        return new Matrix(Ps.Num(a[0]), Ps.Num(a[1]), Ps.Num(a[2]), Ps.Num(a[3]), Ps.Num(a[4]), Ps.Num(a[5]));
    }

    private static void StoreMatrix(PsArray a, Matrix m)
    {
        a[0] = m.M11; a[1] = m.M12; a[2] = m.M21; a[3] = m.M22; a[4] = m.OffsetX; a[5] = m.OffsetY;
    }

    private static PsArray NewMatrix(Matrix m)
    {
        var a = new PsArray(new object[6]);
        StoreMatrix(a, m);
        return a;
    }

    private static Matrix Inverse(Matrix m)
    {
        if (!m.HasInverse) throw new PsError("undefinedresult", "singular matrix");
        m.Invert();
        return m;
    }

    /// <summary>translate / scale / rotate: change the CTM, or with a matrix operand, fill that matrix instead.</summary>
    private void MatrixOp(Func<Matrix> make, int operands)
    {
        if (Peek() is PsArray target)
        {
            Pop();
            var m = make();
            StoreMatrix(target, m);
            Push(target);
        }
        else
        {
            _gs.Ctm = Matrix.Multiply(make(), _gs.Ctm);
        }
        _ = operands;
    }

    private Point UserToDevice(double x, double y) => _gs.Ctm.Transform(new Point(x, y));

    private Point CurrentUserPoint()
    {
        if (_gs.Path.Current is not Point p) throw new PsError("nocurrentpoint");
        return Inverse(_gs.Ctm).Transform(p);
    }

    // --- Path construction ---

    private void MoveTo(Point device)
    {
        var path = _gs.Path;
        // Consecutive movetos: the last one wins
        if (path.Figures.Count > 0 && path.Figures[^1].Segments.Count == 0 && !path.Figures[^1].Closed)
            path.Figures[^1].Start = device;
        else
            path.Figures.Add(new PsFigure { Start = device });
        path.Current = device;
    }

    private PsFigure OpenFigure()
    {
        var path = _gs.Path;
        if (path.Current is not Point cur) throw new PsError("nocurrentpoint");
        if (path.Figures.Count == 0 || path.Figures[^1].Closed)
            path.Figures.Add(new PsFigure { Start = cur });
        return path.Figures[^1];
    }

    private void LineTo(Point device)
    {
        OpenFigure().Segments.Add(new PsSegment { P3 = device });
        _gs.Path.Current = device;
    }

    private void CurveTo(Point a, Point b, Point c)
    {
        OpenFigure().Segments.Add(new PsSegment { Curve = true, P1 = a, P2 = b, P3 = c });
        _gs.Path.Current = c;
    }

    private void ClosePath()
    {
        var path = _gs.Path;
        if (path.Figures.Count == 0 || path.Figures[^1].Closed) return;
        var f = path.Figures[^1];
        f.Closed = true;
        path.Current = f.Start;
    }

    /// <summary>Arc as Bézier pieces of at most 90°, in user space, then into device space.</summary>
    private void Arc(double x, double y, double r, double a1, double a2, bool clockwise)
    {
        double start = a1 * Math.PI / 180, end = a2 * Math.PI / 180;
        if (!clockwise) { while (end < start) end += 2 * Math.PI; }
        else { while (end > start) end -= 2 * Math.PI; }

        Point P(double ang) => UserToDevice(x + r * Math.Cos(ang), y + r * Math.Sin(ang));
        var first = P(start);
        if (_gs.Path.Current == null) MoveTo(first); else LineTo(first);

        double total = end - start;
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(total) / (Math.PI / 2) - 1e-9));
        double step = total / pieces;
        double k = 4.0 / 3 * Math.Tan(step / 4);
        double a = start;
        for (int i = 0; i < pieces; i++)
        {
            double b = a + step;
            double c1x = x + r * (Math.Cos(a) - k * Math.Sin(a)), c1y = y + r * (Math.Sin(a) + k * Math.Cos(a));
            double c2x = x + r * (Math.Cos(b) + k * Math.Sin(b)), c2y = y + r * (Math.Sin(b) - k * Math.Cos(b));
            CurveTo(UserToDevice(c1x, c1y), UserToDevice(c2x, c2y), P(b));
            a = b;
        }
    }

    /// <summary>arct/arcto: rounded corner between the lines current→(x1,y1)→(x2,y2).</summary>
    private (double, double, double, double) ArcTo(double x1, double y1, double x2, double y2, double r)
    {
        var p0 = CurrentUserPoint();
        double ux = p0.X - x1, uy = p0.Y - y1, vx = x2 - x1, vy = y2 - y1;
        double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
        if (lu == 0 || lv == 0 || r == 0) { LineTo(UserToDevice(x1, y1)); return (x1, y1, x1, y1); }
        ux /= lu; uy /= lu; vx /= lv; vy /= lv;
        double cos = ux * vx + uy * vy;
        double angle = Math.Acos(Math.Clamp(cos, -1, 1));
        if (Math.Abs(Math.Sin(angle)) < 1e-9) { LineTo(UserToDevice(x1, y1)); return (x1, y1, x1, y1); }
        double dist = r / Math.Tan(angle / 2);
        double t1x = x1 + ux * dist, t1y = y1 + uy * dist, t2x = x1 + vx * dist, t2y = y1 + vy * dist;
        // Center along the bisector
        double bx = ux + vx, by = uy + vy, bl = Math.Sqrt(bx * bx + by * by);
        double cd = r / Math.Sin(angle / 2);
        double cx = x1 + bx / bl * cd, cy = y1 + by / bl * cd;
        double a1 = Math.Atan2(t1y - cy, t1x - cx) * 180 / Math.PI;
        double a2 = Math.Atan2(t2y - cy, t2x - cx) * 180 / Math.PI;
        bool clockwise = ux * vy - uy * vx > 0;
        LineTo(UserToDevice(t1x, t1y));
        Arc(cx, cy, r, a1, a2, clockwise);
        return (t1x, t1y, t2x, t2y);
    }

    // --- Painting ---

    private void FillPath(FillRule rule)
    {
        if (!_gs.Path.IsEmpty) Paint(_gs.Path.ToGeometry(rule));
        _gs.Path = new PsPath();
    }

    /// <summary>How many things were painted: 0 means the program drew nothing we could render.</summary>
    public int PaintCount { get; private set; }

    private void Paint(Geometry deviceGeometry)
    {
        PaintCount++;
        if (_gs.Pattern != null) { PaintPattern(deviceGeometry, _gs.Pattern); return; }
        _dc.DrawGeometry(BrushFor(_gs.Color), null, deviceGeometry);
    }

    private Pen? UserPen(out Matrix ctm)
    {
        ctm = _gs.Ctm;
        if (!ctm.HasInverse) return null;
        double det = Math.Abs(ctm.M11 * ctm.M22 - ctm.M12 * ctm.M21);
        double hairline = 1 / Math.Sqrt(Math.Max(det, 1e-12));
        double width = Math.Max(_gs.LineWidth, hairline * 0.75); // "0 setlinewidth" = thinnest visible line
        var pen = new Pen(_gs.Pattern != null ? BrushFor(Colors.Gray) : BrushFor(_gs.Color), width)
        {
            StartLineCap = _gs.Cap,
            EndLineCap = _gs.Cap,
            DashCap = _gs.Cap,
            LineJoin = _gs.Join,
            MiterLimit = Math.Max(1, _gs.MiterLimit),
        };
        if (_gs.Dash.Length > 0 && _gs.Dash.Any(d => d > 0))
            pen.DashStyle = new DashStyle(_gs.Dash.Select(d => d / width), _gs.DashOffset / width);
        pen.Freeze();
        return pen;
    }

    private void StrokePath()
    {
        if (!_gs.Path.IsEmpty)
        {
            var pen = UserPen(out var ctm);
            if (pen != null)
            {
                // Stroke in user space, so line width and dashes follow a non-uniform CTM like PostScript does
                var user = _gs.Path.ToGeometry(FillRule.Nonzero).Clone();
                user.Transform = new MatrixTransform(Inverse(ctm));
                _dc.PushTransform(new MatrixTransform(ctm));
                _dc.DrawGeometry(null, pen, user);
                PaintCount++;
                _dc.Pop();
            }
        }
        _gs.Path = new PsPath();
    }

    private void ClipPath(FillRule rule)
    {
        var geometry = _gs.Path.ToGeometry(rule);
        _dc.PushClip(geometry);
        _gs.ClipPushes++;
        _gs.Clip = _gs.Clip == null ? geometry : Geometry.Combine(_gs.Clip, geometry, GeometryCombineMode.Intersect, null);
    }

    private void SetColor(Color c)
    {
        _gs.Color = c;
        _gs.Pattern = null;
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    private static Color Rgb(double r, double g, double b) => Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
    private static Color Cmyk(double c, double m, double y, double k) =>
        Rgb((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));

    private static Color Hsb(double h, double s, double v)
    {
        h = (h - Math.Floor(h)) * 6;
        int i = (int)h;
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => Rgb(v, t, p), 1 => Rgb(q, v, p), 2 => Rgb(p, v, t),
            3 => Rgb(p, q, v), 4 => Rgb(t, p, v), _ => Rgb(v, p, q),
        };
    }

    private static string SpaceFamily(object space) => space switch
    {
        string s => s,
        PsName n => n.Value,
        PsArray a when a.Length > 0 => Ps.ToText(a[0]),
        _ => "DeviceGray",
    };

    private int ComponentsOf(object space)
    {
        switch (SpaceFamily(space))
        {
            case "DeviceGray" or "CalGray" or "Indexed" or "Separation" or "CIEBasedA": return 1;
            case "DeviceRGB" or "CalRGB" or "Lab" or "CIEBasedABC" or "CIEBasedDEF": return 3;
            case "DeviceCMYK" or "CIEBasedDEFG": return 4;
            case "DeviceN": return space is PsArray a && a.Length > 1 && a[1] is PsArray names ? names.Length : 1;
            case "ICCBased":
                if (space is PsArray ia && ia.Length > 1 && ia[1] is PsDict d && d.Get("N") is long n) return (int)n;
                return 3;
            case "Pattern": return 1;
            default: return 1;
        }
    }

    /// <summary>Converts color components in any supported color space to RGB.</summary>
    private Color ColorFrom(object space, ReadOnlySpan<double> v)
    {
        string family = SpaceFamily(space);
        switch (family)
        {
            case "DeviceGray" or "CalGray" or "CIEBasedA": return Rgb(v[0], v[0], v[0]);
            case "DeviceRGB" or "CalRGB" or "CIEBasedABC" or "CIEBasedDEF": return Rgb(v[0], v[1], v[2]);
            case "DeviceCMYK" or "CIEBasedDEFG": return Cmyk(v[0], v[1], v[2], v[3]);
            case "Lab": return LabToRgb(v[0], v[1], v[2]);
            case "ICCBased":
                return v.Length switch { 1 => Rgb(v[0], v[0], v[0]), 4 => Cmyk(v[0], v[1], v[2], v[3]), _ => Rgb(v[0], v[1], v[2]) };
            case "Indexed" when space is PsArray a && a.Length >= 4:
                {
                    var baseSpace = a[1];
                    int n = ComponentsOf(baseSpace);
                    int index = (int)Math.Clamp(Math.Round(v[0]), 0, Ps.Int(a[2]));
                    var comps = new double[n];
                    if (a[3] is PsString lookup)
                        for (int i = 0; i < n && index * n + i < lookup.Length; i++) comps[i] = lookup[index * n + i] / 255.0;
                    else if (a[3] is PsArray proc)
                    {
                        Push((long)index);
                        Execute(proc);
                        for (int i = n - 1; i >= 0; i--) comps[i] = PopNum();
                    }
                    return ColorFrom(baseSpace, comps);
                }
            case "Separation" or "DeviceN" when space is PsArray a && a.Length >= 4:
                {
                    // Spot colors: the tint transform (a PostScript procedure) maps tints to the alternate space
                    var alt = a[2];
                    for (int i = 0; i < v.Length; i++) Push(v[i]);
                    try
                    {
                        Execute(a[3]);
                        int n = ComponentsOf(alt);
                        var comps = new double[n];
                        for (int i = n - 1; i >= 0; i--) comps[i] = PopNum();
                        return ColorFrom(alt, comps);
                    }
                    catch (PsError)
                    {
                        return Rgb(1 - v[0], 1 - v[0], 1 - v[0]);
                    }
                }
            default:
                return v.Length >= 3 ? Rgb(v[0], v[1], v[2]) : Rgb(v[0], v[0], v[0]);
        }
    }

    private static Color LabToRgb(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double F(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
        double x = 0.9505 * F(fx), y = F(fy), z = 1.089 * F(fz);
        double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        double bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        static double Gamma(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return Rgb(Gamma(r), Gamma(g), Gamma(bl));
    }

    private void SetColorSpace(object space)
    {
        _gs.ColorSpace = space;
        int n = ComponentsOf(space);
        _gs.ColorComponents = n;
        string family = SpaceFamily(space);
        // Initial color: black (gray/RGB 0, CMYK 0 0 0 1), or index 0 / full tint
        var init = new double[n];
        if (family == "DeviceCMYK") init[3] = 1;
        if (family is "Separation" or "DeviceN") Array.Fill(init, 1.0);
        _gs.ColorValues = init;
        if (family == "Pattern") { _gs.Pattern = null; return; }
        SetColor(ColorFrom(space, init));
    }

    // --- Registration ---

    private void RegisterGraphics()
    {
        Op("gsave", () => SaveGState());
        Op("grestore", GRestore);
        Op("grestoreall", () => RestoreGState(0));
        Op("initgraphics", () => { _gs.Ctm = _defaultMatrix; _gs.Path = new PsPath(); _gs.LineWidth = 1; SetColor(Colors.Black); _gs.Dash = Array.Empty<double>(); });
        Op("gstate", () => Push(new PsGState { Snapshot = _gs.Clone() }));
        Op("currentgstate", () => { var g = Pop() as PsGState ?? new PsGState(); g.Snapshot = _gs.Clone(); Push(g); });
        Op("setgstate", () =>
        {
            if (Pop() is PsGState { Snapshot: GState s })
            {
                var pushes = _gs.ClipPushes;
                _gs = s.Clone();
                _gs.ClipPushes = pushes; // the drawing context's clip stack stays as it is
            }
        });

        // Matrices
        Op("matrix", () => Push(NewMatrix(Matrix.Identity)));
        Op("identmatrix", () => { var a = PopArray(); StoreMatrix(a, Matrix.Identity); Push(a); });
        Op("initmatrix", () => _gs.Ctm = _defaultMatrix);
        Op("defaultmatrix", () => { var a = PopArray(); StoreMatrix(a, _defaultMatrix); Push(a); });
        Op("currentmatrix", () => { var a = PopArray(); StoreMatrix(a, _gs.Ctm); Push(a); });
        Op("setmatrix", () => _gs.Ctm = ToMatrix(PopArray()));
        Op("concat", () => _gs.Ctm = Matrix.Multiply(ToMatrix(PopArray()), _gs.Ctm));
        Op("concatmatrix", () =>
        {
            var dest = PopArray();
            var b = ToMatrix(PopArray());
            var a = ToMatrix(PopArray());
            StoreMatrix(dest, Matrix.Multiply(a, b));
            Push(dest);
        });
        Op("invertmatrix", () =>
        {
            var dest = PopArray();
            StoreMatrix(dest, Inverse(ToMatrix(PopArray())));
            Push(dest);
        });
        Op("translate", () =>
        {
            PsArray? target = Peek() is PsArray t ? (PsArray)Pop() : null;
            double ty = PopNum(), tx = PopNum();
            var m = new Matrix(1, 0, 0, 1, tx, ty);
            if (target != null) { StoreMatrix(target, m); Push(target); } else _gs.Ctm = Matrix.Multiply(m, _gs.Ctm);
        });
        Op("scale", () =>
        {
            PsArray? target = Peek() is PsArray t ? (PsArray)Pop() : null;
            double sy = PopNum(), sx = PopNum();
            var m = new Matrix(sx, 0, 0, sy, 0, 0);
            if (target != null) { StoreMatrix(target, m); Push(target); } else _gs.Ctm = Matrix.Multiply(m, _gs.Ctm);
        });
        Op("rotate", () =>
        {
            PsArray? target = Peek() is PsArray t ? (PsArray)Pop() : null;
            double a = PopNum() * Math.PI / 180;
            var m = new Matrix(Math.Cos(a), Math.Sin(a), -Math.Sin(a), Math.Cos(a), 0, 0);
            if (target != null) { StoreMatrix(target, m); Push(target); } else _gs.Ctm = Matrix.Multiply(m, _gs.Ctm);
        });
        Op("transform", () => TransformOp((m, x, y) => m.Transform(new Point(x, y)), inverse: false));
        Op("itransform", () => TransformOp((m, x, y) => m.Transform(new Point(x, y)), inverse: true));
        Op("dtransform", () => TransformOp((m, x, y) => (Point)m.Transform(new Vector(x, y)), inverse: false));
        Op("idtransform", () => TransformOp((m, x, y) => (Point)m.Transform(new Vector(x, y)), inverse: true));

        // Path
        Op("newpath", () => _gs.Path = new PsPath());
        Op("moveto", () => { double y = PopNum(), x = PopNum(); MoveTo(UserToDevice(x, y)); });
        Op("rmoveto", () => { double dy = PopNum(), dx = PopNum(); var p = CurrentUserPoint(); MoveTo(UserToDevice(p.X + dx, p.Y + dy)); });
        Op("lineto", () => { double y = PopNum(), x = PopNum(); LineTo(UserToDevice(x, y)); });
        Op("rlineto", () => { double dy = PopNum(), dx = PopNum(); var p = CurrentUserPoint(); LineTo(UserToDevice(p.X + dx, p.Y + dy)); });
        Op("curveto", () =>
        {
            double y3 = PopNum(), x3 = PopNum(), y2 = PopNum(), x2 = PopNum(), y1 = PopNum(), x1 = PopNum();
            CurveTo(UserToDevice(x1, y1), UserToDevice(x2, y2), UserToDevice(x3, y3));
        });
        Op("rcurveto", () =>
        {
            double y3 = PopNum(), x3 = PopNum(), y2 = PopNum(), x2 = PopNum(), y1 = PopNum(), x1 = PopNum();
            var p = CurrentUserPoint();
            CurveTo(UserToDevice(p.X + x1, p.Y + y1), UserToDevice(p.X + x2, p.Y + y2), UserToDevice(p.X + x3, p.Y + y3));
        });
        Op("closepath", ClosePath);
        Op("arc", () => { double a2 = PopNum(), a1 = PopNum(), r = PopNum(), y = PopNum(), x = PopNum(); Arc(x, y, r, a1, a2, false); });
        Op("arcn", () => { double a2 = PopNum(), a1 = PopNum(), r = PopNum(), y = PopNum(), x = PopNum(); Arc(x, y, r, a1, a2, true); });
        Op("arct", () => { double r = PopNum(), y2 = PopNum(), x2 = PopNum(), y1 = PopNum(), x1 = PopNum(); ArcTo(x1, y1, x2, y2, r); });
        Op("arcto", () =>
        {
            double r = PopNum(), y2 = PopNum(), x2 = PopNum(), y1 = PopNum(), x1 = PopNum();
            var (a, b, c, d) = ArcTo(x1, y1, x2, y2, r);
            Push(a); Push(b); Push(c); Push(d);
        });
        Op("currentpoint", () => { var p = CurrentUserPoint(); Push(p.X); Push(p.Y); });
        Op("flattenpath", () => { });
        Op("reversepath", () => { });
        Op("strokepath", () =>
        {
            var pen = UserPen(out var ctm);
            if (pen == null || _gs.Path.IsEmpty) return;
            var user = _gs.Path.ToGeometry(FillRule.Nonzero).Clone();
            user.Transform = new MatrixTransform(Inverse(ctm));
            var outline = user.GetWidenedPathGeometry(pen);
            _gs.Path = new PsPath();
            _gs.Path.Append(outline, ctm);
        });
        Op("pathbbox", () =>
        {
            var r = _gs.Path.Bounds();
            if (r.IsEmpty) throw new PsError("nocurrentpoint");
            var inv = Inverse(_gs.Ctm);
            var ur = Rect.Transform(r, inv);
            Push(ur.Left); Push(ur.Top); Push(ur.Right); Push(ur.Bottom);
        });
        Op("pathforall", () =>
        {
            var close = PopProc(); var curve = PopProc(); var line = PopProc(); var move = PopProc();
            var inv = Inverse(_gs.Ctm);
            var figures = _gs.Path.Clone().Figures;
            Loop(() =>
            {
                foreach (var f in figures)
                {
                    var s = inv.Transform(f.Start); Push(s.X); Push(s.Y); Execute(move);
                    foreach (var seg in f.Segments)
                    {
                        if (seg.Curve)
                        {
                            foreach (var p in new[] { seg.P1, seg.P2, seg.P3 }) { var u = inv.Transform(p); Push(u.X); Push(u.Y); }
                            Execute(curve);
                        }
                        else { var u = inv.Transform(seg.P3); Push(u.X); Push(u.Y); Execute(line); }
                    }
                    if (f.Closed) Execute(close);
                }
            });
        });
        Op("clippath", () =>
        {
            _gs.Path = new PsPath();
            if (_gs.Clip != null) _gs.Path.Append(_gs.Clip, Matrix.Identity);
            else
            {
                var r = PageDeviceRect;
                MoveTo(r.TopLeft); LineTo(r.TopRight); LineTo(r.BottomRight); LineTo(r.BottomLeft); ClosePath();
            }
        });

        // Painting
        Op("fill", () => FillPath(FillRule.Nonzero));
        Op("eofill", () => FillPath(FillRule.EvenOdd));
        Op("stroke", StrokePath);
        Op("clip", () => ClipPath(FillRule.Nonzero));
        Op("eoclip", () => ClipPath(FillRule.EvenOdd));
        Op("initclip", () => { _gs.Clip = null; });
        Op("clipsave", () => _clipSaves.Push((_gstack.Count, _gs.ClipPushes, _gs.Clip)));
        Op("cliprestore", () =>
        {
            if (_clipSaves.Count == 0) return;
            var (level, pushes, clip) = _clipSaves.Pop();
            if (level != _gstack.Count) return; // saved in another gsave level: leave the clip alone
            for (int i = pushes; i < _gs.ClipPushes; i++) _dc.Pop();
            _gs.ClipPushes = Math.Min(_gs.ClipPushes, pushes);
            _gs.Clip = clip;
        });
        Op("rectfill", () => RectOp(() => FillPath(FillRule.Nonzero)));
        Op("rectstroke", () =>
        {
            if (Peek() is PsArray m && m.Length == 6 && Ps.IsNum(m[0]) && _stack.Count >= 5 && Ps.IsNum(Peek(1)) && Ps.IsNum(Peek(4))) Pop(); // optional matrix
            RectOp(StrokePath);
        });
        Op("rectclip", () => RectOp(() => { ClipPath(FillRule.Nonzero); _gs.Path = new PsPath(); }));
        Op("shfill", () =>
        {
            var shading = PopDict();
            var clip = _gs.Clip ?? new RectangleGeometry(PageDeviceRect);
            var brush = ShadingBrush(shading, _gs.Ctm);
            if (brush != null) _dc.DrawGeometry(brush, null, clip);
        });

        // Line attributes
        Op("setlinewidth", () => _gs.LineWidth = Math.Abs(PopNum()));
        Op("currentlinewidth", () => Push(_gs.LineWidth));
        Op("setlinecap", () => _gs.Cap = PopInt() switch { 1 => PenLineCap.Round, 2 => PenLineCap.Square, _ => PenLineCap.Flat });
        Op("currentlinecap", () => Push(_gs.Cap switch { PenLineCap.Round => 1L, PenLineCap.Square => 2L, _ => 0L }));
        Op("setlinejoin", () => _gs.Join = PopInt() switch { 1 => PenLineJoin.Round, 2 => PenLineJoin.Bevel, _ => PenLineJoin.Miter });
        Op("currentlinejoin", () => Push(_gs.Join switch { PenLineJoin.Round => 1L, PenLineJoin.Bevel => 2L, _ => 0L }));
        Op("setmiterlimit", () => _gs.MiterLimit = PopNum());
        Op("currentmiterlimit", () => Push(_gs.MiterLimit));
        Op("setdash", () =>
        {
            double offset = PopNum();
            var a = PopArray();
            _gs.Dash = Enumerable.Range(0, a.Length).Select(i => Ps.Num(a[i])).ToArray();
            _gs.DashOffset = offset;
        });
        Op("currentdash", () => { Push(new PsArray(_gs.Dash.Select(d => (object)d).ToArray())); Push(_gs.DashOffset); });
        foreach (var n in new[] { "setflat", "setsmoothness", "setstrokeadjust", "setoverprint", "setoverprintmode", "setrenderingintent", "setblackgeneration", "setundercolorremoval", "settransfer", "setcolorrendering", "sethalftone" })
            Op(n, () => Pop());
        Op("setscreen", () => { Pop(); Pop(); Pop(); });
        Op("setcolortransfer", () => { Pop(); Pop(); Pop(); Pop(); });
        Op("setcolorscreen", () => { for (int i = 0; i < 12; i++) Pop(); });
        Op("currentflat", () => Push(1.0));
        Op("currentsmoothness", () => Push(0.02));
        Op("currentstrokeadjust", () => Push(false));
        Op("currentoverprint", () => Push(false));
        Op("currentscreen", () => { Push(60.0); Push(45.0); Push(new PsArray(new object[] { 0.0 }, exec: true)); });
        Op("currenttransfer", () => Push(new PsArray(Array.Empty<object>(), exec: true)));
        Op("currentblackgeneration", () => Push(new PsArray(Array.Empty<object>(), exec: true)));
        Op("currentundercolorremoval", () => Push(new PsArray(Array.Empty<object>(), exec: true)));
        Op("currenthalftone", () => Push(new PsDict()));
        Op("currentcolorrendering", () => Push(new PsDict()));

        // Color
        Op("setgray", () => { double g = PopNum(); _gs.ColorSpace = "DeviceGray"; _gs.ColorComponents = 1; _gs.ColorValues = new[] { g }; SetColor(Rgb(g, g, g)); });
        Op("setrgbcolor", () => { double b = PopNum(), g = PopNum(), r = PopNum(); _gs.ColorSpace = "DeviceRGB"; _gs.ColorComponents = 3; _gs.ColorValues = new[] { r, g, b }; SetColor(Rgb(r, g, b)); });
        Op("setcmykcolor", () =>
        {
            double k = PopNum(), y = PopNum(), m = PopNum(), c = PopNum();
            _gs.ColorSpace = "DeviceCMYK"; _gs.ColorComponents = 4; _gs.ColorValues = new[] { c, m, y, k };
            SetColor(Cmyk(c, m, y, k));
        });
        Op("sethsbcolor", () => { double b = PopNum(), s = PopNum(), h = PopNum(); var c = Hsb(h, s, b); _gs.ColorSpace = "DeviceRGB"; _gs.ColorComponents = 3; _gs.ColorValues = new[] { c.R / 255.0, c.G / 255.0, c.B / 255.0 }; SetColor(c); });
        Op("currentgray", () => { var c = _gs.Color; Push((0.3 * c.R + 0.59 * c.G + 0.11 * c.B) / 255); });
        Op("currentrgbcolor", () => { var c = _gs.Color; Push(c.R / 255.0); Push(c.G / 255.0); Push(c.B / 255.0); });
        Op("currentcmykcolor", () =>
        {
            if (SpaceFamily(_gs.ColorSpace) == "DeviceCMYK") { foreach (var v in _gs.ColorValues) Push(v); return; }
            var c = _gs.Color;
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, k = 1 - Math.Max(r, Math.Max(g, b));
            Push(k >= 1 ? 0.0 : (1 - r - k) / (1 - k)); Push(k >= 1 ? 0.0 : (1 - g - k) / (1 - k)); Push(k >= 1 ? 0.0 : (1 - b - k) / (1 - k)); Push(k);
        });
        Op("currenthsbcolor", () => { var c = _gs.Color; var col = System.Drawing.Color.FromArgb(c.R, c.G, c.B); Push(col.GetHue() / 360.0); Push((double)col.GetSaturation()); Push((double)col.GetBrightness()); });
        Op("setcolorspace", () =>
        {
            var s = Pop();
            SetColorSpace(s is PsName n ? n.Value : s);
        });
        Op("currentcolorspace", () => Push(_gs.ColorSpace is string s ? new PsArray(new object[] { Name(s) }) : _gs.ColorSpace));
        Op("setcolor", () =>
        {
            if (SpaceFamily(_gs.ColorSpace) == "Pattern")
            {
                var p = Pop();
                if (p is PsDict pattern) { _gs.Pattern = pattern; return; }
                Push(p);
            }
            int n = _gs.ColorComponents;
            var v = new double[n];
            for (int i = n - 1; i >= 0; i--) v[i] = PopNum();
            _gs.ColorValues = v;
            SetColor(ColorFrom(_gs.ColorSpace, v));
        });
        Op("currentcolor", () => { foreach (var v in _gs.ColorValues) Push(v); });
        Op("makepattern", () =>
        {
            var m = ToMatrix(PopArray());
            var proto = PopDict();
            var pattern = new PsDict(proto.Map.Count + 2);
            foreach (var kv in proto.Map) pattern.Map[kv.Key] = kv.Value;
            // The pattern space is fixed when the pattern is made: its matrix times the CTM at that moment
            pattern.Put("__Matrix", NewMatrix(Matrix.Multiply(m, _gs.Ctm)));
            Push(pattern);
        });
        Op("setpattern", () =>
        {
            var p = Pop();
            _gs.ColorSpace = new PsArray(new object[] { Name("Pattern") });
            _gs.ColorComponents = 1;
            if (p is PsDict pattern) _gs.Pattern = pattern;
        });

        // Images
        Op("image", () =>
        {
            if (Peek() is PsDict d) { Pop(); ImageFromDict(d, mask: false); return; }
            var src = Pop(); var matrix = PopArray(); long bits = PopInt(); long h = PopInt(); long w = PopInt();
            DrawImage((int)w, (int)h, (int)bits, ToMatrix(matrix), new[] { ToSource(src) }, "DeviceGray", 1, null, mask: false, maskPolarity: false);
        });
        Op("imagemask", () =>
        {
            if (Peek() is PsDict d) { Pop(); ImageFromDict(d, mask: true); return; }
            var src = Pop(); var matrix = PopArray(); bool polarity = PopBool(); long h = PopInt(); long w = PopInt();
            DrawImage((int)w, (int)h, 1, ToMatrix(matrix), new[] { ToSource(src) }, "DeviceGray", 1, null, mask: true, maskPolarity: polarity);
        });
        Op("colorimage", () =>
        {
            long ncomp = PopInt(); bool multi = PopBool();
            var sources = new IByteSource[multi ? ncomp : 1];
            for (int i = sources.Length - 1; i >= 0; i--) sources[i] = ToSource(Pop());
            var matrix = PopArray(); long bits = PopInt(); long h = PopInt(); long w = PopInt();
            string space = ncomp switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" };
            DrawImage((int)w, (int)h, (int)bits, ToMatrix(matrix), sources, space, (int)ncomp, null, mask: false, maskPolarity: false);
        });
        Op("execform", () =>
        {
            var form = PopDict();
            SaveGState();
            try
            {
                if (form.Get("Matrix") is PsArray m) _gs.Ctm = Matrix.Multiply(ToMatrix(m), _gs.Ctm);
                if (form.Get("BBox") is PsArray bb && bb.Length == 4)
                {
                    double x0 = Ps.Num(bb[0]), y0 = Ps.Num(bb[1]), x1 = Ps.Num(bb[2]), y1 = Ps.Num(bb[3]);
                    _gs.Path = new PsPath();
                    MoveTo(UserToDevice(x0, y0)); LineTo(UserToDevice(x1, y0)); LineTo(UserToDevice(x1, y1)); LineTo(UserToDevice(x0, y1)); ClosePath();
                    ClipPath(FillRule.Nonzero);
                    _gs.Path = new PsPath();
                }
                if (form.Get("PaintProc") is PsArray proc) { Push(form); Execute(proc); }
            }
            finally { GRestore(); }
        });
    }

    public Rect PageDeviceRect { get; set; }

    private void TransformOp(Func<Matrix, double, double, Point> f, bool inverse)
    {
        Matrix m = _gs.Ctm;
        if (Peek() is PsArray a) { Pop(); m = ToMatrix(a); }
        double y = PopNum(), x = PopNum();
        if (inverse) m = Inverse(m);
        var p = f(m, x, y);
        Push(p.X); Push(p.Y);
    }

    /// <summary>rectfill / rectstroke / rectclip: x y w h, or an array of such quadruples.</summary>
    private void RectOp(Action paint)
    {
        var top = Pop();
        var rects = new List<(double, double, double, double)>();
        if (top is PsArray a)
            for (int i = 0; i + 3 < a.Length; i += 4) rects.Add((Ps.Num(a[i]), Ps.Num(a[i + 1]), Ps.Num(a[i + 2]), Ps.Num(a[i + 3])));
        else if (top is PsString)
            throw new PsError("typecheck", "encoded number strings not supported");
        else
        {
            double h = Ps.Num(top), w = PopNum(), y = PopNum(), x = PopNum();
            rects.Add((x, y, w, h));
        }
        var saved = _gs.Path;
        _gs.Path = new PsPath();
        foreach (var (x, y, w, h) in rects)
        {
            MoveTo(UserToDevice(x, y)); LineTo(UserToDevice(x + w, y)); LineTo(UserToDevice(x + w, y + h)); LineTo(UserToDevice(x, y + h)); ClosePath();
        }
        paint();
        _gs.Path = saved;
    }

    // --- Shadings and patterns ---

    private void PaintPattern(Geometry deviceGeometry, PsDict pattern)
    {
        var space = pattern.Get("__Matrix") is PsArray pm ? ToMatrix(pm) : _gs.Ctm;
        if (pattern.Get("PatternType") is long pt && pt == 2 && pattern.Get("Shading") is PsDict shading)
        {
            var brush = ShadingBrush(shading, space);
            if (brush != null) _dc.DrawGeometry(brush, null, deviceGeometry);
            return;
        }

        // Tiling pattern: run its PaintProc once into a tile and repeat it
        if (pattern.Get("PaintProc") is PsArray proc && pattern.Get("BBox") is PsArray bb && bb.Length == 4)
        {
            double x0 = Ps.Num(bb[0]), y0 = Ps.Num(bb[1]), x1 = Ps.Num(bb[2]), y1 = Ps.Num(bb[3]);
            double xs = pattern.Get("XStep") is object xo && Ps.IsNum(xo) ? Ps.Num(xo) : x1 - x0;
            double ys = pattern.Get("YStep") is object yo && Ps.IsNum(yo) ? Ps.Num(yo) : y1 - y0;
            if (Math.Abs(xs) < 1e-6 || Math.Abs(ys) < 1e-6) return;

            var tile = new DrawingGroup();
            var outerDc = _dc;
            var outerGs = _gs;
            var outerStack = _gstack.ToArray();
            using (var dc = tile.Open())
            {
                _dc = dc;
                _gs = new GState { Ctm = Matrix.Identity };
                if (pattern.Get("PaintType") is long paintType && paintType == 2) SetColor(outerGs.Color);
                try { Push(pattern); Execute(proc); }
                catch (PsError) { }
                finally
                {
                    for (int i = 0; i < _gs.ClipPushes; i++) _dc.Pop();
                    while (_gstack.Count > outerStack.Length) { var g = _gstack.Pop(); for (int i = 0; i < g.ClipPushes; i++) _dc.Pop(); }
                    _dc = outerDc;
                    _gs = outerGs;
                }
            }
            var brush = new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile,
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(x0, y0, Math.Abs(xs), Math.Abs(ys)),
                Viewport = new Rect(x0, y0, Math.Abs(xs), Math.Abs(ys)),
                Stretch = Stretch.None,
                Transform = new MatrixTransform(space),
            };
            brush.Freeze();
            _dc.DrawGeometry(brush, null, deviceGeometry);
            return;
        }
        _dc.DrawGeometry(BrushFor(Colors.Gray), null, deviceGeometry);
    }

    /// <summary>Axial and radial shadings become WPF gradients, sampled from the shading's color function.</summary>
    private Brush? ShadingBrush(PsDict shading, Matrix space)
    {
        long type = shading.Get("ShadingType") is long t ? t : 0;
        var cs = shading.Get("ColorSpace") is object c ? (c is PsName n ? n.Value : c) : "DeviceRGB";
        if (shading.Get("Coords") is not PsArray coords) return null;
        var fn = shading.Get("Function");
        double d0 = 0, d1 = 1;
        if (shading.Get("Domain") is PsArray dom && dom.Length >= 2) { d0 = Ps.Num(dom[0]); d1 = Ps.Num(dom[1]); }

        var stops = new GradientStopCollection();
        const int samples = 48;
        for (int i = 0; i <= samples; i++)
        {
            double tt = d0 + (d1 - d0) * i / samples;
            var comps = EvalFunction(fn, tt, ComponentsOf(cs));
            stops.Add(new GradientStop(ColorFrom(cs, comps), (double)i / samples));
        }

        bool extend = shading.Get("Extend") is PsArray ex && ex.Length > 0 && ex[0] is true;
        GradientBrush brush;
        if (type == 2 && coords.Length >= 4)
        {
            brush = new LinearGradientBrush(stops, new Point(Ps.Num(coords[0]), Ps.Num(coords[1])), new Point(Ps.Num(coords[2]), Ps.Num(coords[3])))
            { MappingMode = BrushMappingMode.Absolute };
        }
        else if (type == 3 && coords.Length >= 6)
        {
            double r1 = Ps.Num(coords[5]);
            brush = new RadialGradientBrush(stops)
            {
                MappingMode = BrushMappingMode.Absolute,
                GradientOrigin = new Point(Ps.Num(coords[0]), Ps.Num(coords[1])),
                Center = new Point(Ps.Num(coords[3]), Ps.Num(coords[4])),
                RadiusX = r1,
                RadiusY = r1,
            };
        }
        else
        {
            return BrushFor(stops[stops.Count / 2].Color);
        }
        brush.SpreadMethod = GradientSpreadMethod.Pad;
        _ = extend;
        brush.Transform = new MatrixTransform(space);
        brush.Freeze();
        return brush;
    }

    /// <summary>PostScript/PDF functions: 2 (exponential), 3 (stitching), 4 (calculator), 0 (sampled), or an array of them.</summary>
    private double[] EvalFunction(object? fn, double x, int outputs)
    {
        var result = new double[outputs];
        try
        {
            switch (fn)
            {
                case PsArray arr when !arr.Exec:
                    for (int i = 0; i < Math.Min(arr.Length, outputs); i++) result[i] = EvalFunction(arr[i], x, 1)[0];
                    return result;
                case PsArray proc:
                    Push(x);
                    Execute(proc);
                    for (int i = outputs - 1; i >= 0; i--) result[i] = PopNum();
                    return result;
                case PsDict d:
                    long type = d.Get("FunctionType") is long t ? t : 2;
                    if (d.Get("Domain") is PsArray dom && dom.Length >= 2) x = Math.Clamp(x, Ps.Num(dom[0]), Ps.Num(dom[1]));
                    switch (type)
                    {
                        case 2:
                            {
                                var c0 = d.Get("C0") as PsArray; var c1 = d.Get("C1") as PsArray;
                                double n = d.Get("N") is object no && Ps.IsNum(no) ? Ps.Num(no) : 1;
                                double xn = Math.Pow(x, n);
                                for (int i = 0; i < outputs; i++)
                                {
                                    double a = c0 != null && i < c0.Length ? Ps.Num(c0[i]) : 0;
                                    double b = c1 != null && i < c1.Length ? Ps.Num(c1[i]) : 1;
                                    result[i] = a + xn * (b - a);
                                }
                                return result;
                            }
                        case 3:
                            {
                                var fns = (PsArray)d.Get("Functions")!;
                                var bounds = d.Get("Bounds") as PsArray;
                                var encode = d.Get("Encode") as PsArray;
                                var domain = d.Get("Domain") as PsArray;
                                double lo = domain != null ? Ps.Num(domain[0]) : 0, hi = domain != null ? Ps.Num(domain[1]) : 1;
                                int k = 0;
                                while (bounds != null && k < bounds.Length && x >= Ps.Num(bounds[k])) k++;
                                k = Math.Min(k, fns.Length - 1);
                                double b0 = k == 0 ? lo : Ps.Num(bounds![k - 1]);
                                double b1 = bounds != null && k < bounds.Length ? Ps.Num(bounds[k]) : hi;
                                double e0 = encode != null ? Ps.Num(encode[2 * k]) : 0, e1 = encode != null ? Ps.Num(encode[2 * k + 1]) : 1;
                                double xe = b1 == b0 ? e0 : e0 + (x - b0) * (e1 - e0) / (b1 - b0);
                                return EvalFunction(fns[k], xe, outputs);
                            }
                        case 4 when d.Get("Function") is PsArray calc:
                            return EvalFunction(calc, x, outputs);
                        case 0:
                            return EvalSampled(d, x, outputs);
                        default:
                            {
                                // Sampled or unknown: the range midpoint is better than nothing
                                if (d.Get("Range") is PsArray range)
                                    for (int i = 0; i < outputs && 2 * i + 1 < range.Length; i++) result[i] = (Ps.Num(range[2 * i]) + Ps.Num(range[2 * i + 1])) / 2;
                                return result;
                            }
                    }
            }
        }
        catch (PsError) { }
        return result;
    }

    private readonly Dictionary<PsDict, double[]> _sampleCache = new();

    /// <summary>Sampled function (type 0), one input: a table of samples, linearly interpolated.</summary>
    private double[] EvalSampled(PsDict d, double x, int outputs)
    {
        var result = new double[outputs];
        if (d.Get("Size") is not PsArray size || size.Length < 1) return result;
        int count = (int)Ps.Int(size[0]);
        var range = d.Get("Range") as PsArray;
        int n = range != null ? range.Length / 2 : outputs;
        int bps = d.Get("BitsPerSample") is long b ? (int)b : 8;
        if (count < 1 || n < 1) return result;

        if (!_sampleCache.TryGetValue(d, out var samples))
        {
            samples = new double[count * n];
            var reader = new BitReader(ToSource(d.Get("DataSource") ?? new PsString(Array.Empty<byte>())));
            double max = (1L << bps) - 1;
            for (int i = 0; i < samples.Length; i++) samples[i] = reader.Read(bps) / max;
            _sampleCache[d] = samples;
        }

        double d0 = 0, d1 = 1;
        if (d.Get("Domain") is PsArray dom && dom.Length >= 2) { d0 = Ps.Num(dom[0]); d1 = Ps.Num(dom[1]); }
        double e0 = 0, e1 = count - 1;
        if (d.Get("Encode") is PsArray enc && enc.Length >= 2) { e0 = Ps.Num(enc[0]); e1 = Ps.Num(enc[1]); }
        double e = d1 == d0 ? e0 : e0 + (x - d0) * (e1 - e0) / (d1 - d0);
        e = Math.Clamp(e, 0, count - 1);
        int i0 = (int)Math.Floor(e), i1 = Math.Min(i0 + 1, count - 1);
        double t = e - i0;

        var decode = d.Get("Decode") as PsArray ?? range;
        for (int k = 0; k < Math.Min(n, outputs); k++)
        {
            double s = samples[i0 * n + k] * (1 - t) + samples[i1 * n + k] * t;
            if (decode != null && decode.Length >= 2 * k + 2)
                s = Ps.Num(decode[2 * k]) + s * (Ps.Num(decode[2 * k + 1]) - Ps.Num(decode[2 * k]));
            result[k] = s;
        }
        return result;
    }

    // --- Images ---

    private void ImageFromDict(PsDict d, bool mask)
    {
        long type = d.Get("ImageType") is long t ? t : 1;
        if (type == 3 && d.Get("DataDict") is PsDict data) { ImageFromDict(data, mask); return; }
        int w = (int)Ps.Int(d.Get("Width") ?? throw new PsError("rangecheck"));
        int h = (int)Ps.Int(d.Get("Height") ?? throw new PsError("rangecheck"));
        int bits = d.Get("BitsPerComponent") is long b ? (int)b : 1;
        var matrix = ToMatrix((PsArray)(d.Get("ImageMatrix") ?? throw new PsError("rangecheck")));
        object space = mask ? "DeviceGray" : _gs.ColorSpace;
        int ncomp = mask ? 1 : ComponentsOf(space);
        IByteSource[] sources;
        if (d.Get("MultipleDataSources") is true && d.Get("DataSource") is PsArray multi)
            sources = Enumerable.Range(0, multi.Length).Select(i => ToSource(multi[i])).ToArray();
        else
            sources = new[] { ToSource(d.Get("DataSource") ?? throw new PsError("rangecheck")) };
        double[]? decode = d.Get("Decode") is PsArray dec ? Enumerable.Range(0, dec.Length).Select(i => Ps.Num(dec[i])).ToArray() : null;
        bool polarity = decode != null && decode.Length >= 2 && decode[0] > decode[1]; // [1 0] = paint where bits are 1
        DrawImage(w, h, bits, matrix, sources, space, ncomp, decode, mask, mask ? polarity : false);
    }

    private void DrawImage(int w, int h, int bits, Matrix imageMatrix, IByteSource[] sources, object space, int ncomp,
        double[]? decode, bool mask, bool maskPolarity)
    {
        if (w <= 0 || h <= 0 || (long)w * h > 60_000_000) throw new PsError("limitcheck", "image size");
        if (bits is not (1 or 2 or 4 or 8 or 12 or 16)) throw new PsError("rangecheck", "bits per component");

        var pixels = new byte[w * h * 4];
        int maxValue = (1 << bits) - 1;
        string family = SpaceFamily(space);
        bool indexed = family == "Indexed";
        var readers = sources.Select(s => new BitReader(s)).ToArray();
        var comps = new double[ncomp];
        var maskColor = _gs.Color;
        bool fast8 = !mask && bits == 8 && decode == null && family is "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "ICCBased" or "CalRGB" or "CalGray";

        // Colors of an Indexed / spot image are looked up once per distinct sample
        var cache = new Dictionary<long, Color>();

        for (int y = 0; y < h; y++)
        {
            foreach (var r in readers) r.AlignToByte();
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (mask)
                {
                    int bit = readers[0].Read(1);
                    bool paint = maskPolarity ? bit == 1 : bit == 0;
                    if (paint) { pixels[o] = maskColor.B; pixels[o + 1] = maskColor.G; pixels[o + 2] = maskColor.R; pixels[o + 3] = 255; }
                    continue;
                }

                long key = 0;
                for (int c = 0; c < ncomp; c++)
                {
                    var reader = readers.Length > 1 ? readers[Math.Min(c, readers.Length - 1)] : readers[0];
                    int raw = reader.Read(bits);
                    key = key * 65537 + raw;
                    double v;
                    if (decode != null && decode.Length >= 2 * c + 2)
                        v = decode[2 * c] + raw * (decode[2 * c + 1] - decode[2 * c]) / maxValue;
                    else
                        v = indexed ? raw : (double)raw / maxValue;
                    comps[c] = v;
                }

                Color color;
                if (fast8)
                    color = ncomp switch
                    {
                        1 => Rgb(comps[0], comps[0], comps[0]),
                        4 => Cmyk(comps[0], comps[1], comps[2], comps[3]),
                        _ => Rgb(comps[0], comps[1], comps[2]),
                    };
                else if (!cache.TryGetValue(key, out color))
                {
                    color = ColorFrom(space, comps);
                    if (cache.Count < 65536) cache[key] = color;
                }
                pixels[o] = color.B; pixels[o + 1] = color.G; pixels[o + 2] = color.R; pixels[o + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();

        // The image fills the unit square of the image space; ImageMatrix maps user space to it
        var toDevice = Matrix.Multiply(Inverse(imageMatrix), _gs.Ctm);
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, w * h < 250_000 && !mask ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var gdc = group.Open()) gdc.DrawImage(bmp, new Rect(0, 0, w, h));
        group.Transform = new MatrixTransform(toDevice);
        group.Freeze();
        _dc.DrawDrawing(group);
        PaintCount++;
    }

    private sealed class BitReader
    {
        private readonly IByteSource _src;
        private int _buf, _left;
        public BitReader(IByteSource src) => _src = src;

        public void AlignToByte() => _left = 0;

        public int Read(int bits)
        {
            if (bits == 8) { _left = 0; int b = _src.Read(); return b < 0 ? 0 : b; }
            if (bits == 16) { _left = 0; int hi = _src.Read(), lo = _src.Read(); return hi < 0 ? 0 : (hi << 8) | Math.Max(lo, 0); }
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                if (_left == 0)
                {
                    _buf = _src.Read();
                    if (_buf < 0) _buf = 0;
                    _left = 8;
                }
                _left--;
                value = (value << 1) | ((_buf >> _left) & 1);
            }
            return value;
        }
    }
}
