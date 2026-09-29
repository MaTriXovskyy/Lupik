namespace Lupik.Core.PostScript;

/// <summary>Standard PostScript encodings and glyph-name → Unicode mapping (for drawing text with system fonts).</summary>
internal static class PsEncodings
{
    private static readonly string[] Ascii =
    {
        "space","exclam","quotedbl","numbersign","dollar","percent","ampersand","quoteright","parenleft","parenright",
        "asterisk","plus","comma","hyphen","period","slash","zero","one","two","three","four","five","six","seven","eight",
        "nine","colon","semicolon","less","equal","greater","question","at","A","B","C","D","E","F","G","H","I","J","K","L",
        "M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z","bracketleft","backslash","bracketright","asciicircum",
        "underscore","quoteleft","a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v",
        "w","x","y","z","braceleft","bar","braceright","asciitilde",
    };

    private static readonly (int, string)[] StandardHigh =
    {
        (161,"exclamdown"),(162,"cent"),(163,"sterling"),(164,"fraction"),(165,"yen"),(166,"florin"),(167,"section"),
        (168,"currency"),(169,"quotesingle"),(170,"quotedblleft"),(171,"guillemotleft"),(172,"guilsinglleft"),
        (173,"guilsinglright"),(174,"fi"),(175,"fl"),(177,"endash"),(178,"dagger"),(179,"daggerdbl"),(180,"periodcentered"),
        (182,"paragraph"),(183,"bullet"),(184,"quotesinglbase"),(185,"quotedblbase"),(186,"quotedblright"),
        (187,"guillemotright"),(188,"ellipsis"),(189,"perthousand"),(191,"questiondown"),(193,"grave"),(194,"acute"),
        (195,"circumflex"),(196,"tilde"),(197,"macron"),(198,"breve"),(199,"dotaccent"),(200,"dieresis"),(202,"ring"),
        (203,"cedilla"),(205,"hungarumlaut"),(206,"ogonek"),(207,"caron"),(208,"emdash"),(225,"AE"),(227,"ordfeminine"),
        (232,"Lslash"),(233,"Oslash"),(234,"OE"),(235,"ordmasculine"),(241,"ae"),(245,"dotlessi"),(248,"lslash"),
        (249,"oslash"),(250,"oe"),(251,"germandbls"),
    };

    private static readonly string[] Latin1High =
    {
        "space","exclamdown","cent","sterling","currency","yen","brokenbar","section","dieresis","copyright","ordfeminine",
        "guillemotleft","logicalnot","hyphen","registered","macron","degree","plusminus","twosuperior","threesuperior",
        "acute","mu","paragraph","periodcentered","cedilla","onesuperior","ordmasculine","guillemotright","onequarter",
        "onehalf","threequarters","questiondown","Agrave","Aacute","Acircumflex","Atilde","Adieresis","Aring","AE",
        "Ccedilla","Egrave","Eacute","Ecircumflex","Edieresis","Igrave","Iacute","Icircumflex","Idieresis","Eth","Ntilde",
        "Ograve","Oacute","Ocircumflex","Otilde","Odieresis","multiply","Oslash","Ugrave","Uacute","Ucircumflex",
        "Udieresis","Yacute","Thorn","germandbls","agrave","aacute","acircumflex","atilde","adieresis","aring","ae",
        "ccedilla","egrave","eacute","ecircumflex","edieresis","igrave","iacute","icircumflex","idieresis","eth","ntilde",
        "ograve","oacute","ocircumflex","otilde","odieresis","divide","oslash","ugrave","uacute","ucircumflex","udieresis",
        "yacute","thorn","ydieresis",
    };

    public static readonly string[] Standard = Build(standard: true);
    public static readonly string[] IsoLatin1 = Build(standard: false);

    private static string[] Build(bool standard)
    {
        var e = new string[256];
        Array.Fill(e, ".notdef");
        for (int i = 0; i < Ascii.Length; i++) e[32 + i] = Ascii[i];
        if (standard)
        {
            foreach (var (code, name) in StandardHigh) e[code] = name;
        }
        else
        {
            e[39] = "quoteright"; e[96] = "quoteleft";
            string[] accents = { "dotlessi", "grave", "acute", "circumflex", "tilde", "macron", "breve", "dotaccent", "dieresis", ".notdef", "ring", "cedilla", ".notdef", "hungarumlaut", "ogonek", "caron" };
            for (int i = 0; i < accents.Length; i++) e[144 + i] = accents[i];
            for (int i = 0; i < Latin1High.Length; i++) e[160 + i] = Latin1High[i];
        }
        return e;
    }

    public static PsArray StandardArray() => new(Standard.Select(n => (object)new PsName(n, false)).ToArray());
    public static PsArray IsoLatin1Array() => new(IsoLatin1.Select(n => (object)new PsName(n, false)).ToArray());

    private static readonly Dictionary<string, char> NameToChar = BuildNameMap();

    private static Dictionary<string, char> BuildNameMap()
    {
        var map = new Dictionary<string, char>();
        for (int i = 0; i < Ascii.Length; i++) map[Ascii[i]] = (char)(32 + i);
        map["quoteright"] = '’';
        map["quoteleft"] = '‘';
        map["quotesingle"] = '\'';
        map["grave"] = '`';
        for (int i = 1; i < Latin1High.Length; i++) map.TryAdd(Latin1High[i], (char)(160 + i));
        (string, char)[] extra =
        {
            ("quotedblleft",'“'),("quotedblright",'”'),("quotesinglbase",'‚'),("quotedblbase",'„'),
            ("guilsinglleft",'‹'),("guilsinglright",'›'),("endash",'–'),("emdash",'—'),
            ("bullet",'•'),("ellipsis",'…'),("dagger",'†'),("daggerdbl",'‡'),("perthousand",'‰'),
            ("fraction",'⁄'),("florin",'ƒ'),("fi",'ﬁ'),("fl",'ﬂ'),("trademark",'™'),
            ("Euro",'€'),("minus",'−'),("OE",'Œ'),("oe",'œ'),("Lslash",'Ł'),("lslash",'ł'),
            ("dotlessi",'ı'),("Scaron",'Š'),("scaron",'š'),("Zcaron",'Ž'),("zcaron",'ž'),
            ("Ydieresis",'Ÿ'),("Aogonek",'Ą'),("aogonek",'ą'),("Cacute",'Ć'),("cacute",'ć'),
            ("Eogonek",'Ę'),("eogonek",'ę'),("Nacute",'Ń'),("nacute",'ń'),("Sacute",'Ś'),
            ("sacute",'ś'),("Zacute",'Ź'),("zacute",'ź'),("Zdotaccent",'Ż'),("zdotaccent",'ż'),
            ("Ccaron",'Č'),("ccaron",'č'),("Ecaron",'Ě'),("ecaron",'ě'),("Rcaron",'Ř'),
            ("rcaron",'ř'),("circumflex",'ˆ'),("tilde",'˜'),("caron",'ˇ'),("breve",'˘'),
            ("dotaccent",'˙'),("ring",'˚'),("ogonek",'˛'),("hungarumlaut",'˝'),("space",' '),
            ("nbspace",' '),("uni00A0",' '),("hyphen",'-'),("sfthyphen",'­'),("periodcentered",'·'),
        };
        foreach (var (n, c) in extra) map[n] = c;
        return map;
    }

    /// <summary>Character for a glyph name ("A", "eacute", "uni0105", "u1F600"...), or null.</summary>
    public static char? CharFor(string glyph)
    {
        if (NameToChar.TryGetValue(glyph, out var c)) return c;
        int dot = glyph.IndexOf('.');
        if (dot > 0 && NameToChar.TryGetValue(glyph[..dot], out c)) return c; // "a.sc", "one.oldstyle"
        if (glyph.StartsWith("uni") && glyph.Length >= 7 && int.TryParse(glyph.AsSpan(3, 4), System.Globalization.NumberStyles.HexNumber, null, out int u)) return (char)u;
        if (glyph.StartsWith('u') && glyph.Length is 5 or 6 && int.TryParse(glyph.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out u) && u < 0x10000) return (char)u;
        if (glyph.Length == 1) return glyph[0];
        return null;
    }
}
