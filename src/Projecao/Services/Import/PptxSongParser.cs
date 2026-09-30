using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Projecao.Services.Import;

/// <summary>
/// Importa louvores de apresentações PowerPoint (.pptx) usadas nas igrejas (ex.: "COLETÂNEA … EDIÇÃO 2018",
/// "LOUVORES AVULSOS"). Não há marcadores de título no PowerPoint, por isso a estrutura é lida pela posição:
///   • título  = caixa de UMA linha no topo do slide (y &lt; 7 %) → início de um novo louvor
///               ("01 – O SANGUE DE JESUS TEM PODER" → nº 1 + título);
///   • letra   = caixas de várias linhas → um slide por estrofe (linhas em branco dentro da caixa
///               ficam no mesmo slide, com "/");
///   • anotações pequenas ao lado da letra (BIS, 2X, VARÕES, SERVAS, H, M…) são colocadas na linha
///               à mesma altura: "(BIS)" no fim; "(H) " / "(M) " no início;
///   • "Índice", "ATUALIZAÇÃO …", capa e slides de índice antes do 1.º louvor são ignorados.
/// "CORO" passa a "*CORO": nas apresentações os refrões já estão repetidos slide a slide.
/// </summary>
public static partial class PptxSongParser
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const double TitleMaxY = 0.07;

    private sealed record Shape(string Text, double X, double Y, double W, double H)
    {
        public string[] Lines => Text.Split('\n');
        public bool SingleLine => !Text.Contains('\n');
    }

    public static ParseResult Parse(byte[] content, string source)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var issues = new List<ImportIssue>();

        var presentation = Load(zip, "ppt/presentation.xml");
        var size = presentation.Root!.Element(P + "sldSz");
        double slideW = double.Parse(size?.Attribute("cx")?.Value ?? "12192000", CultureInfo.InvariantCulture);
        double slideH = double.Parse(size?.Attribute("cy")?.Value ?? "6858000", CultureInfo.InvariantCulture);

        var rels = Load(zip, "ppt/_rels/presentation.xml.rels").Root!.Elements(Rel + "Relationship")
            .ToDictionary(r => r.Attribute("Id")!.Value, r => r.Attribute("Target")!.Value);
        var slidePaths = presentation.Root!.Element(P + "sldIdLst")?.Elements(P + "sldId")
            .Select(s => rels.GetValueOrDefault(s.Attribute(R + "id")?.Value ?? ""))
            .Where(t => t is not null)
            .Select(t => "ppt/" + t!.TrimStart('/').Replace("../", ""))
            .ToList() ?? [];

        var collection = GuessCollection(source);
        var songs = new List<ImportedSong>();
        string? title = null;
        int? number = null;
        var stanzas = new List<string>();
        var songStartSlide = 0;

        void Flush()
        {
            if (title is null) return;
            if (stanzas.Count == 0)
                issues.Add(new ImportIssue($"{source} (slide {songStartSlide})", $"\"{title}\" não tem letra — ignorado."));
            else
                songs.Add(new ImportedSong(title, string.Join("\n\n", stanzas), $"{source} (slide {songStartSlide})",
                    Number: number, Collection: collection));
            title = null; number = null; stanzas.Clear();
        }

        for (var i = 0; i < slidePaths.Count; i++)
        {
            var shapes = ReadShapes(zip, slidePaths[i], slideW, slideH);

            // Capa: descobre a coletânea ("COLETÂNEA … EDIÇÃO 2018")
            if (i == 0 && CoverRegex().Match(string.Join(" ", shapes.Select(s => s.Text))) is { Success: true } cover)
                collection = $"Coletânea {cover.Groups[1].Value}";

            // Título: 1 linha no topo; ou até 2 linhas se começar por número ("11 – CLAMO A TI… / (UMA NOVA CANÇÃO)").
            var titleShape = shapes.Where(s => s.Y < TitleMaxY && !IsNoise(s.Text) && !IsAnnotation(s)
                                               && (s.SingleLine || (s.Lines.Length <= 2 && NumberedTitleRegex().IsMatch(Clean(s.Lines[0])))))
                                   .OrderBy(s => s.Y).FirstOrDefault();
            if (titleShape is not null)
            {
                Flush();
                (number, title) = SplitNumber(Clean(titleShape.Text.Replace('\n', ' ')));
                songStartSlide = i + 1;
            }

            if (title is null) continue; // capa / índice antes do 1.º louvor

            var body = shapes.Where(s => s != titleShape && !IsNoise(s.Text)).ToList();
            var lyricBoxes = body.Where(s => !IsAnnotation(s)).OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
            var annotations = body.Where(IsAnnotation).ToList();
            if (lyricBoxes.Count == 0) continue;

            var stanza = BuildStanza(lyricBoxes, annotations);
            if (stanza.Length > 0) stanzas.Add(stanza);
        }
        Flush();

        if (songs.Count == 0)
            issues.Add(new ImportIssue(source, "Não encontrei louvores (títulos no topo dos slides)."));

        return new ParseResult(songs, issues);
    }

    /// <summary>Junta as caixas de letra de um slide e aplica as anotações (BIS, H/M…) à linha à mesma altura.</summary>
    private static string BuildStanza(List<Shape> lyricBoxes, List<Shape> annotations)
    {
        // Linhas com a sua altura aproximada no slide, para poder casar as anotações.
        var lines = new List<(string Text, double Y)>();
        foreach (var box in lyricBoxes)
        {
            var boxLines = box.Lines;
            for (var k = 0; k < boxLines.Length; k++)
                lines.Add((boxLines[k].TrimEnd(), box.Y + box.H * (k + 0.5) / boxLines.Length));
        }

        foreach (var note in annotations)
        {
            var label = Clean(note.Text).ToUpperInvariant().Trim('(', ')', ' ');
            var y = note.Y + note.H / 2;
            var target = Enumerable.Range(0, lines.Count).Where(k => lines[k].Text.Trim().Length > 0)
                .OrderBy(k => Math.Abs(lines[k].Y - y)).DefaultIfEmpty(-1).First();
            if (target < 0) continue;

            var (text, ly) = lines[target];
            lines[target] = label switch
            {
                "VARÕES" or "VAROES" or "H" or "HOMENS" => ($"(H) {text.TrimStart()}", ly),
                "SERVAS" or "M" or "MULHERES" or "IRMÃS" => ($"(M) {text.TrimStart()}", ly),
                "T" or "TODOS" => (text, ly),
                _ => ($"{text} ({label})", ly)
            };
        }

        // Linhas em branco dentro do slide → "/" na linha seguinte (fica tudo no mesmo slide).
        var sb = new StringBuilder();
        var pendingBlank = false;
        foreach (var (raw, _) in lines)
        {
            var line = WhitespaceRegex().Replace(raw, " ").Trim();
            if (line.Length == 0) { pendingBlank = sb.Length > 0; continue; }
            if (sb.Length > 0) sb.Append('\n');
            if (pendingBlank) sb.Append('/');
            pendingBlank = false;
            sb.Append(line);
        }

        // Refrões já estão escritos slide a slide: "CORO" → "*CORO" (sem repetição automática).
        var result = sb.ToString();
        return ChorusLabelRegex().Replace(result, "*$1");
    }

    private static List<Shape> ReadShapes(ZipArchive zip, string path, double slideW, double slideH)
    {
        var xml = Load(zip, path);
        var shapes = new List<Shape>();
        foreach (var sp in xml.Descendants(P + "sp"))
        {
            var paragraphs = sp.Descendants(A + "p")
                .Select(p => string.Concat(p.Descendants().Where(e => e.Name == A + "t" || e.Name == A + "br")
                    .Select(e => e.Name == A + "br" ? "\n" : e.Value)))
                .ToList();
            var text = string.Join("\n", paragraphs).Replace("\r", "").Trim('\n', ' ');
            if (text.Trim().Length == 0) continue;

            var xfrm = sp.Descendants(A + "xfrm").FirstOrDefault();
            var off = xfrm?.Element(A + "off");
            var ext = xfrm?.Element(A + "ext");
            if (off is null || ext is null) continue;

            shapes.Add(new Shape(text,
                Num(off, "x") / slideW, Num(off, "y") / slideH, Num(ext, "cx") / slideW, Num(ext, "cy") / slideH));
        }
        return shapes;
    }

    private static double Num(XElement e, string attr) =>
        double.TryParse(e.Attribute(attr)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static XDocument Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException($"Ficheiro PowerPoint incompleto ({path}).");
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>Anotação curta ao lado da letra: BIS, 2X, 3X, VARÕES, SERVAS, H, M, T, "BIS NO FINAL"…</summary>
    private static bool IsAnnotation(Shape s) =>
        s.SingleLine && s.Text.Trim().Length <= 14 && AnnotationRegex().IsMatch(Clean(s.Text));

    private static bool IsNoise(string text)
    {
        var t = Clean(text).ToUpperInvariant();
        return t is "ÍNDICE" or "INDICE" || t.StartsWith("ATUALIZAÇÃO", StringComparison.Ordinal) || t.StartsWith("ATUALIZACAO", StringComparison.Ordinal);
    }

    private static (int? Number, string Title) SplitNumber(string title)
    {
        var m = NumberedTitleRegex().Match(title);
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value.Trim()) : (null, title);
    }

    private static string? GuessCollection(string fileName)
    {
        var n = fileName.ToUpperInvariant();
        return n.Contains("AVULSO") ? "Avulsos" : null;
    }

    private static string Clean(string s) => WhitespaceRegex().Replace(s.Replace(' ', ' '), " ").Trim();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(\d{1,4})\s*[-–—.]\s*(.+)$")]
    private static partial Regex NumberedTitleRegex();

    [GeneratedRegex(@"^\(?\s*(BIS|\d\s*X|VAR[ÕO]ES|SERVAS|[HMT]|HOMENS|MULHERES|IRMÃS|TODOS|BIS NO FINAL|\d\s*X\s*FINAL)\s*\)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AnnotationRegex();

    [GeneratedRegex(@"COLET[ÂA]NEA.*?EDI[ÇC][ÃA]O\s*(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex CoverRegex();

    // "CORO", "CORO 2X", "CORO:" no início de uma estrofe (sem asterisco)
    [GeneratedRegex(@"^(CORO\b[^\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ChorusLabelRegex();
}
