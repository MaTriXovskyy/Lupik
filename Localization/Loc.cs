using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace Lupik.Localization;

/// <summary>
/// The app's texts in 8 languages (Localization/Strings/*.json, embedded). XAML binds to the indexer through
/// {loc:T key}, so switching the language updates every open window at once, no restart.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    /// <summary>Supported languages: code, name in that language.</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("pl", "Polski"), ("en", "English"), ("de", "Deutsch"), ("es", "Español"),
        ("fr", "Français"), ("it", "Italiano"), ("ru", "Русский"), ("uk", "Українська"),
    };

    private Dictionary<string, string> _texts = new();
    private readonly Dictionary<string, string> _english;

    public string Code { get; private set; } = "en";
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-GB");

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed (for texts built in code).</summary>
    public event Action? LanguageChanged;

    private Loc()
    {
        _english = Load("en");
        _texts = _english;
    }

    public string this[string key] =>
        _texts.TryGetValue(key, out var t) ? t : _english.TryGetValue(key, out var e) ? e : key;

    public static string T(string key) => Instance[key];

    public static string T(string key, params object?[] args) => string.Format(Instance.Culture, Instance[key], args);

    /// <summary>Counted text: "{0} files" in the right plural form ("key.one", "key.few", "key.many", "key.other").</summary>
    public static string Plural(string key, long n)
    {
        string form = PluralForm(Instance.Code, n);
        string text = Instance.Has($"{key}.{form}") ? Instance[$"{key}.{form}"]
            : Instance.Has($"{key}.other") ? Instance[$"{key}.other"] : Instance[$"{key}.many"];
        return string.Format(Instance.Culture, text, n);
    }

    private bool Has(string key) => _texts.ContainsKey(key) || _english.ContainsKey(key);

    /// <summary>CLDR plural rules for the supported languages.</summary>
    private static string PluralForm(string code, long n)
    {
        long mod10 = n % 10, mod100 = n % 100;
        switch (code)
        {
            case "pl":
                if (n == 1) return "one";
                return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
            case "ru" or "uk":
                if (mod10 == 1 && mod100 != 11) return "one";
                return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
            default:
                return n == 1 ? "one" : "other";
        }
    }

    /// <summary>"auto" = the Windows display language if Lupik speaks it, English otherwise.</summary>
    public static string Resolve(string? setting)
    {
        string code = string.IsNullOrEmpty(setting) || setting == "auto"
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : setting;
        return Languages.Any(l => l.Code == code) ? code : "en";
    }

    public void SetLanguage(string? setting)
    {
        string code = Resolve(setting);
        _texts = code == "en" ? _english : Load(code);
        Code = code;
        Culture = CultureInfo.GetCultureInfo(code switch
        {
            "en" => "en-GB", "pl" => "pl-PL", "de" => "de-DE", "es" => "es-ES",
            "fr" => "fr-FR", "it" => "it-IT", "ru" => "ru-RU", "uk" => "uk-UA", _ => code,
        });
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        LanguageChanged?.Invoke();
        App.Log($"[Loc] Language: {code} ({_texts.Count} texts)");
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Lupik.Localization.Strings.{code}.json");
            if (stream == null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch (Exception ex)
        {
            App.Log($"[Loc] Could not load '{code}': {ex.Message}");
            return new();
        }
    }

    /// <summary>Keys present in English but missing in another language (logged at startup).</summary>
    public static IEnumerable<string> MissingKeys()
    {
        var english = Load("en");
        foreach (var (code, _) in Languages.Where(l => l.Code != "en"))
        {
            var other = Load(code);
            // Plural forms differ per language (en: one/other, pl: one/few/many): compare the base key
            static string Base(string k) => System.Text.RegularExpressions.Regex.Replace(k, @"\.(one|few|many|other)$", "");
            var otherBases = other.Keys.Select(Base).ToHashSet();
            foreach (var key in english.Keys.Select(Base).Distinct().Where(k => !otherBases.Contains(k)))
                yield return $"{code}:{key}";
        }
    }
}

/// <summary>XAML: Text="{loc:T image.zoomIn}". A live binding, so it follows language changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
