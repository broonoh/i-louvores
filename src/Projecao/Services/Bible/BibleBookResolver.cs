using System.Text.RegularExpressions;
using Projecao.Data;

namespace Projecao.Services.Bible;

/// <summary>
/// Converte o identificador de um livro, como aparece nos ficheiros, no Id canónico (1–66).
/// Aceita: número (1–66), nomes em português (com/sem acentos, pt-BR/pt-PT) e inglês,
/// siglas portuguesas, códigos OSIS (Gen, Exod…) e USFM/Paratext (GEN, EXO…),
/// e numeração romana ("I Reis").
/// </summary>
public static partial class BibleBookResolver
{
    private static readonly string[] Osis =
    [
        "Gen", "Exod", "Lev", "Num", "Deut", "Josh", "Judg", "Ruth", "1Sam", "2Sam", "1Kgs", "2Kgs", "1Chr", "2Chr",
        "Ezra", "Neh", "Esth", "Job", "Ps", "Prov", "Eccl", "Song", "Isa", "Jer", "Lam", "Ezek", "Dan", "Hos", "Joel",
        "Amos", "Obad", "Jonah", "Mic", "Nah", "Hab", "Zeph", "Hag", "Zech", "Mal",
        "Matt", "Mark", "Luke", "John", "Acts", "Rom", "1Cor", "2Cor", "Gal", "Eph", "Phil", "Col", "1Thess", "2Thess",
        "1Tim", "2Tim", "Titus", "Phlm", "Heb", "Jas", "1Pet", "2Pet", "1John", "2John", "3John", "Jude", "Rev"
    ];

    private static readonly string[] Usfm =
    [
        "GEN", "EXO", "LEV", "NUM", "DEU", "JOS", "JDG", "RUT", "1SA", "2SA", "1KI", "2KI", "1CH", "2CH", "EZR", "NEH",
        "EST", "JOB", "PSA", "PRO", "ECC", "SNG", "ISA", "JER", "LAM", "EZK", "DAN", "HOS", "JOL", "AMO", "OBA", "JON",
        "MIC", "NAM", "HAB", "ZEP", "HAG", "ZEC", "MAL", "MAT", "MRK", "LUK", "JHN", "ACT", "ROM", "1CO", "2CO", "GAL",
        "EPH", "PHP", "COL", "1TH", "2TH", "1TI", "2TI", "TIT", "PHM", "HEB", "JAS", "1PE", "2PE", "1JN", "2JN", "3JN",
        "JUD", "REV"
    ];

    private static readonly string[] English =
    [
        "Genesis", "Exodus", "Leviticus", "Numbers", "Deuteronomy", "Joshua", "Judges", "Ruth", "1 Samuel", "2 Samuel",
        "1 Kings", "2 Kings", "1 Chronicles", "2 Chronicles", "Ezra", "Nehemiah", "Esther", "Job", "Psalms", "Proverbs",
        "Ecclesiastes", "Song of Solomon", "Isaiah", "Jeremiah", "Lamentations", "Ezekiel", "Daniel", "Hosea", "Joel",
        "Amos", "Obadiah", "Jonah", "Micah", "Nahum", "Habakkuk", "Zephaniah", "Haggai", "Zechariah", "Malachi",
        "Matthew", "Mark", "Luke", "John", "Acts", "Romans", "1 Corinthians", "2 Corinthians", "Galatians", "Ephesians",
        "Philippians", "Colossians", "1 Thessalonians", "2 Thessalonians", "1 Timothy", "2 Timothy", "Titus", "Philemon",
        "Hebrews", "James", "1 Peter", "2 Peter", "1 John", "2 John", "3 John", "Jude", "Revelation"
    ];

    // Siglas usadas em JSONs populares (ex.: thiagobodruk/biblia) e variantes comuns.
    private static readonly string[] PortugueseShort =
    [
        "gn", "ex", "lv", "nm", "dt", "js", "jz", "rt", "1sm", "2sm", "1rs", "2rs", "1cr", "2cr", "ed", "ne", "et", "jó",
        "sl", "pv", "ec", "ct", "is", "jr", "lm", "ez", "dn", "os", "jl", "am", "ob", "jn", "mq", "na", "hc", "sf",
        "ag", "zc", "ml", "mt", "mc", "lc", "jo", "atos", "rm", "1co", "2co", "gl", "ef", "fp", "cl", "1ts", "2ts",
        "1tm", "2tm", "tt", "fm", "hb", "tg", "1pe", "2pe", "1jo", "2jo", "3jo", "jd", "ap"
    ];

    private static readonly (string Name, int Id)[] Variants =
    [
        ("Salmo", 19), ("Cantares", 22), ("Cântico dos Cânticos", 22), ("Cantares de Salomão", 22), ("Cânticos dos Cânticos", 22),
        ("Atos dos Apóstolos", 44), ("Apocalipse de João", 66), ("Oséias", 28), ("Song of Songs", 22), ("Psalm", 19),
        ("Revelation of John", 66), ("Deuteronómio", 5), ("Génesis", 1), ("Êxodo", 2)
    ];

    private static readonly Dictionary<string, int> Exact = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> Loose = new(StringComparer.Ordinal);

    static BibleBookResolver()
    {
        var books = BibleCanon.CreateBooks();
        var loose = new Dictionary<string, HashSet<int>>();

        void Add(string key, int id)
        {
            Exact.TryAdd(Compact(key), id);
            var k = Normalize(key);
            if (!loose.TryGetValue(k, out var set)) loose[k] = set = [];
            set.Add(id);
        }

        foreach (var b in books)
        {
            Add(b.Name, b.Id);
            Add(b.Abbreviation, b.Id);
        }
        for (var i = 0; i < 66; i++)
        {
            Add(Osis[i], i + 1);
            Add(Usfm[i], i + 1);
            Add(English[i], i + 1);
            Add(PortugueseShort[i], i + 1);
        }
        foreach (var (name, id) in Variants)
            Add(name, id);

        // Chaves sem acentos só entram se não forem ambíguas ("jo" = Jó ou João? → só pela forma exata).
        foreach (var (k, ids) in loose)
            if (ids.Count == 1)
                Loose[k] = ids.First();
    }

    public static string OsisId(int bookId) => Osis[bookId - 1];

    /// <summary>Devolve o Id (1–66) ou null se não reconhecer.</summary>
    public static int? Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (int.TryParse(value, out var n))
            return n is >= 1 and <= 66 ? n : null;

        value = RomanPrefixRegex().Replace(value, m => m.Groups[1].Value.ToUpperInvariant() switch
        {
            "I" => "1", "II" => "2", "III" => "3", _ => m.Groups[1].Value
        } + m.Groups[2].Value);

        return Exact.TryGetValue(Compact(value), out var id) ? id
            : Loose.TryGetValue(Normalize(value), out id) ? id
            : null;
    }

    // "1 Samuel" / "1Samuel" / "1. Samuel" → "1samuel" (mantém acentos)
    private static string Compact(string s) => SpacesDotsRegex().Replace(s.Trim().ToLowerInvariant(), string.Empty);

    private static string Normalize(string s) => SpacesDotsRegex().Replace(TextNormalizer.Normalize(s), string.Empty);

    [GeneratedRegex(@"[\s.\-_º°]+")]
    private static partial Regex SpacesDotsRegex();

    [GeneratedRegex(@"^(III|II|I)(\s+\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex RomanPrefixRegex();
}
