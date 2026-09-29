using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace QuickPeek.Core.PostScript;

/// <summary>
/// A PostScript interpreter written for QuickPeek: enough of the language (Level 2/3 core) to run the
/// prologs that Illustrator, CorelDRAW, Inkscape, Photoshop, etc. put into EPS files, and to draw them.
/// This part holds the execution model and the non-graphics operators; drawing is in PsInterpreter.Graphics.cs.
/// </summary>
internal sealed partial class PsInterpreter
{
    private readonly List<object> _stack = new();
    private readonly List<PsDict> _dicts = new();
    private readonly Stack<IByteSource> _files = new(); // currentfile = the source being executed
    private readonly PsDict _systemDict = new(512);
    private readonly PsDict _globalDict = new(64);
    private readonly PsDict _userDict = new(256);
    private readonly PsDict _errorDict = new(32);
    private readonly PsDict _dollarError = new(16);
    private readonly PsDict _fontDirectory = new(64);
    private readonly Dictionary<string, PsDict> _resources = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _timeLimit;
    private long _ops;
    private readonly List<Dictionary<PsDict, Dictionary<object, object>>> _vmSaves = new();
    private int _depth;

    public int ErrorCount { get; private set; }
    public string? FirstError { get; private set; }
    public readonly List<string> ErrorLog = new();
    private string _lastName = "";

    public PsInterpreter(TimeSpan timeLimit)
    {
        _timeLimit = timeLimit;
        _dicts.Add(_systemDict);
        _dicts.Add(_globalDict);
        _dicts.Add(_userDict);
        PsDict.BeforeWrite = Journal;
        RegisterCore();
        RegisterGraphics();
        RegisterFonts();
    }

    /// <summary>First change to a dictionary since the last save: remember its contents.</summary>
    private void Journal(PsDict d)
    {
        if (_vmSaves.Count == 0) return;
        var journal = _vmSaves[^1];
        if (!journal.ContainsKey(d)) journal[d] = new Dictionary<object, object>(d.Map);
    }

    // --- Stack ---

    public void Push(object o) => _stack.Add(o);

    public object Pop()
    {
        if (_stack.Count == 0) throw new PsError("stackunderflow");
        var o = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return o;
    }

    public object Peek(int fromTop = 0)
    {
        if (_stack.Count <= fromTop) throw new PsError("stackunderflow");
        return _stack[_stack.Count - 1 - fromTop];
    }

    private double PopNum() => Ps.Num(Pop());
    private long PopInt() => Ps.Int(Pop());
    private bool PopBool() => Pop() is bool b ? b : throw new PsError("typecheck", "boolean expected");
    private PsDict PopDict() => Pop() is PsDict d ? d : throw new PsError("typecheck", "dictionary expected");
    private PsArray PopArray() => Pop() is PsArray a ? a : throw new PsError("typecheck", "array expected");
    private PsString PopString() => Pop() is PsString s ? s : throw new PsError("typecheck", "string expected");
    private PsArray PopProc() => PopArray();

    private static object Number(double d) => d;
    private static object IntOrReal(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < long.MaxValue / 2 ? (long)d : d;

    // --- Execution ---

    private void Tick()
    {
        if ((++_ops & 0xFFF) == 0 && _clock.Elapsed > _timeLimit)
            throw new PsQuit("time limit");
    }

    public object Lookup(string name)
    {
        for (int i = _dicts.Count - 1; i >= 0; i--)
            if (_dicts[i].Map.TryGetValue(name, out var v)) return v;
        throw new PsError("undefined", name);
    }

    private PsDict? Where(object key)
    {
        object k = PsDict.Key(key);
        for (int i = _dicts.Count - 1; i >= 0; i--)
            if (_dicts[i].Map.ContainsKey(k)) return _dicts[i];
        return null;
    }

    private PsDict CurrentDict => _dicts[^1];

    /// <summary>Executes an object the way the interpreter does when it meets it by name or via exec.</summary>
    public void Execute(object obj)
    {
        Tick();
        switch (obj)
        {
            case PsOperator op:
                op.Fn();
                break;
            case PsName { Exec: true } n:
                _lastName = n.Value;
                Execute(Lookup(n.Value));
                break;
            case PsArray { Exec: true } proc:
                RunProc(proc);
                break;
            case PsString { Exec: true } s:
                RunSource(new BytesSource(s.Span.ToArray()));
                break;
            case PsFile { Exec: true } f:
                RunSource(f.Source);
                break;
            default:
                Push(obj);
                break;
        }
    }

    private void RunProc(PsArray proc)
    {
        if (++_depth > 1500) { _depth--; throw new PsError("execstackoverflow"); }
        try
        {
            for (int i = 0; i < proc.Length; i++)
            {
                var e = proc[i];
                switch (e)
                {
                    case PsName { Exec: true } n:
                        Tick();
                        _lastName = n.Value;
                        Execute(Lookup(n.Value));
                        break;
                    case PsOperator op:
                        Tick();
                        op.Fn();
                        break;
                    default:
                        Push(e); // literals and nested procedures
                        break;
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Runs a program (or a nested one: eexec, run, cvx exec of a string) token by token.</summary>
    public void RunSource(IByteSource source, bool topLevel = false)
    {
        var lexer = new PsLexer(source);
        _files.Push(source);
        try
        {
            string lastToken = "";
            while (!source.Closed)
            {
                object? token;
                try
                {
                    token = lexer.Next();
                    if (token == null) break;
                    Tick();
                    lastToken = token.ToString() ?? "";
                    if (token is PsName { Exec: true } n) { _lastName = n.Value; Execute(Lookup(n.Value)); }
                    else Push(token);
                }
                catch (PsError e) when (topLevel)
                {
                    // A real printer would abort the job; for a preview it's better to skip the failing
                    // operator and keep drawing whatever else the file contains
                    ErrorCount++;
                    FirstError ??= e.Message;
                    if (ErrorLog.Count < 25) ErrorLog.Add($"{e.Message} (in '{_lastName}', token '{lastToken}')");
                    if (ErrorCount > 500) throw new PsQuit("too many errors");
                }
                catch (PsExit) when (topLevel) { }
                catch (PsStop) when (topLevel) { }
            }
        }
        finally
        {
            _files.Pop();
        }
    }

    private void Loop(Action body)
    {
        try { body(); }
        catch (PsExit) { }
    }

    // --- Registration helpers ---

    private void Op(string name, Action fn) => _systemDict.Put(name, new PsOperator(name, fn));

    private static PsName Name(string s) => new(s, false);
    private static PsName ExecName(string s) => new(s, true);

    private void RegisterCore()
    {
        _systemDict.Put("systemdict", _systemDict);
        _systemDict.Put("userdict", _userDict);
        _systemDict.Put("globaldict", _globalDict);
        _systemDict.Put("errordict", _errorDict);
        _systemDict.Put("$error", _dollarError);
        _systemDict.Put("statusdict", new PsDict());
        _systemDict.Put("FontDirectory", _fontDirectory);
        _systemDict.Put("GlobalFontDirectory", _fontDirectory);
        _systemDict.Put("SharedFontDirectory", _fontDirectory);
        _systemDict.Put("null", PsNull.Instance);
        _systemDict.Put("true", true);
        _systemDict.Put("false", false);
        _systemDict.Put("StandardEncoding", PsEncodings.StandardArray());
        _systemDict.Put("ISOLatin1Encoding", PsEncodings.IsoLatin1Array());
        _dollarError.Put("newerror", false);
        foreach (var err in new[] { "handleerror", "undefined", "typecheck", "rangecheck", "stackunderflow", "invalidfont", "ioerror", "undefinedresult", "invalidaccess", "limitcheck", "syntaxerror", "unmatchedmark", "undefinedfilename", "nocurrentpoint", "dictstackunderflow", "invalidrestore" })
            _errorDict.Put(err, new PsArray(Array.Empty<object>(), exec: true));

        // Stack
        Op("pop", () => Pop());
        Op("exch", () => { var a = Pop(); var b = Pop(); Push(a); Push(b); });
        Op("dup", () => Push(Peek()));
        Op("copy", () =>
        {
            var top = Pop();
            if (top is long n)
            {
                if (n < 0 || n > _stack.Count) throw new PsError("rangecheck");
                _stack.AddRange(_stack.GetRange(_stack.Count - (int)n, (int)n));
                return;
            }
            var src = Pop();
            switch (src, top)
            {
                case (PsArray a, PsArray b):
                    if (b.Length < a.Length) throw new PsError("rangecheck");
                    for (int i = 0; i < a.Length; i++) b[i] = a[i];
                    Push(new PsArray(b.Data, b.Offset, a.Length, b.Exec));
                    break;
                case (PsString a, PsString b):
                    if (b.Length < a.Length) throw new PsError("rangecheck");
                    a.Span.CopyTo(b.Data.AsSpan(b.Offset));
                    Push(new PsString(b.Data, b.Offset, a.Length));
                    break;
                case (PsDict a, PsDict b):
                    foreach (var kv in a.Map) b.Put(kv.Key, kv.Value);
                    Push(b);
                    break;
                default:
                    Push(top); // gstate copy etc.
                    break;
            }
        });
        Op("index", () => { long n = PopInt(); Push(Peek((int)n)); });
        Op("roll", () =>
        {
            long j = PopInt(), n = PopInt();
            if (n < 0 || n > _stack.Count) throw new PsError("rangecheck");
            if (n == 0) return;
            int start = _stack.Count - (int)n;
            var part = _stack.GetRange(start, (int)n);
            int shift = (int)(((j % n) + n) % n);
            for (int i = 0; i < n; i++) _stack[start + (int)((i + shift) % n)] = part[i];
        });
        Op("clear", () => _stack.Clear());
        Op("count", () => Push((long)_stack.Count));
        Op("mark", () => Push(PsMark.Instance));
        Op("[", () => Push(PsMark.Instance));
        Op("<<", () => Push(PsMark.Instance));
        Op("]", () => { var items = PopToMark(); Push(new PsArray(items)); });
        Op(">>", () =>
        {
            var items = PopToMark();
            var d = new PsDict(items.Length / 2 + 1);
            for (int i = 0; i + 1 < items.Length; i += 2) d.Put(items[i], items[i + 1]);
            Push(d);
        });
        Op("cleartomark", () => PopToMark());
        Op("counttomark", () => Push((long)CountToMark()));

        // Arithmetic
        Op("add", () => { var b = Pop(); var a = Pop(); Push(a is long x && b is long y ? x + y : Number(Ps.Num(a) + Ps.Num(b))); });
        Op("sub", () => { var b = Pop(); var a = Pop(); Push(a is long x && b is long y ? x - y : Number(Ps.Num(a) - Ps.Num(b))); });
        Op("mul", () => { var b = Pop(); var a = Pop(); Push(a is long x && b is long y ? x * y : Number(Ps.Num(a) * Ps.Num(b))); });
        Op("div", () => { double b = PopNum(), a = PopNum(); if (b == 0) throw new PsError("undefinedresult"); Push(a / b); });
        Op("idiv", () => { long b = PopInt(), a = PopInt(); if (b == 0) throw new PsError("undefinedresult"); Push(a / b); });
        Op("mod", () => { long b = PopInt(), a = PopInt(); if (b == 0) throw new PsError("undefinedresult"); Push(a % b); });
        Op("neg", () => { var a = Pop(); Push(a is long l ? -l : Number(-Ps.Num(a))); });
        Op("abs", () => { var a = Pop(); Push(a is long l ? Math.Abs(l) : Number(Math.Abs(Ps.Num(a)))); });
        Op("ceiling", () => { var a = Pop(); Push(a is long ? a : Number(Math.Ceiling(Ps.Num(a)))); });
        Op("floor", () => { var a = Pop(); Push(a is long ? a : Number(Math.Floor(Ps.Num(a)))); });
        Op("round", () => { var a = Pop(); Push(a is long ? a : Number(Math.Floor(Ps.Num(a) + 0.5))); });
        Op("truncate", () => { var a = Pop(); Push(a is long ? a : Number(Math.Truncate(Ps.Num(a)))); });
        Op("sqrt", () => Push(Math.Sqrt(PopNum())));
        Op("sin", () => Push(Math.Sin(PopNum() * Math.PI / 180)));
        Op("cos", () => Push(Math.Cos(PopNum() * Math.PI / 180)));
        Op("atan", () =>
        {
            double den = PopNum(), num = PopNum();
            double deg = Math.Atan2(num, den) * 180 / Math.PI;
            Push(deg < 0 ? deg + 360 : deg);
        });
        Op("exp", () => { double e = PopNum(), b = PopNum(); Push(Math.Pow(b, e)); });
        Op("ln", () => Push(Math.Log(PopNum())));
        Op("log", () => Push(Math.Log10(PopNum())));
        Op("rand", () => Push((long)Random.Shared.Next()));
        Op("srand", () => Pop());
        Op("rrand", () => Push(0L));
        Op("cvi", () =>
        {
            var a = Pop();
            if (a is PsString s) a = PsLexer.ParseNumber(s.Text.Trim()) ?? throw new PsError("typecheck");
            Push((long)Math.Truncate(Ps.Num(a)));
        });
        Op("cvr", () =>
        {
            var a = Pop();
            if (a is PsString s) a = PsLexer.ParseNumber(s.Text.Trim()) ?? throw new PsError("typecheck");
            Push(Ps.Num(a));
        });

        // Relational / logical
        Op("eq", () => { var b = Pop(); var a = Pop(); Push(Ps.PsEquals(a, b)); });
        Op("ne", () => { var b = Pop(); var a = Pop(); Push(!Ps.PsEquals(a, b)); });
        Op("gt", () => Compare((a, b) => a > b));
        Op("ge", () => Compare((a, b) => a >= b));
        Op("lt", () => Compare((a, b) => a < b));
        Op("le", () => Compare((a, b) => a <= b));
        Op("and", () => { var b = Pop(); var a = Pop(); Push(a is bool x && b is bool y ? x && y : (object)(Ps.Int(a) & Ps.Int(b))); });
        Op("or", () => { var b = Pop(); var a = Pop(); Push(a is bool x && b is bool y ? x || y : (object)(Ps.Int(a) | Ps.Int(b))); });
        Op("xor", () => { var b = Pop(); var a = Pop(); Push(a is bool x && b is bool y ? x ^ y : (object)(Ps.Int(a) ^ Ps.Int(b))); });
        Op("not", () => { var a = Pop(); Push(a is bool x ? !x : (object)~Ps.Int(a)); });
        Op("bitshift", () => { long s = PopInt(), v = PopInt(); Push(s >= 0 ? (long)((ulong)(uint)v << (int)s) & 0xFFFFFFFF : (long)((uint)v >> (int)-s)); });

        // Control
        Op("exec", () => Execute(Pop()));
        Op("if", () => { var p = PopProc(); if (PopBool()) Execute(p); });
        Op("ifelse", () => { var no = PopProc(); var yes = PopProc(); Execute(PopBool() ? yes : no); });
        Op("for", () =>
        {
            var proc = PopProc();
            var limitO = Pop(); var incO = Pop(); var initO = Pop();
            bool ints = initO is long && incO is long && limitO is long;
            double init = Ps.Num(initO), inc = Ps.Num(incO), limit = Ps.Num(limitO);
            if (inc == 0) return;
            Loop(() =>
            {
                for (double v = init; inc > 0 ? v <= limit + 1e-9 : v >= limit - 1e-9; v += inc)
                {
                    Push(ints ? (object)(long)v : v);
                    Execute(proc);
                }
            });
        });
        Op("repeat", () =>
        {
            var proc = PopProc();
            long n = PopInt();
            Loop(() => { for (long i = 0; i < n; i++) Execute(proc); });
        });
        Op("loop", () =>
        {
            var proc = PopProc();
            Loop(() => { while (true) Execute(proc); });
        });
        Op("exit", () => throw new PsExit());
        Op("stop", () => throw new PsStop());
        Op("stopped", () =>
        {
            var obj = Pop();
            int depth = _stack.Count;
            int dictDepth = _dicts.Count;
            try
            {
                Execute(obj);
                Push(false);
            }
            catch (PsStop)
            {
                Push(true);
            }
            catch (PsError e)
            {
                if (ErrorLog.Count < 25) ErrorLog.Add($"(stopped) {e.Message} in '{_lastName}'");
                _dollarError.Put("newerror", true);
                _dollarError.Put("errorname", Name(e.ErrorName));
                Push(true);
            }
            catch (PsExit)
            {
                Push(true);
            }
            _ = depth; _ = dictDepth;
        });
        Op("countexecstack", () => Push(0L));
        Op("execstack", () => { Pop(); Push(new PsArray(Array.Empty<object>())); });
        Op("quit", () => throw new PsQuit("quit"));
        Op("start", () => { });

        // Types
        Op("type", () => Push(ExecName(Ps.TypeName(Pop()))));
        Op("cvlit", () => Push(WithExec(Pop(), false)));
        Op("cvx", () => Push(WithExec(Pop(), true)));
        Op("xcheck", () => Push(Ps.IsExec(Pop())));
        foreach (var n in new[] { "readonly", "executeonly", "noaccess" }) Op(n, () => { });
        Op("rcheck", () => { Pop(); Push(true); });
        Op("wcheck", () => { Pop(); Push(true); });
        Op("cvn", () => { var s = PopString(); Push(new PsName(s.Text, s.Exec)); });
        Op("cvs", () =>
        {
            var dest = PopString();
            var bytes = Encoding.Latin1.GetBytes(Ps.ToText(Pop()));
            int n = Math.Min(bytes.Length, dest.Length);
            bytes.AsSpan(0, n).CopyTo(dest.Data.AsSpan(dest.Offset));
            Push(new PsString(dest.Data, dest.Offset, n));
        });
        Op("cvrs", () =>
        {
            var dest = PopString();
            long radix = PopInt();
            var num = Pop();
            string text = radix == 10 ? Ps.ToText(num) : Convert.ToString((long)Ps.Num(num) & 0xFFFFFFFF, (int)radix).ToUpperInvariant();
            var bytes = Encoding.Latin1.GetBytes(text);
            int n = Math.Min(bytes.Length, dest.Length);
            bytes.AsSpan(0, n).CopyTo(dest.Data.AsSpan(dest.Offset));
            Push(new PsString(dest.Data, dest.Offset, n));
        });

        // Composite objects
        Op("array", () => { long n = PopInt(); var a = new object[n]; Array.Fill(a, PsNull.Instance); Push(new PsArray(a)); });
        Op("packedarray", () => { long n = PopInt(); var items = new object[n]; for (long i = n - 1; i >= 0; i--) items[i] = Pop(); Push(new PsArray(items)); });
        Op("string", () => Push(new PsString(new byte[PopInt()])));
        Op("dict", () => Push(new PsDict((int)Math.Max(1, PopInt()))));
        Op("maxlength", () => Push((long)PopDict().Capacity));
        Op("length", () => Push(Pop() switch
        {
            PsArray a => (long)a.Length,
            PsString s => (long)s.Length,
            PsDict d => (long)d.Map.Count,
            PsName n => (long)n.Value.Length,
            _ => throw new PsError("typecheck"),
        }));
        Op("get", () =>
        {
            var key = Pop();
            var obj = Pop();
            switch (obj)
            {
                case PsArray a: { long i = Ps.Int(key); if (i < 0 || i >= a.Length) throw new PsError("rangecheck"); Push(a[(int)i]); break; }
                case PsString s: { long i = Ps.Int(key); if (i < 0 || i >= s.Length) throw new PsError("rangecheck"); Push((long)s[(int)i]); break; }
                case PsDict d: if (!d.TryGet(key, out var v)) throw new PsError("undefined", Ps.ToText(key)); Push(v); break;
                default: throw new PsError("typecheck");
            }
        });
        Op("put", () =>
        {
            var value = Pop();
            var key = Pop();
            var obj = Pop();
            switch (obj)
            {
                case PsArray a: { long i = Ps.Int(key); if (i < 0 || i >= a.Length) throw new PsError("rangecheck"); a[(int)i] = value; break; }
                case PsString s: { long i = Ps.Int(key); if (i < 0 || i >= s.Length) throw new PsError("rangecheck"); s[(int)i] = (byte)Ps.Int(value); break; }
                case PsDict d: d.Put(key, value); break;
                default: throw new PsError("typecheck");
            }
        });
        Op("getinterval", () =>
        {
            long count = PopInt(), index = PopInt();
            var obj = Pop();
            switch (obj)
            {
                case PsArray a:
                    if (index < 0 || count < 0 || index + count > a.Length) throw new PsError("rangecheck");
                    Push(new PsArray(a.Data, a.Offset + (int)index, (int)count, a.Exec)); break;
                case PsString s:
                    if (index < 0 || count < 0 || index + count > s.Length) throw new PsError("rangecheck");
                    Push(new PsString(s.Data, s.Offset + (int)index, (int)count) { Exec = s.Exec }); break;
                default: throw new PsError("typecheck");
            }
        });
        Op("putinterval", () =>
        {
            var src = Pop();
            long index = PopInt();
            var dest = Pop();
            switch (dest, src)
            {
                case (PsArray d, PsArray s):
                    if (index < 0 || index + s.Length > d.Length) throw new PsError("rangecheck");
                    var copy = new object[s.Length];
                    for (int i = 0; i < s.Length; i++) copy[i] = s[i];
                    for (int i = 0; i < copy.Length; i++) d[(int)index + i] = copy[i];
                    break;
                case (PsString d, PsString s):
                    if (index < 0 || index + s.Length > d.Length) throw new PsError("rangecheck");
                    s.Span.ToArray().CopyTo(d.Data, d.Offset + (int)index);
                    break;
                default: throw new PsError("typecheck");
            }
        });
        Op("aload", () =>
        {
            var a = PopArray();
            for (int i = 0; i < a.Length; i++) Push(a[i]);
            Push(a);
        });
        Op("astore", () =>
        {
            var a = PopArray();
            for (int i = a.Length - 1; i >= 0; i--) a[i] = Pop();
            Push(a);
        });
        Op("forall", () =>
        {
            var proc = PopProc();
            var obj = Pop();
            Loop(() =>
            {
                switch (obj)
                {
                    case PsArray a:
                        for (int i = 0; i < a.Length; i++) { Push(a[i]); Execute(proc); }
                        break;
                    case PsString s:
                        for (int i = 0; i < s.Length; i++) { Push((long)s[i]); Execute(proc); }
                        break;
                    case PsDict d:
                        foreach (var kv in d.Map.ToArray())
                        {
                            Push(kv.Key switch { string k => Name(k), double k when k == Math.Floor(k) => (long)k, var k => k });
                            Push(kv.Value);
                            Execute(proc);
                        }
                        break;
                    default: throw new PsError("typecheck");
                }
            });
        });
        Op("search", () => Search(anchored: false));
        Op("anchorsearch", () => Search(anchored: true));
        Op("token", () =>
        {
            var src = Pop();
            if (src is PsString s)
            {
                var bs = new BytesSource(s.Data, s.Offset, s.Offset + s.Length);
                var tok = new PsLexer(bs).Next();
                if (tok == null) { Push(false); return; }
                Push(new PsString(s.Data, bs.Position, s.Offset + s.Length - bs.Position));
                Push(tok);
                Push(true);
            }
            else if (src is PsFile f)
            {
                var tok = new PsLexer(f.Source).Next();
                if (tok == null) { Push(false); return; }
                Push(tok);
                Push(true);
            }
            else throw new PsError("typecheck");
        });

        // Dictionaries
        Op("begin", () => _dicts.Add(PopDict()));
        Op("end", () => { if (_dicts.Count <= 3) throw new PsError("dictstackunderflow"); _dicts.RemoveAt(_dicts.Count - 1); });
        Op("def", () => { var v = Pop(); var k = Pop(); CurrentDict.Put(k, v); });
        Op("load", () => { var k = Pop(); var d = Where(k) ?? throw new PsError("undefined", Ps.ToText(k)); d.TryGet(k, out var v); Push(v); });
        Op("store", () => { var v = Pop(); var k = Pop(); (Where(k) ?? CurrentDict).Put(k, v); });
        Op("known", () => { var k = Pop(); Push(PopDict().Has(k)); });
        Op("undef", () => { var k = Pop(); PopDict().Remove(k); });
        Op("where", () =>
        {
            var d = Where(Pop());
            if (d == null) Push(false);
            else { Push(d); Push(true); }
        });
        Op("currentdict", () => Push(CurrentDict));
        Op("countdictstack", () => Push((long)_dicts.Count));
        Op("dictstack", () =>
        {
            var a = PopArray();
            for (int i = 0; i < _dicts.Count && i < a.Length; i++) a[i] = _dicts[i];
            Push(new PsArray(a.Data, a.Offset, Math.Min(_dicts.Count, a.Length), false));
        });
        Op("cleardictstack", () => { while (_dicts.Count > 3) _dicts.RemoveAt(_dicts.Count - 1); });
        Op("bind", () => { var p = Peek(); if (p is PsArray a) Bind(a, 0); });

        // VM / environment
        Op("save", () =>
        {
            _vmSaves.Add(new Dictionary<PsDict, Dictionary<object, object>>());
            Push(new PsSave(SaveGState()) { Level = _vmSaves.Count });
        });
        Op("restore", () =>
        {
            if (Pop() is not PsSave s) return;
            // Undo dictionary changes made since the save (newest first), then the graphics state
            while (_vmSaves.Count >= s.Level && _vmSaves.Count > 0)
            {
                var journal = _vmSaves[^1];
                _vmSaves.RemoveAt(_vmSaves.Count - 1);
                foreach (var (dict, before) in journal)
                {
                    dict.Map.Clear();
                    foreach (var kv in before) dict.Map[kv.Key] = kv.Value;
                }
            }
            RestoreGState(s.GStateDepth);
        });
        Op("vmstatus", () => { Push(0L); Push(0L); Push(10_000_000L); });
        Op("setglobal", () => Pop());
        Op("currentglobal", () => Push(false));
        Op("gcheck", () => { Pop(); Push(false); });
        Op("setpacking", () => Pop());
        Op("currentpacking", () => Push(false));
        _systemDict.Put("languagelevel", 3L); // Adobe prologs read it with "systemdict /languagelevel get"
        Op("version", () => Push(new PsString("3010")));
        Op("product", () => Push(new PsString("QuickPeek")));
        Op("revision", () => Push(1L));
        Op("serialnumber", () => Push(0L));
        Op("realtime", () => Push((long)_clock.ElapsedMilliseconds));
        Op("usertime", () => Push((long)_clock.ElapsedMilliseconds));
        foreach (var n in new[] { "setuserparams", "setsystemparams", "setdevparams", "setpagedevice", "setobjectformat", "setvmthreshold", "setucacheparams", "setcachelimit" })
            Op(n, () => Pop());
        Op("currentuserparams", () =>
        {
            var d = new PsDict();
            foreach (var key in new[] { "MaxPatternCache", "MaxFontItem", "MaxFormItem", "MaxUPathItem", "MaxLocalVM", "MaxDictStack", "MaxExecStack", "MaxOpStack", "MaxScreenItem", "MinFontCompress", "VMReclaim", "VMThreshold", "MaxSuperScreen" })
                d.Put(key, 1_000_000L);
            Push(d);
        });
        Op("currentsystemparams", () => { Execute(Lookup("currentuserparams")); });
        Op("currentpagedevice", () =>
        {
            var d = new PsDict();
            d.Put("PageSize", new PsArray(new object[] { 612.0, 792.0 }));
            Push(d);
        });
        Op("vmreclaim", () => Pop());
        Op("currentcolortransfer", () => { for (int i = 0; i < 4; i++) Push(new PsArray(Array.Empty<object>(), exec: true)); });
        foreach (var n in new[] { "showpage", "copypage", "erasepage", "flush", "ucache", "prompt", "echo", "executive", "internaldict_", "setshared" }) Op(n, () => { });
        Op("internaldict", () => { Pop(); Push(_userDict); });
        Op("=", () => Pop());
        Op("==", () => Pop());
        Op("print", () => Pop());
        Op("pstack", () => { });
        Op("stack", () => { });

        // Resources
        Op("defineresource", () =>
        {
            var category = Ps.ToText(Pop());
            var instance = Pop();
            var key = Pop();
            if (category == "Font" && instance is PsDict font) { DefineFont(key, font); Push(font); return; }
            Resource(category).Put(key, instance);
            Push(instance);
        });
        Op("findresource", () =>
        {
            var category = Ps.ToText(Pop());
            var key = Pop();
            if (category == "Font") { Push(FindFont(key)); return; }
            if (category == "Category") { Push(Resource(Ps.ToText(key))); return; }
            if (Resource(category).TryGet(key, out var v)) { Push(v); return; }
            if (category == "ProcSet" && Ps.ToText(key) == "CIDInit") { Push(CidInit()); return; }
            if (category == "CMap") { Push(new PsDict()); return; }
            throw new PsError("undefinedresource", Ps.ToText(key));
        });
        Op("resourcestatus", () =>
        {
            var category = Ps.ToText(Pop());
            var key = Pop();
            bool has = category == "Font" ? _fontDirectory.Has(key) : Resource(category).Has(key);
            if (has) { Push(0L); Push(0L); Push(true); } else Push(false);
        });
        Op("undefineresource", () => { var category = Ps.ToText(Pop()); Resource(category).Remove(Pop()); });
        Op("resourceforall", () => { Pop(); Pop(); Pop(); Pop(); });

        // Files
        Op("currentfile", () => Push(new PsFile(_files.Count > 0 ? _files.Peek() : new BytesSource(Array.Empty<byte>()))));
        Op("read", () =>
        {
            var f = PopFile();
            int b = f.Source.Read();
            if (b < 0) Push(false);
            else { Push((long)b); Push(true); }
        });
        Op("readstring", () =>
        {
            var s = PopString();
            var f = PopFile();
            int n = 0;
            while (n < s.Length) { int b = f.Source.Read(); if (b < 0) break; s[n++] = (byte)b; }
            Push(new PsString(s.Data, s.Offset, n));
            Push(n == s.Length);
        });
        Op("readhexstring", () =>
        {
            var s = PopString();
            var f = PopFile();
            int n = 0, hi = -1;
            while (n < s.Length)
            {
                int c = f.Source.Read();
                if (c < 0) break;
                int v = PsLexer.HexValue(c);
                if (v < 0) continue;
                if (hi < 0) hi = v;
                else { s[n++] = (byte)((hi << 4) | v); hi = -1; }
            }
            Push(new PsString(s.Data, s.Offset, n));
            Push(n == s.Length);
        });
        Op("readline", () =>
        {
            var s = PopString();
            var f = PopFile();
            int n = 0;
            bool gotEol = false;
            while (true)
            {
                int c = f.Source.Read();
                if (c < 0) break;
                if (c == '\n') { gotEol = true; break; }
                if (c == '\r') { if (f.Source.Peek() == '\n') f.Source.Read(); gotEol = true; break; }
                if (n < s.Length) s[n++] = (byte)c;
            }
            Push(new PsString(s.Data, s.Offset, n));
            Push(gotEol || n > 0);
        });
        Op("bytesavailable", () => { PopFile(); Push(-1L); });
        Op("closefile", () =>
        {
            var f = PopFile();
            // A decode filter reads on to its end-of-data marker, so the program continues right after the data
            if (f.Source is not BytesSource && !_files.Contains(f.Source)) while (f.Source.Read() >= 0) { }
            f.Source.Closed = true;
        });
        Op("flushfile", () =>
        {
            var f = PopFile();
            // Draining the program itself would throw away the rest of the drawing; filters are drained
            if (f.Source is not BytesSource && !_files.Contains(f.Source)) while (f.Source.Read() >= 0) { }
        });
        Op("resetfile", () => PopFile());
        Op("status", () => { var o = Pop(); Push(o is PsFile f && !f.Source.Closed); });
        Op("fileposition", () => { PopFile(); Push(0L); });
        Op("setfileposition", () => { Pop(); PopFile(); });
        Op("file", () =>
        {
            Pop();
            var name = Ps.ToText(Pop());
            if (name is "%stdin" or "%lineedit" or "%statementedit")
                Push(new PsFile(_files.Count > 0 ? _files.Peek() : new BytesSource(Array.Empty<byte>())));
            else
                throw new PsError("undefinedfilename", name);
        });
        Op("run", () => throw new PsError("undefinedfilename", Ps.ToText(Pop())));
        Op("filter", () =>
        {
            string name = Ps.ToText(Pop());
            PsDict? parms = null;
            if (Peek() is PsDict d) { parms = d; Pop(); }
            if (name == "SubFileDecode" && parms == null)
            {
                // Level 2 form: source count string /SubFileDecode filter
                var eod = PopString();
                long count = PopInt();
                var srcObj = Pop();
                Push(new PsFile(new SubFileDecode(ToSource(srcObj), count, eod.Span.ToArray(), (int)count)));
                return;
            }
            var src = ToSource(Pop());
            Push(new PsFile(PsFilters.Create(name, src, parms)));
        });
        Op("eexec", () =>
        {
            var src = ToSource(Pop());
            var decrypted = new EexecDecode(src);
            _dicts.Add(_systemDict);
            int depth = _dicts.Count;
            try { RunSource(decrypted); }
            finally
            {
                if (_dicts.Count >= depth) _dicts.RemoveRange(depth - 1, _dicts.Count - depth + 1);
            }
        });
    }

    private PsDict? _cidInit;

    /// <summary>
    /// CIDInit: the operators a CMap definition uses. CMaps only matter for CID-keyed (e.g. CJK) text, which falls back
    /// to system fonts anyway, so the ranges are consumed without being interpreted.
    /// </summary>
    private PsDict CidInit()
    {
        if (_cidInit != null) return _cidInit;
        var d = new PsDict(32);
        void Def(string name, Action fn) => d.Put(name, new PsOperator(name, fn));
        Def("begincmap", () => { });
        Def("endcmap", () => { });
        Def("usecmap", () => Pop());
        Def("usefont", () => Pop());
        foreach (var kind in new[] { "codespacerange", "bfchar", "bfrange", "cidchar", "cidrange", "notdefchar", "notdefrange" })
        {
            Def("begin" + kind, () => { Pop(); Push(PsMark.Instance); });
            Def("end" + kind, () => PopToMark());
        }
        Def("beginrearrangedfont", () => { Pop(); Pop(); });
        Def("endrearrangedfont", () => { });
        return _cidInit = d;
    }

    private PsFile PopFile() => Pop() is PsFile f ? f : throw new PsError("typecheck", "file expected");

    private IByteSource ToSource(object o) => o switch
    {
        PsFile f => f.Source,
        PsString s => new BytesSource(s.Span.ToArray()),
        PsArray p => new ProcSource(this, p),
        _ => throw new PsError("typecheck", "data source expected"),
    };

    private PsDict Resource(string category)
    {
        if (!_resources.TryGetValue(category, out var d)) _resources[category] = d = new PsDict();
        return d;
    }

    private object[] PopToMark()
    {
        int i = _stack.Count - 1;
        while (i >= 0 && _stack[i] is not PsMark) i--;
        if (i < 0) throw new PsError("unmatchedmark");
        var items = _stack.GetRange(i + 1, _stack.Count - i - 1).ToArray();
        _stack.RemoveRange(i, _stack.Count - i);
        return items;
    }

    private int CountToMark()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
            if (_stack[i] is PsMark) return _stack.Count - 1 - i;
        throw new PsError("unmatchedmark");
    }

    private void Compare(Func<double, double, bool> cmp)
    {
        var b = Pop(); var a = Pop();
        if (a is PsString sa && b is PsString sb) Push(cmp(string.CompareOrdinal(sa.Text, sb.Text), 0));
        else Push(cmp(Ps.Num(a), Ps.Num(b)));
    }

    private void Search(bool anchored)
    {
        var seek = PopString();
        var s = PopString();
        var hay = s.Span;
        int idx = anchored ? (hay.StartsWith(seek.Span) ? 0 : -1) : hay.IndexOf(seek.Span);
        if (idx < 0) { Push(s); Push(false); return; }
        if (anchored)
        {
            Push(new PsString(s.Data, s.Offset + seek.Length, s.Length - seek.Length));
            Push(new PsString(s.Data, s.Offset, seek.Length));
        }
        else
        {
            Push(new PsString(s.Data, s.Offset + idx + seek.Length, s.Length - idx - seek.Length));
            Push(new PsString(s.Data, s.Offset + idx, seek.Length));
            Push(new PsString(s.Data, s.Offset, idx));
        }
        Push(true);
    }

    private static object WithExec(object o, bool exec) => o switch
    {
        PsArray a => new PsArray(a.Data, a.Offset, a.Length, exec),
        PsName n => new PsName(n.Value, exec),
        PsString s => new PsString(s.Data, s.Offset, s.Length) { Exec = exec },
        PsFile f => new PsFile(f.Source) { Exec = exec },
        _ => o,
    };

    /// <summary>bind: replaces operator names inside a procedure with the operators themselves.</summary>
    private void Bind(PsArray proc, int level)
    {
        if (level > 50) return;
        for (int i = 0; i < proc.Length; i++)
        {
            var e = proc[i];
            if (e is PsName { Exec: true } n)
            {
                PsDict? d = Where(n.Value);
                if (d != null && d.Map[n.Value] is PsOperator op) proc[i] = op;
            }
            else if (e is PsArray { Exec: true } inner)
            {
                Bind(inner, level + 1);
            }
        }
    }
}
