using System.Text;

namespace QuickPeek.Core.PostScript;

// PostScript values. Integers are long, reals double, booleans bool; everything else is one of the classes below.

internal sealed class PsError : Exception
{
    public PsError(string name, string? detail = null) : base(detail == null ? name : $"{name}: {detail}") => ErrorName = name;
    public string ErrorName { get; }
}

/// <summary>Thrown by <c>exit</c>, caught by the innermost loop.</summary>
internal sealed class PsExit : Exception { }

/// <summary>Thrown by <c>stop</c>, caught by <c>stopped</c>.</summary>
internal sealed class PsStop : Exception { }

/// <summary>Stops the whole job (quit, time/operation budget exceeded).</summary>
internal sealed class PsQuit : Exception
{
    public PsQuit(string reason) : base(reason) { }
}

internal sealed class PsName
{
    public readonly string Value;
    public readonly bool Exec;
    public PsName(string value, bool exec) { Value = value; Exec = exec; }
    public override string ToString() => (Exec ? "" : "/") + Value;
}

internal sealed class PsString
{
    public byte[] Data;
    public int Offset;
    public int Length;
    public bool Exec;

    public PsString(byte[] data, int offset, int length) { Data = data; Offset = offset; Length = length; }
    public PsString(byte[] data) : this(data, 0, data.Length) { }
    public PsString(string text) : this(Encoding.Latin1.GetBytes(text)) { }

    public byte this[int i]
    {
        get => Data[Offset + i];
        set => Data[Offset + i] = value;
    }

    public ReadOnlySpan<byte> Span => Data.AsSpan(Offset, Length);
    public string Text => Encoding.Latin1.GetString(Data, Offset, Length);
    public override string ToString() => Text;
}

internal sealed class PsArray
{
    public object[] Data;
    public int Offset;
    public int Length;
    public bool Exec;

    public PsArray(object[] data, int offset, int length, bool exec) { Data = data; Offset = offset; Length = length; Exec = exec; }
    public PsArray(object[] data, bool exec = false) : this(data, 0, data.Length, exec) { }

    public object this[int i]
    {
        get => Data[Offset + i];
        set => Data[Offset + i] = value;
    }
}

internal sealed class PsDict
{
    public readonly Dictionary<object, object> Map = new();
    public int Capacity;
    public PsDict(int capacity = 16) => Capacity = capacity;

    /// <summary>Names and strings are the same key in PostScript; integers and equal reals too.</summary>
    public static object Key(object k) => k switch
    {
        PsName n => n.Value,
        PsString s => s.Text,
        long l => (double)l,
        _ => k,
    };

    public bool TryGet(object key, out object value) => Map.TryGetValue(Key(key), out value!);
    public object? Get(string key) => Map.TryGetValue(key, out var v) ? v : null;
    /// <summary>Called before a dictionary changes, so save/restore can undo it (one render per thread).</summary>
    [ThreadStatic] public static Action<PsDict>? BeforeWrite;

    public void Put(object key, object value) { BeforeWrite?.Invoke(this); Map[Key(key)] = value; }
    public bool Remove(object key) { BeforeWrite?.Invoke(this); return Map.Remove(Key(key)); }
    public bool Has(object key) => Map.ContainsKey(Key(key));
}

internal sealed class PsOperator
{
    public readonly string Name;
    public readonly Action Fn;
    public PsOperator(string name, Action fn) { Name = name; Fn = fn; }
    public override string ToString() => "--" + Name + "--";
}

internal sealed class PsMark
{
    public static readonly PsMark Instance = new();
}

internal sealed class PsNull
{
    public static readonly PsNull Instance = new();
}

internal sealed class PsSave
{
    public readonly int GStateDepth;
    public int Level;
    public PsSave(int depth) => GStateDepth = depth;
}

internal sealed class PsFile
{
    public readonly IByteSource Source;
    public bool Exec;
    public PsFile(IByteSource source) => Source = source;
}

internal sealed class PsGState
{
    public object Snapshot = null!;
}

internal static class Ps
{
    public static readonly PsName Null = new("null", false);

    public static double Num(object o) => o switch
    {
        long l => l,
        double d => d,
        int i => i,
        _ => throw new PsError("typecheck", $"number expected, got {TypeName(o)}"),
    };

    public static long Int(object o) => o switch
    {
        long l => l,
        double d when d == Math.Floor(d) => (long)d,
        int i => i,
        _ => throw new PsError("typecheck", $"integer expected, got {TypeName(o)}"),
    };

    public static bool IsNum(object o) => o is long or double;

    public static string TypeName(object o) => o switch
    {
        long => "integertype",
        double => "realtype",
        bool => "booleantype",
        PsName => "nametype",
        PsString => "stringtype",
        PsArray => "arraytype",
        PsDict => "dicttype",
        PsOperator => "operatortype",
        PsMark => "marktype",
        PsNull => "nulltype",
        PsSave => "savetype",
        PsFile => "filetype",
        PsGState => "gstatetype",
        _ => "nulltype",
    };

    public static bool IsExec(object o) => o switch
    {
        PsName n => n.Exec,
        PsArray a => a.Exec,
        PsString s => s.Exec,
        PsFile f => f.Exec,
        PsOperator => true,
        _ => false,
    };

    /// <summary>Text form used by cvs / = (numbers, names, strings, the rest as --type--).</summary>
    public static string ToText(object o) => o switch
    {
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => FormatReal(d),
        bool b => b ? "true" : "false",
        PsName n => n.Value,
        PsString s => s.Text,
        PsOperator op => op.Name,
        _ => "--nostringval--",
    };

    public static string FormatReal(double d)
    {
        string s = d.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }

    public static bool PsEquals(object a, object b)
    {
        if (IsNum(a) && IsNum(b)) return Num(a) == Num(b);
        if (a is PsName or PsString && b is PsName or PsString) return ToText(a) == ToText(b);
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is PsFile fa && b is PsFile fb) return fa.Source == fb.Source;
        if (a is PsArray aa && b is PsArray ab) return aa.Data == ab.Data && aa.Offset == ab.Offset && aa.Length == ab.Length;
        return ReferenceEquals(a, b);
    }
}
