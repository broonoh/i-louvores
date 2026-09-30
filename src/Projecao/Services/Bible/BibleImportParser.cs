using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Projecao.Services.Import;

namespace Projecao.Services.Bible;

public sealed record ParsedVerse(int BookId, int Chapter, int Verse, string Text);

public sealed record ParsedBible(
    IReadOnlyList<ParsedVerse> Verses,
    IReadOnlyList<string> Issues,
    string Format,
    string? SuggestedAbbreviation,
    string? SuggestedName)
{
    public int BookCount => Verses.Select(v => v.BookId).Distinct().Count();
}

/// <summary>
/// Lê versões da Bíblia em vários formatos abertos:
///   • OSIS (.xml/.osis) — contentor ou "milestone" (sID/eID), p. ex. CrossWire e Bíblia Livre
///   • Zefania XML (.xml) — OpenLP, Quelea…
///   • OpenSong (.xml)
///   • JSON (.json) — array de livros com "chapters" (ex.: thiagobodruk/biblia) ou lista de versículos
///   • CSV/TSV (.csv/.tsv) — livro, capítulo, versículo, texto
///   • Um versículo por linha (.txt/.vpl) — "Gn 1:1 No princípio…"
/// Ficheiros .zip são abertos e o primeiro ficheiro reconhecido é lido.
/// Notas de rodapé e títulos de secção são descartados: só o texto bíblico é importado.
/// </summary>
public static partial class BibleImportParser
{
    public const long MaxFileBytes = 100L * 1024 * 1024;

    public static readonly string[] SupportedExtensions = [".xml", ".osis", ".json", ".csv", ".tsv", ".txt", ".vpl", ".zip"];

    public static ParsedBible Parse(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            if (ext == ".zip")
                return ParseZip(fileName, content);

            var issues = new List<string>();
            var (format, verses, abbrev, name) = ext switch
            {
                ".xml" or ".osis" => ParseXml(content, issues),
                ".json" => ("JSON", ParseJson(content, issues), null, null),
                ".csv" or ".tsv" => ("CSV", ParseDelimited(SongImportParser.Decode(content), issues), null, null),
                ".txt" or ".vpl" => ("Texto (um versículo por linha)", ParseVpl(SongImportParser.Decode(content), issues), null, null),
                _ => throw new InvalidDataException($"Formato não suportado ({ext}).")
            };

            var stem = Path.GetFileNameWithoutExtension(fileName);
            return Finish(verses, issues, format, abbrev ?? SuggestAbbreviation(stem), name ?? stem);
        }
        catch (Exception ex) when (ex is XmlException or JsonException or InvalidDataException or FormatException)
        {
            return new ParsedBible([], [$"Ficheiro inválido: {ex.Message}"], "?", null, null);
        }
    }

    private static ParsedBible ParseZip(string fileName, byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var entry = zip.Entries
            .Where(e => e.Length > 0 && SupportedExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()) && Path.GetExtension(e.Name) != ".zip")
            .OrderByDescending(e => e.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException("O .zip não contém nenhum ficheiro de Bíblia reconhecido.");

        if (entry.Length > MaxFileBytes)
            throw new InvalidDataException("Ficheiro dentro do .zip demasiado grande.");

        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);
        var inner = Parse(entry.Name, ms.ToArray());
        return inner.SuggestedAbbreviation is null
            ? inner with { SuggestedAbbreviation = SuggestAbbreviation(Path.GetFileNameWithoutExtension(fileName)) }
            : inner;
    }

    // ===================================================================
    // XML: deteta OSIS / Zefania / OpenSong pelo elemento raiz
    // ===================================================================

    private static (string, List<ParsedVerse>, string?, string?) ParseXml(byte[] content, List<string> issues)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using (var probe = XmlReader.Create(new MemoryStream(content), settings))
        {
            probe.MoveToContent();
            switch (probe.LocalName.ToLowerInvariant())
            {
                case "osis":
                    break;
                case "xmlbible":
                    return ParseZefania(content, settings, issues);
                case "bible":
                    return ("OpenSong", ParseOpenSong(content, settings, issues), null, null);
                default:
                    throw new InvalidDataException($"XML não reconhecido (<{probe.LocalName}>). Use OSIS, Zefania ou OpenSong.");
            }
        }
        return ParseOsis(content, settings, issues);
    }

    /// <summary>
    /// OSIS em streaming (ficheiros de ~8 MB). Suporta versículos contentor
    /// (&lt;verse osisID&gt;texto&lt;/verse&gt;) e milestone (&lt;verse sID/&gt;texto&lt;verse eID/&gt;).
    /// Ignora &lt;note&gt;, &lt;title&gt; e cabeçalho.
    /// </summary>
    private static (string, List<ParsedVerse>, string?, string?) ParseOsis(byte[] content, XmlReaderSettings settings, List<string> issues)
    {
        var verses = new List<ParsedVerse>(32000);
        string? title = null, identifier = null;
        string? currentRef = null;
        var buffer = new StringBuilder();
        var skipDepth = 0;         // dentro de <note>/<title>/<header>
        var containerDepth = -1;   // profundidade do <verse> contentor atual
        var unknownBooks = new HashSet<string>();

        void Flush()
        {
            if (currentRef is null) return;
            AddOsisVerse(verses, currentRef, buffer.ToString(), unknownBooks, issues);
            currentRef = null;
            buffer.Clear();
        }

        using var r = XmlReader.Create(new MemoryStream(content), settings);
        while (r.Read())
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    var name = r.LocalName;
                    if (name == "title" && skipDepth == 0 && r.Depth <= 4 && title is null && IsInWork(r))
                    {
                        title = r.ReadElementContentAsString().Trim();
                        continue;
                    }
                    if (name == "identifier" && identifier is null && IsInWork(r))
                    {
                        identifier = r.ReadElementContentAsString().Trim();
                        continue;
                    }
                    if (name is "note" or "title" or "header" && !r.IsEmptyElement)
                    {
                        skipDepth++;
                        continue;
                    }
                    if (name == "verse")
                    {
                        if (r.GetAttribute("eID") is not null)
                        {
                            Flush();
                        }
                        else if (r.GetAttribute("osisID") is { } id)
                        {
                            Flush();
                            currentRef = id;
                            if (r.GetAttribute("sID") is null && !r.IsEmptyElement)
                                containerDepth = r.Depth;
                        }
                    }
                    else if (name is "lb" or "l" && currentRef is not null && skipDepth == 0)
                    {
                        buffer.Append(' ');
                    }
                    break;

                case XmlNodeType.EndElement:
                    if (r.LocalName is "note" or "title" or "header" && skipDepth > 0)
                        skipDepth--;
                    else if (r.LocalName == "verse" && r.Depth == containerDepth)
                    {
                        Flush();
                        containerDepth = -1;
                    }
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (currentRef is not null && skipDepth == 0)
                        buffer.Append(r.Value);
                    break;
            }
        }
        Flush();

        if (unknownBooks.Count > 0)
            issues.Add($"Livros não reconhecidos (ignorados): {string.Join(", ", unknownBooks)}");

        return ("OSIS", verses, identifier is null ? null : SuggestAbbreviation(identifier), title);

        static bool IsInWork(XmlReader r) => r.Depth <= 5; // <osis><osisText><header><work><title>
    }

    private static void AddOsisVerse(List<ParsedVerse> verses, string osisRef, string text, HashSet<string> unknownBooks, List<string> issues)
    {
        // "Gen.1.1" ou intervalos "Gen.1.1 Gen.1.2" (versículos combinados) → usa o primeiro.
        var first = osisRef.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var parts = first.Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[1], out var ch) || !int.TryParse(parts[2], out var vs))
        {
            issues.Add($"Referência OSIS inválida: {osisRef}");
            return;
        }
        if (BibleBookResolver.Resolve(parts[0]) is not { } book)
        {
            unknownBooks.Add(parts[0]); // ex.: livros deuterocanónicos
            return;
        }
        verses.Add(new ParsedVerse(book, ch, vs, text));
    }

    private static (string, List<ParsedVerse>, string?, string?) ParseZefania(byte[] content, XmlReaderSettings settings, List<string> issues)
    {
        var doc = XDocument.Load(XmlReader.Create(new MemoryStream(content), settings));
        var root = doc.Root!;
        var info = root.Element("INFORMATION");
        var verses = new List<ParsedVerse>();
        var bookIndex = 0;

        foreach (var book in root.Elements("BIBLEBOOK"))
        {
            bookIndex++;
            var id = BibleBookResolver.Resolve(book.Attribute("bnumber")?.Value)
                     ?? BibleBookResolver.Resolve(book.Attribute("bname")?.Value)
                     ?? BibleBookResolver.Resolve(book.Attribute("bsname")?.Value);
            if (id is null)
            {
                issues.Add($"Livro não reconhecido: {book.Attribute("bname")?.Value ?? $"#{bookIndex}"}");
                continue;
            }

            foreach (var chapter in book.Elements("CHAPTER"))
            {
                if (!int.TryParse(chapter.Attribute("cnumber")?.Value, out var ch)) continue;
                foreach (var vers in chapter.Elements("VERS"))
                {
                    if (!int.TryParse(vers.Attribute("vnumber")?.Value, out var vs)) continue;
                    verses.Add(new ParsedVerse(id.Value, ch, vs, ElementText(vers, "NOTE", "DIV", "note")));
                }
            }
        }

        return ("Zefania", verses, info?.Element("identifier")?.Value, info?.Element("title")?.Value);
    }

    private static List<ParsedVerse> ParseOpenSong(byte[] content, XmlReaderSettings settings, List<string> issues)
    {
        var doc = XDocument.Load(XmlReader.Create(new MemoryStream(content), settings));
        var verses = new List<ParsedVerse>();
        var books = doc.Root!.Elements("b").ToList();
        for (var i = 0; i < books.Count; i++)
        {
            var name = books[i].Attribute("n")?.Value;
            var id = BibleBookResolver.Resolve(name) ?? (books.Count == 66 ? i + 1 : null);
            if (id is null)
            {
                issues.Add($"Livro não reconhecido: {name}");
                continue;
            }
            foreach (var c in books[i].Elements("c"))
            {
                if (!int.TryParse(c.Attribute("n")?.Value, out var ch)) continue;
                foreach (var v in c.Elements("v"))
                    if (int.TryParse(v.Attribute("n")?.Value, out var vs))
                        verses.Add(new ParsedVerse(id.Value, ch, vs, ElementText(v)));
            }
        }
        return verses;
    }

    private static string ElementText(XElement element, params string[] excluded) =>
        string.Concat(element.DescendantNodes().OfType<XText>()
            .Where(t => !t.Ancestors().TakeWhile(a => a != element).Any(a => excluded.Contains(a.Name.LocalName)))
            .Select(t => t.Value));

    // ===================================================================
    // JSON
    // ===================================================================

    private static List<ParsedVerse> ParseJson(byte[] content, List<string> issues)
    {
        var text = SongImportParser.Decode(content);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && TryProp(root, out var inner, "books", "livros", "verses", "versiculos"))
            root = inner;

        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("JSON esperado: uma lista de livros ou de versículos.");

        var verses = new List<ParsedVerse>();
        var items = root.EnumerateArray().ToList();
        if (items.Count == 0)
            return verses;

        // Formato 1: [{ "abbrev"/"name": "...", "chapters": [["v1","v2"], …] }, …]
        if (items[0].ValueKind == JsonValueKind.Object && TryProp(items[0], out _, "chapters", "capitulos"))
        {
            for (var i = 0; i < items.Count; i++)
            {
                var book = items[i];
                string? label = TryProp(book, out var n, "name", "nome", "book", "livro") ? n.ToString()
                              : TryProp(book, out var a, "abbrev", "sigla") ? a.ToString() : null;
                var id = BibleBookResolver.Resolve(label) ?? (items.Count == 66 ? i + 1 : null);
                if (id is null)
                {
                    issues.Add($"Livro não reconhecido: {label ?? $"#{i + 1}"}");
                    continue;
                }

                TryProp(book, out var chapters, "chapters", "capitulos");
                var ch = 0;
                foreach (var chapter in chapters.EnumerateArray())
                {
                    ch++;
                    var vs = 0;
                    foreach (var verse in chapter.EnumerateArray())
                    {
                        vs++;
                        verses.Add(new ParsedVerse(id.Value, ch, vs, verse.ValueKind == JsonValueKind.String ? verse.GetString()! : verse.ToString()));
                    }
                }
            }
            return verses;
        }

        // Formato 2: [{ "book": "Gn"|1, "chapter": 1, "verse": 1, "text": "…" }, …]
        foreach (var item in items)
        {
            if (!TryProp(item, out var b, "book", "livro", "book_id", "abbrev") ||
                !TryProp(item, out var c, "chapter", "capitulo") ||
                !TryProp(item, out var v, "verse", "versiculo") ||
                !TryProp(item, out var t, "text", "texto"))
                throw new InvalidDataException("Cada versículo precisa de book/chapter/verse/text.");

            if (BibleBookResolver.Resolve(b.ToString()) is { } id && int.TryParse(c.ToString(), out var ch) && int.TryParse(v.ToString(), out var vs))
                verses.Add(new ParsedVerse(id, ch, vs, t.GetString() ?? string.Empty));
            else
                issues.Add($"Versículo ignorado: {b} {c}:{v}");
        }
        return verses;
    }

    private static bool TryProp(JsonElement obj, out JsonElement value, params string[] names)
    {
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (names.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
        value = default;
        return false;
    }

    // ===================================================================
    // CSV/TSV e um-versículo-por-linha
    // ===================================================================

    private static List<ParsedVerse> ParseDelimited(string text, List<string> issues)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var header = lines.FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        var delimiter = new[] { '\t', ';', ',' }.MaxBy(d => header.Count(c => c == d));
        var verses = new List<ParsedVerse>();
        var badLines = 0;

        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
        {
            if (line.Trim().Length == 0) continue;
            var cols = SplitLimited(line, delimiter, 4);
            if (cols.Length < 4 || !int.TryParse(cols[1].Trim(), out var ch) || !int.TryParse(cols[2].Trim(), out var vs))
            {
                if (index > 0) badLines++; // a 1.ª linha pode ser cabeçalho
                continue;
            }
            if (BibleBookResolver.Resolve(cols[0].Trim('"', ' ')) is { } id)
                verses.Add(new ParsedVerse(id, ch, vs, cols[3].Trim().Trim('"').Replace("\"\"", "\"")));
            else
                badLines++;
        }

        if (badLines > 0)
            issues.Add($"{badLines} linha(s) ignorada(s) por formato ou livro inválido.");
        return verses;
    }

    private static List<ParsedVerse> ParseVpl(string text, List<string> issues)
    {
        var verses = new List<ParsedVerse>();
        var bad = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var m = VplRegex().Match(line);
            if (m.Success && BibleBookResolver.Resolve(m.Groups[1].Value) is { } id)
                verses.Add(new ParsedVerse(id, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Value));
            else
                bad++;
        }
        if (bad > 0)
            issues.Add($"{bad} linha(s) sem o formato \"Livro capítulo:versículo texto\" foram ignoradas.");
        return verses;
    }

    private static string[] SplitLimited(string line, char delimiter, int max)
    {
        // O texto (última coluna) pode conter o separador: só divide as 3 primeiras.
        var result = new List<string>(max);
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < line.Length && result.Count < max - 1; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == delimiter && !inQuotes)
            {
                result.Add(line[start..i]);
                start = i + 1;
            }
        }
        result.Add(line[start..]);
        return result.ToArray();
    }

    // ===================================================================

    private static ParsedBible Finish(List<ParsedVerse> raw, List<string> issues, string format, string? abbrev, string? name)
    {
        // Limpeza do texto e remoção de duplicados (fica o primeiro).
        var seen = new HashSet<(int, int, int)>();
        var verses = new List<ParsedVerse>(raw.Count);
        var dupes = 0;
        var empty = 0;
        foreach (var v in raw)
        {
            var text = WhitespaceRegex().Replace(v.Text, " ").Trim();
            if (v.Chapter < 1 || v.Verse < 1) continue;
            if (text.Length == 0) { empty++; continue; }
            if (!seen.Add((v.BookId, v.Chapter, v.Verse))) { dupes++; continue; }
            verses.Add(v with { Text = text });
        }
        if (dupes > 0) issues.Add($"{dupes} versículo(s) repetido(s) ignorado(s).");
        if (empty > 0) issues.Add($"{empty} versículo(s) sem texto ignorado(s).");
        if (verses.Count == 0) issues.Add("Nenhum versículo encontrado.");

        return new ParsedBible(verses, issues, format, abbrev, name?.Trim());
    }

    /// <summary>"bliv-tr_osis" → "BLIV-TR"; "Bible.pt.tr" → "TR"</summary>
    private static string? SuggestAbbreviation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var token = raw.Split('.', StringSplitOptions.RemoveEmptyEntries).Last();
        token = SuffixRegex().Replace(token, string.Empty);
        var clean = new string(token.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).ToUpperInvariant();
        return clean.Length is > 0 and <= 20 ? clean : clean.Length > 20 ? clean[..20] : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\s*(.+?)\s+(\d{1,3})\s*[:.,]\s*(\d{1,3})\s+(.+?)\s*$")]
    private static partial Regex VplRegex();

    [GeneratedRegex(@"[_-]?(osis|zefania|opensong|vpl|json|xml|bible|biblia)$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixRegex();
}
