using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Projecao.Data;

namespace Projecao.Services.Import;

/// <summary>Louvor lido de um ficheiro, ainda não gravado.</summary>
public sealed record ImportedSong(
    string Title,
    string Lyrics,
    string Source,
    int? Number = null,
    int? AltNumber = null,
    string? Collection = null,
    string? Author = null,
    string? Category = null,
    string? MusicalKey = null,
    string? Rhythm = null)
{
    public int StanzaCount => SongImportParser.SplitStanzas(Lyrics).Count;
}

public sealed record ImportIssue(string Source, string Message);

public sealed record ParseResult(IReadOnlyList<ImportedSong> Songs, IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// Lê louvores de TXT, CSV e OpenLyrics (XML). Não toca na base de dados.
///
/// TXT — um ou mais louvores por ficheiro, separados por uma linha "---" ou "===":
/// <code>
/// Título: Nome do louvor        ← cabeçalho opcional (Número, Nº 2, Autor, Categoria,
/// Número: 12                       Tom, Ritmo, Coletânea); sem ele, a 1.ª linha é o
/// Tom: G                           título e aceita "12 - Nome do louvor"
///
/// 1.ª estrofe, linha 1
/// 1.ª estrofe, linha 2
///
/// 2.ª estrofe…                  ← linha em branco = novo slide
/// </code>
/// </summary>
public static partial class SongImportParser
{
    public const int MaxFileBytes = 200 * 1024 * 1024; // apresentações PowerPoint podem ter dezenas de MB

    static SongImportParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static readonly string[] SupportedExtensions = [".txt", ".csv", ".xml", ".pptx"];

    public static ParseResult Parse(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".txt" => ParseText(Decode(content), fileName),
                ".csv" => ParseCsv(Decode(content), fileName),
                ".xml" => ParseOpenLyrics(content, fileName),
                ".pptx" => PptxSongParser.Parse(content, fileName),
                _ => new ParseResult([], [new ImportIssue(fileName, $"Formato não suportado ({ext}). Use .txt, .csv, .pptx (PowerPoint) ou .xml (OpenLyrics).")])
            };
        }
        catch (Exception ex) when (ex is XmlException or FormatException or InvalidDataException)
        {
            return new ParseResult([], [new ImportIssue(fileName, $"Ficheiro inválido: {ex.Message}")]);
        }
    }

    /// <summary>UTF-8 (com ou sem BOM); se não for UTF-8 válido, Windows-1252 (ficheiros antigos do Windows).</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    // ===================================================================
    // TXT
    // ===================================================================

    public static ParseResult ParseText(string text, string source)
    {
        var songs = new List<ImportedSong>();
        var issues = new List<ImportIssue>();
        var blocks = SongSeparatorRegex().Split(NormalizeNewlines(text));

        for (var b = 0; b < blocks.Length; b++)
        {
            var lines = blocks[b].Split('\n').ToList();
            TrimBlankEdges(lines);
            if (lines.Count == 0)
                continue;

            var label = blocks.Length > 1 ? $"{source} (#{b + 1})" : source;
            var fields = new Dictionary<string, string>();

            // Cabeçalho "Chave: valor" (só chaves conhecidas — versos com ":" não são confundidos).
            var i = 0;
            while (i < lines.Count && HeaderRegex().Match(lines[i]) is { Success: true } m && KeyFor(m.Groups[1].Value) is { } key)
            {
                fields[key] = m.Groups[2].Value.Trim();
                i++;
            }

            if (!fields.ContainsKey("titulo"))
            {
                while (i < lines.Count && string.IsNullOrWhiteSpace(lines[i])) i++;
                if (i >= lines.Count)
                {
                    issues.Add(new ImportIssue(label, "Louvor sem título nem letra — ignorado."));
                    continue;
                }

                var first = lines[i++].Trim();
                if (NumberedTitleRegex().Match(first) is { Success: true } nm)
                {
                    fields.TryAdd("numero", nm.Groups[1].Value);
                    first = nm.Groups[2].Value.Trim();
                }
                fields["titulo"] = first;
            }

            var lyrics = CleanLyrics(string.Join('\n', lines.Skip(i)));
            AddSong(songs, issues, label, fields, lyrics);
        }

        return new ParseResult(songs, issues);
    }

    // ===================================================================
    // I-LOUVORES "raw" (Documentos/I-LOUVORES/Louvores/raw)
    // ===================================================================

    /// <summary>
    /// Formato I-LOUVORES: um .txt por louvor, título = nome do ficheiro, letra com a
    /// sintaxe do I-LOUVORES (mantida tal como está). Metadados opcionais no LouvoresRaw.ini:
    /// [Título] Nome=… NumeroAntigo=… NumeroNovo=… Tom=… Ritimo=… Autor=… ("N/D" = vazio).
    /// </summary>
    public static ParseResult ParseOneFilePerSong(IEnumerable<(string FileName, byte[] Content)> files, byte[]? louvoresRawIni)
    {
        var meta = louvoresRawIni is null ? [] : ReadIni(Decode(louvoresRawIni));
        var byTitle = meta.Values
            .Select(section => (Key: TextNormalizer.Normalize(section.GetValueOrDefault("nome") ?? string.Empty), Section: section))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Section);

        var songs = new List<ImportedSong>();
        var issues = new List<ImportIssue>();
        foreach (var (fileName, content) in files)
        {
            if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                continue;

            var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
            var key = TextNormalizer.Normalize(stem);
            var section = meta.GetValueOrDefault(stem) ?? byTitle.GetValueOrDefault(key) ?? new Dictionary<string, string>();

            string? Meta(string k) => section.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)
                                      && !v.Trim().Equals("N/D", StringComparison.OrdinalIgnoreCase) ? v.Trim() : null;

            var fields = new Dictionary<string, string> { ["titulo"] = Meta("nome") ?? stem };
            void Put(string target, string? value) { if (value is not null) fields[target] = value; }
            Put("numero", Meta("numeronovo"));
            Put("numero2", Meta("numeroantigo"));
            Put("tom", Meta("tom"));
            Put("ritmo", Meta("ritimo") ?? Meta("ritmo"));
            Put("autor", Meta("autor"));

            AddSong(songs, issues, fileName, fields, CleanLyrics(Decode(content)));
        }
        return new ParseResult(songs, issues);
    }

    /// <summary>INI simples: [secção] chave=valor (chaves em minúsculas).</summary>
    public static Dictionary<string, Dictionary<string, string>> ReadIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in NormalizeNewlines(text).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[line[1..^1].Trim()] = current;
            }
            else if (current is not null && line.IndexOf('=') is var eq and > 0)
            {
                current[line[..eq].Trim().ToLowerInvariant()] = line[(eq + 1)..].Trim();
            }
        }
        return result;
    }

    // ===================================================================
    // CSV
    // ===================================================================

    /// <summary>
    /// CSV com cabeçalho. Colunas reconhecidas (sem distinguir maiúsculas/acentos):
    /// titulo*, letra*, numero, numero2, coletanea, autor, categoria, tom, ritmo.
    /// A letra pode ter quebras de linha reais (campo entre aspas) ou "\n" literal.
    /// </summary>
    public static ParseResult ParseCsv(string text, string source)
    {
        var songs = new List<ImportedSong>();
        var issues = new List<ImportIssue>();
        text = NormalizeNewlines(text);

        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ';', ',', '\t' }.MaxBy(d => firstLine.Count(c => c == d));
        var rows = ReadCsv(text, delimiter);
        if (rows.Count == 0)
            return new ParseResult(songs, [new ImportIssue(source, "Ficheiro vazio.")]);

        var header = rows[0].Select(h => KeyFor(h)).ToArray();
        if (!header.Contains("titulo") || !header.Contains("letra"))
            return new ParseResult(songs, [new ImportIssue(source, "O cabeçalho do CSV precisa das colunas \"titulo\" e \"letra\".")]);

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace))
                continue;

            var fields = new Dictionary<string, string>();
            for (var c = 0; c < header.Length && c < row.Count; c++)
                if (header[c] is { } key && !string.IsNullOrWhiteSpace(row[c]))
                    fields[key] = row[c].Trim();

            var lyrics = CleanLyrics((fields.GetValueOrDefault("letra") ?? string.Empty).Replace("\\n", "\n"));
            AddSong(songs, issues, $"{source} (linha {r + 1})", fields, lyrics);
        }

        return new ParseResult(songs, issues);
    }

    /// <summary>Leitor RFC 4180 (aspas, aspas duplicadas, quebras de linha dentro de aspas).</summary>
    private static List<List<string>> ReadCsv(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else field.Append(c);
        }

        if (inQuotes)
            throw new FormatException("aspas por fechar no CSV.");

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    // ===================================================================
    // OpenLyrics (http://openlyrics.org) — OpenLP, FreeWorship, etc.
    // ===================================================================

    public static ParseResult ParseOpenLyrics(byte[] content, string source)
    {
        // Sem DTD nem recursos externos (proteção contra XXE / "billion laughs").
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(content), settings);
        var doc = XDocument.Load(reader);

        var root = doc.Root ?? throw new InvalidDataException("XML vazio.");
        if (root.Name.LocalName != "song")
            return new ParseResult([], [new ImportIssue(source, "Não é um ficheiro OpenLyrics (<song>).")]);

        XNamespace ns = root.Name.Namespace;
        var props = root.Element(ns + "properties");
        var fields = new Dictionary<string, string>();

        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields[key] = value.Trim();
        }

        Put("titulo", props?.Element(ns + "titles")?.Elements(ns + "title").FirstOrDefault()?.Value);
        Put("autor", string.Join(", ", props?.Element(ns + "authors")?.Elements(ns + "author").Select(a => a.Value.Trim()) ?? []));
        Put("tom", props?.Element(ns + "key")?.Value);
        Put("ritmo", props?.Element(ns + "tempo")?.Value);
        Put("categoria", props?.Element(ns + "themes")?.Elements(ns + "theme").FirstOrDefault()?.Value);
        var songbook = props?.Element(ns + "songbooks")?.Elements(ns + "songbook").FirstOrDefault();
        Put("coletanea", songbook?.Attribute("name")?.Value);
        Put("numero", songbook?.Attribute("entry")?.Value);

        // Cada <lines> vira uma estrofe (um slide). <br/> = quebra de linha; acordes são ignorados.
        var verses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var verse in root.Element(ns + "lyrics")?.Elements(ns + "verse") ?? [])
        {
            var name = verse.Attribute("name")?.Value ?? $"v{order.Count + 1}";
            var stanzas = verse.Elements(ns + "lines").Select(l => LinesText(l, ns)).Where(t => t.Length > 0).ToList();
            verses[name] = stanzas;
            order.Add(name);
        }

        // verseOrder ("v1 c v2 c") define a ordem de execução, com o refrão repetido.
        var verseOrder = props?.Element(ns + "verseOrder")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sequence = verseOrder is { Length: > 0 } && verseOrder.All(verses.ContainsKey) ? verseOrder.ToList() : order;

        var lyrics = string.Join("\n\n", sequence.SelectMany(v => verses[v]));
        var songs = new List<ImportedSong>();
        var issues = new List<ImportIssue>();
        AddSong(songs, issues, source, fields, CleanLyrics(lyrics));
        return new ParseResult(songs, issues);
    }

    private static string LinesText(XElement lines, XNamespace ns)
    {
        var sb = new StringBuilder();
        foreach (var node in lines.DescendantNodes())
        {
            if (node is XText text && text.Parent?.Name.LocalName != "comment")
                sb.Append(text.Value);
            else if (node is XElement { Name.LocalName: "br" })
                sb.Append('\n');
        }
        return string.Join('\n', sb.ToString().Split('\n').Select(l => WhitespaceRegex().Replace(l, " ").Trim())).Trim();
    }

    // ===================================================================
    // Comum
    // ===================================================================

    /// <summary>Divide a letra em estrofes (mesma regra do Song.GetStanzas).</summary>
    public static IReadOnlyList<string> SplitStanzas(string lyrics) =>
        BlankLineRegex().Split(lyrics).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static void AddSong(List<ImportedSong> songs, List<ImportIssue> issues, string source,
        Dictionary<string, string> f, string lyrics)
    {
        var title = f.GetValueOrDefault("titulo");
        if (string.IsNullOrWhiteSpace(title))
        {
            issues.Add(new ImportIssue(source, "Sem título — ignorado."));
            return;
        }
        if (string.IsNullOrWhiteSpace(lyrics))
            issues.Add(new ImportIssue(source, $"\"{title}\" não tem letra (será importado só com o título)."));

        songs.Add(new ImportedSong(
            Title: title.Length <= 200 ? title : title[..200],
            Lyrics: lyrics,
            Source: source,
            Number: ParseInt(f.GetValueOrDefault("numero")),
            AltNumber: ParseInt(f.GetValueOrDefault("numero2")),
            Collection: Truncate(f.GetValueOrDefault("coletanea"), 100),
            Author: Truncate(f.GetValueOrDefault("autor"), 200),
            Category: Truncate(f.GetValueOrDefault("categoria"), 100),
            MusicalKey: Truncate(f.GetValueOrDefault("tom"), 10),
            Rhythm: Truncate(f.GetValueOrDefault("ritmo"), 50)));
    }

    /// <summary>Mapeia nomes de campo (pt/en, com ou sem acentos) para a chave interna.</summary>
    private static string? KeyFor(string rawKey) =>
        TextNormalizer.Normalize(rawKey).Replace(" ", "").Replace("º", "").Replace("°", "").Replace(".", "") switch
        {
            "titulo" or "title" or "nome" or "nomedolouvor" => "titulo",
            "numero" or "n" or "no" or "num" or "number" or "numero1" or "n1" => "numero",
            "numero2" or "n2" or "no2" or "num2" => "numero2",
            "autor" or "author" or "autores" => "autor",
            "categoria" or "category" or "tema" => "categoria",
            "tom" or "tonalidade" or "key" => "tom",
            "ritmo" or "tempo" or "rhythm" => "ritmo",
            "coletanea" or "hinario" or "colecao" or "songbook" or "livro" => "coletanea",
            "letra" or "lyrics" => "letra",
            _ => null
        };

    private static string CleanLyrics(string text)
    {
        var lines = NormalizeNewlines(text).Split('\n').Select(l => l.TrimEnd()).ToList();
        TrimBlankEdges(lines);
        return ManyBlankLinesRegex().Replace(string.Join('\n', lines), "\n\n");
    }

    private static void TrimBlankEdges(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"^\s*(?:-{3,}|={3,})\s*$", RegexOptions.Multiline)]
    private static partial Regex SongSeparatorRegex();

    [GeneratedRegex(@"^\s*([\p{L}º°. ]{1,20}?\s*\d?)\s*:\s*(.*)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^(\d{1,5})\s*[-–—.)]\s*(.+)$")]
    private static partial Regex NumberedTitleRegex();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLinesRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
