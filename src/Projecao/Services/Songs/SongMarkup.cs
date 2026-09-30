using System.Text.RegularExpressions;

namespace Projecao.Services.Songs;

public enum MarkupLineKind
{
    Normal,
    /// <summary>Rótulo no início da estrofe: "Coro", "Coro 2x", "Final", "Cantar 2x".</summary>
    Label,
    /// <summary>"(H) …" — cantam os homens (varões).</summary>
    Men,
    /// <summary>"(M) …" — cantam as mulheres (irmãs).</summary>
    Women,
    /// <summary>Linha vazia forçada por "/" no início da linha.</summary>
    Blank
}

/// <param name="Color">Cor própria deste trecho ("[cor=#ffff00]…[/cor]" na letra), já validada.</param>
/// <param name="SourceLine">Linha do slide (texto original) de onde vem este trecho.</param>
/// <param name="SourceStart">Posição do trecho nessa linha do texto original (com marcas) — para colorir seleções.</param>
public sealed record MarkupSegment(string Text, bool IsParenthetical, string? Color = null, int SourceLine = 0, int SourceStart = 0);

public sealed record MarkupLine(MarkupLineKind Kind, IReadOnlyList<MarkupSegment> Segments);

public sealed record MarkupSlide(bool IsChorus, IReadOnlyList<MarkupLine> Lines);

/// <summary>
/// Sintaxe de letras do I-LOUVORES (ver "Tutorial - Editar &amp; Adicionar Louvores"):
///   • linha em branco separa slides;
///   • estrofe começada por "Coro" = refrão, repetido AUTOMATICAMENTE depois de cada estrofe seguinte;
///   • "*Coro" (com asterisco) só formata — o louvor passa a ser "manual" e nada é gerado;
///   • estrofe começada por "Final" é a última: não há refrão depois dela;
///   • "Cantar 2x", "Coro 2x", "(2x)" são anotações visíveis;
///   • "(H) " / "(M) " no início da linha = homens / mulheres (cores próprias);
///   • texto entre parênteses tem outra cor;
///   • "/" no início da linha = linha em branco extra DENTRO do mesmo slide (ex.: "/Instrumentos");
///   • "[cor=#ffff00]palavra[/cor]" = cor própria só nesse trecho (aba Editor: selecionar texto → cor).
/// Letras sem nenhuma destas marcas funcionam como texto normal (um slide por estrofe).
/// </summary>
public static partial class SongMarkup
{
    /// <summary>Divide a letra em slides, gerando as repetições automáticas do refrão.</summary>
    public static IReadOnlyList<string> BuildSlides(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
            return [];

        var stanzas = BlankLineRegex().Split(lyrics.Replace("\r\n", "\n").Replace('\r', '\n').Trim())
            .Select(s => string.Join('\n', s.Split('\n').Select(l => l.TrimEnd())).Trim('\n'))
            .Where(s => s.Trim().Length > 0)
            .ToList();

        // Um único "*Coro" torna o louvor manual: o operador escreveu todas as repetições.
        var manual = stanzas.Any(s => ManualChorusRegex().IsMatch(FirstLine(s)));

        var slides = new List<string>(stanzas.Count * 2);
        string? chorus = null;
        foreach (var stanza in stanzas)
        {
            var first = FirstLine(stanza);
            slides.Add(stanza);

            if (!manual && AutoChorusRegex().IsMatch(first))
            {
                chorus ??= stanza;
                continue;
            }

            if (FinalRegex().IsMatch(first))
                break; // "Final": último slide, sem refrão

            if (!manual && chorus is not null)
                slides.Add(chorus);
        }
        return slides;
    }

    /// <summary>Interpreta um slide para desenho (cores de refrão, vozes, parênteses…).</summary>
    public static MarkupSlide Parse(string slideText)
    {
        var raw = slideText.Replace("\r\n", "\n").Split('\n');
        var isChorus = raw.Length > 0 && ChorusLabelRegex().IsMatch(raw[0].Trim());
        var lines = new List<MarkupLine>(raw.Length + 2);

        for (var i = 0; i < raw.Length; i++)
        {
            var offset = raw[i].Length - raw[i].TrimStart().Length; // posição no texto original
            var line = raw[i].Trim();

            if (i == 0 && LabelRegex().IsMatch(line))
            {
                var shown = line.TrimStart('*').TrimStart();
                lines.Add(new MarkupLine(MarkupLineKind.Label, [new MarkupSegment(shown, false, null, i, offset + line.Length - shown.Length)]));
                continue;
            }

            if (line.StartsWith('/'))
            {
                lines.Add(new MarkupLine(MarkupLineKind.Blank, []));
                var rest = line[1..];
                offset += 1 + (rest.Length - rest.TrimStart().Length);
                line = rest.Trim();
                if (line.Length == 0) continue;
            }

            var kind = VoiceRegex().Match(StripColorTags(line)) is { Success: true } v
                ? (char.ToUpperInvariant(v.Groups[1].Value[0]) == 'H' ? MarkupLineKind.Men : MarkupLineKind.Women)
                : MarkupLineKind.Normal;

            lines.Add(new MarkupLine(kind, Segments(line, i, offset)));
        }
        return new MarkupSlide(isChorus, lines);
    }

    /// <summary>Texto simples (sem marcas), para pesquisa e pré-visualizações curtas.</summary>
    public static string ToPlainText(string slideText) =>
        string.Join('\n', Parse(slideText).Lines.Select(l => string.Concat(l.Segments.Select(s => s.Text))));

    /// <summary>Remove as marcas de cor (para pesquisa, títulos e comparações).</summary>
    public static string StripColorTags(string text) => ColorTagRegex().Replace(text, string.Empty);

    /// <summary>Divide a linha em trechos: cor própria ([cor=…]) e parênteses (cor dos parênteses).</summary>
    private static List<MarkupSegment> Segments(string line, int sourceLine, int baseOffset)
    {
        var result = new List<MarkupSegment>();
        string? color = null;
        var last = 0;
        foreach (Match tag in ColorTagRegex().Matches(line))
        {
            if (tag.Index > last) AddWithParens(result, line[last..tag.Index], color, sourceLine, baseOffset + last);
            color = tag.Groups[1].Success ? CssSafeColor(tag.Groups[1].Value) : null; // [/cor] fecha
            last = tag.Index + tag.Length;
        }
        if (last < line.Length) AddWithParens(result, line[last..], color, sourceLine, baseOffset + last);
        return result;
    }

    private static void AddWithParens(List<MarkupSegment> result, string text, string? color, int sourceLine, int start)
    {
        var last = 0;
        foreach (Match m in ParenRegex().Matches(text))
        {
            if (m.Index > last) result.Add(new MarkupSegment(text[last..m.Index], false, color, sourceLine, start + last));
            result.Add(new MarkupSegment(m.Value, true, color, sourceLine, start + m.Index));
            last = m.Index + m.Length;
        }
        if (last < text.Length) result.Add(new MarkupSegment(text[last..], false, color, sourceLine, start + last));
    }

    /// <summary>
    /// Aplica (ou tira, com hex = null) uma cor a um intervalo de um slide, dado em posições do texto
    /// original (linha, carácter) — as que o ecrã devolve ao selecionar com o rato.
    /// Funciona dentro de trechos já coloridos (fecha e reabre a cor à volta da seleção).
    /// </summary>
    public static string ApplyColor(string slideText, int startLine, int startOffset, int endLine, int endOffset, string? hex)
    {
        if (hex is not null && CssSafeColor(hex) is null)
            throw new ArgumentException("Cor inválida.", nameof(hex));

        var lines = slideText.Replace("\r\n", "\n").Split('\n');
        if (startLine > endLine || (startLine == endLine && startOffset > endOffset))
            (startLine, startOffset, endLine, endOffset) = (endLine, endOffset, startLine, startOffset);

        for (var i = Math.Max(0, startLine); i <= Math.Min(endLine, lines.Length - 1); i++)
        {
            var line = lines[i];
            var from = Math.Clamp(i == startLine ? startOffset : 0, 0, line.Length);
            var to = Math.Clamp(i == endLine ? endOffset : line.Length, 0, line.Length);
            if (i != endLine && i != startLine) { from = 0; to = line.Length; }
            if (from >= to) continue;

            var before = line[..from];
            var selected = StripColorTags(line[from..to]);
            var after = line[to..];

            // Cor que estava "aberta" no ponto da seleção: fecha antes e reabre depois.
            var open = OpenColorAt(before);
            var sb = new System.Text.StringBuilder(before);
            if (open is not null) sb.Append("[/cor]");

            var trimmed = selected.Trim();
            var lead = selected[..(selected.Length - selected.TrimStart().Length)];
            var trail = selected[selected.TrimEnd().Length..];
            if (hex is not null && trimmed.Length > 0)
                sb.Append(lead).Append($"[cor={hex}]").Append(trimmed).Append("[/cor]").Append(trail);
            else
                sb.Append(selected);

            if (open is not null) sb.Append($"[cor={open}]");
            sb.Append(after);
            lines[i] = CleanEmptyTags(sb.ToString());
        }
        return string.Join('\n', lines);
    }

    private static string? OpenColorAt(string textBefore)
    {
        string? open = null;
        foreach (Match m in ColorTagRegex().Matches(textBefore))
            open = m.Groups[1].Success ? m.Groups[1].Value : null;
        return open;
    }

    // "[cor=#fff][/cor]" vazios que ficam de sobra
    private static string CleanEmptyTags(string line) => EmptyTagRegex().Replace(line, string.Empty);

    [GeneratedRegex(@"\[cor=[^\]\s]{1,20}\]\s*\[/cor\]", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyTagRegex();

    // Só #RGB/#RRGGBB (as cores da paleta); qualquer outra coisa é ignorada (sem injeção de CSS).
    private static string? CssSafeColor(string value) => HexColorRegex().IsMatch(value) ? value : null;

    [GeneratedRegex(@"\[(?:cor=([^\]\s]{1,20})|/cor)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ColorTagRegex();

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColorRegex();

    private static string FirstLine(string stanza) => stanza.Split('\n', 2)[0].Trim();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLineRegex();

    // "Coro", "CORO 2X", "Refrão:" — sem asterisco → refrão com repetição automática
    [GeneratedRegex(@"^(coro|refr[aã]o)\b\s*(\d+\s*x)?\s*[:.]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AutoChorusRegex();

    [GeneratedRegex(@"^\*\s*(coro|refr[aã]o)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ManualChorusRegex();

    [GeneratedRegex(@"^\*?\s*(coro|refr[aã]o)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChorusLabelRegex();

    [GeneratedRegex(@"^\*?\s*final\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinalRegex();

    // Rótulos visíveis no topo da estrofe
    [GeneratedRegex(@"^\*?\s*((coro|refr[aã]o|final)\b.*|cantar\s+\d+\s*x.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^\(([HMhm])\)\s")]
    private static partial Regex VoiceRegex();

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex ParenRegex();
}
