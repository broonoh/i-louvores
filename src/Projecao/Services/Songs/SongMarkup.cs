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
/// <param name="SourceStart">Posição do trecho nessa linha do texto original (com marcas) — para colorir/redimensionar seleções.</param>
/// <param name="Size">Tamanho próprio deste trecho ("[tam=130]…[/tam]" = 130 % do tamanho normal), já validado.</param>
public sealed record MarkupSegment(string Text, bool IsParenthetical, string? Color = null, int SourceLine = 0, int SourceStart = 0, double? Size = null);

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
///   • "[cor=#ffff00]palavra[/cor]" = cor própria só nesse trecho (aba Editor: selecionar texto → cor);
///   • "[tam=130]palavra[/tam]" = tamanho próprio só nesse trecho, em % do tamanho normal (aba Editor/Configuração: selecionar texto → A−/A+).
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
        var manual = stanzas.Any(s => ManualChorusRegex().IsMatch(StripMarkTags(FirstLine(s))));

        var slides = new List<string>(stanzas.Count * 2);
        string? chorus = null;
        foreach (var stanza in stanzas)
        {
            var first = StripMarkTags(FirstLine(stanza));
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
        return slides.SelectMany(SplitLong).ToList();
    }

    /// <summary>Estrofes grandes ficam ilegíveis (letra minúscula, quase sem margem) — quebra em pedaços de
    /// no máximo <see cref="MaxLinesPerSlide"/> linhas. Nunca repete o rótulo ("Coro"…) nos pedaços seguintes:
    /// cada pedaço continua sendo um trecho exato do texto original (necessário para colorir/redimensionar
    /// trechos selecionados, que localizam o texto por substring).</summary>
    private const int MaxLinesPerSlide = 4;

    private static IEnumerable<string> SplitLong(string stanza)
    {
        var lines = stanza.Split('\n');
        if (lines.Length <= MaxLinesPerSlide)
        {
            yield return stanza;
            yield break;
        }

        for (var i = 0; i < lines.Length; i += MaxLinesPerSlide)
            yield return string.Join('\n', lines.Skip(i).Take(MaxLinesPerSlide));
    }

    /// <summary>Interpreta um slide para desenho (cores de refrão, vozes, parênteses…).</summary>
    public static MarkupSlide Parse(string slideText)
    {
        var raw = slideText.Replace("\r\n", "\n").Split('\n');
        var isChorus = raw.Length > 0 && ChorusLabelRegex().IsMatch(StripMarkTags(raw[0].Trim()));
        var lines = new List<MarkupLine>(raw.Length + 2);

        for (var i = 0; i < raw.Length; i++)
        {
            var offset = raw[i].Length - raw[i].TrimStart().Length; // posição no texto original
            var line = raw[i].Trim();

            // O rótulo é detetado pelo texto SEM marcas ("*[tam=130]Coro[/tam]" continua sendo rótulo),
            // mas o que fica guardado/desenhado passa por Segments() para a cor/tamanho do trecho valerem.
            if (i == 0 && LabelRegex().IsMatch(StripMarkTags(line)))
            {
                var shown = line.TrimStart('*').TrimStart();
                lines.Add(new MarkupLine(MarkupLineKind.Label, Segments(shown, i, offset + line.Length - shown.Length)));
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

            var kind = VoiceRegex().Match(StripMarkTags(line)) is { Success: true } v
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

    /// <summary>Remove as marcas de tamanho (para pesquisa, títulos e comparações).</summary>
    public static string StripSizeTags(string text) => SizeTagRegex().Replace(text, string.Empty);

    /// <summary>Remove cor e tamanho — para reconhecer rótulos ("Coro"/"Final"/vozes) mesmo com marcas dentro.</summary>
    private static string StripMarkTags(string text) => StripSizeTags(StripColorTags(text));

    /// <summary>Divide a linha em trechos: cor própria ([cor=…]), tamanho próprio ([tam=…]) e parênteses (cor dos parênteses).</summary>
    private static List<MarkupSegment> Segments(string line, int sourceLine, int baseOffset)
    {
        var result = new List<MarkupSegment>();
        string? color = null;
        double? size = null;
        var last = 0;
        foreach (Match tag in AnyTagRegex().Matches(line))
        {
            if (tag.Index > last) AddWithParens(result, line[last..tag.Index], color, size, sourceLine, baseOffset + last);
            if (tag.Groups["color"].Success) color = CssSafeColor(tag.Groups["color"].Value);
            else if (tag.Groups["colorClose"].Success) color = null;
            else if (tag.Groups["size"].Success) size = SafeSize(tag.Groups["size"].Value);
            else if (tag.Groups["sizeClose"].Success) size = null;
            last = tag.Index + tag.Length;
        }
        if (last < line.Length) AddWithParens(result, line[last..], color, size, sourceLine, baseOffset + last);
        return result;
    }

    private static void AddWithParens(List<MarkupSegment> result, string text, string? color, double? size, int sourceLine, int start)
    {
        var last = 0;
        foreach (Match m in ParenRegex().Matches(text))
        {
            if (m.Index > last) result.Add(new MarkupSegment(text[last..m.Index], false, color, sourceLine, start + last, size));
            result.Add(new MarkupSegment(m.Value, true, color, sourceLine, start + m.Index, size));
            last = m.Index + m.Length;
        }
        if (last < text.Length) result.Add(new MarkupSegment(text[last..], false, color, sourceLine, start + last, size));
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

        return ApplyTag(slideText, startLine, startOffset, endLine, endOffset, "cor", ColorTagRegex(), StripColorTags, hex);
    }

    /// <summary>
    /// Aumenta/diminui (delta, em pontos percentuais) o tamanho só do trecho selecionado de um slide.
    /// Parte do tamanho já aplicado a essa seleção (ou 100 % se não houver nenhum) e fica entre 50–300 %.
    /// Igual à <see cref="ApplyColor"/>, mas com "[tam=NN]…[/tam]".
    /// </summary>
    public static string ChangeSize(string slideText, int startLine, int startOffset, int endLine, int endOffset, int delta)
    {
        var lines = slideText.Replace("\r\n", "\n").Split('\n');
        if (startLine > endLine || (startLine == endLine && startOffset > endOffset))
            (startLine, startOffset, endLine, endOffset) = (endLine, endOffset, startLine, startOffset);

        // O ponto de partida é o tamanho já aberto no início da seleção (ou 100 %).
        var firstLine = Math.Clamp(startLine, 0, lines.Length - 1);
        var firstFrom = Math.Clamp(startOffset, 0, lines[firstLine].Length);
        var current = OpenTagAt(lines[firstLine][..firstFrom], SizeTagRegex()) is { } open ? int.Parse(open) : 100;
        var next = Math.Clamp(current + delta, 50, 300).ToString();

        return ApplyTag(slideText, startLine, startOffset, endLine, endOffset, "tam", SizeTagRegex(), StripSizeTags, next);
    }

    /// <summary>
    /// Núcleo comum a <see cref="ApplyColor"/> e <see cref="ChangeSize"/>: marca (ou tira, com value = null)
    /// um intervalo do slide com "[tagName=value]…[/tagName]", fechando e reabrindo uma marca já aberta
    /// no ponto da seleção.
    /// </summary>
    private static string ApplyTag(string slideText, int startLine, int startOffset, int endLine, int endOffset,
        string tagName, Regex tagRegex, Func<string, string> stripTags, string? value)
    {
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
            var selected = stripTags(line[from..to]);
            var after = line[to..];

            // Marca que estava "aberta" no ponto da seleção: fecha antes e reabre depois.
            var open = OpenTagAt(before, tagRegex);
            var sb = new System.Text.StringBuilder(before);
            if (open is not null) sb.Append($"[/{tagName}]");

            var trimmed = selected.Trim();
            var lead = selected[..(selected.Length - selected.TrimStart().Length)];
            var trail = selected[selected.TrimEnd().Length..];
            if (value is not null && trimmed.Length > 0)
                sb.Append(lead).Append($"[{tagName}={value}]").Append(trimmed).Append($"[/{tagName}]").Append(trail);
            else
                sb.Append(selected);

            if (open is not null) sb.Append($"[{tagName}={open}]");
            sb.Append(after);
            lines[i] = CleanEmptyTags(sb.ToString(), tagName);
        }
        return string.Join('\n', lines);
    }

    private static string? OpenTagAt(string textBefore, Regex tagRegex)
    {
        string? open = null;
        foreach (Match m in tagRegex.Matches(textBefore))
            open = m.Groups[1].Success ? m.Groups[1].Value : null;
        return open;
    }

    // "[cor=#fff][/cor]" / "[tam=130][/tam]" vazios que ficam de sobra
    private static string CleanEmptyTags(string line, string tagName) =>
        Regex.Replace(line, $@"\[{tagName}=[^\]\s]{{1,20}}\]\s*\[/{tagName}\]", string.Empty, RegexOptions.IgnoreCase);

    // Só 2–3 dígitos (50–300 %); qualquer outra coisa cai no padrão (100 %).
    private static double? SafeSize(string value) =>
        int.TryParse(value, out var pct) && pct is >= 50 and <= 300 ? pct / 100.0 : null;

    [GeneratedRegex(@"\[(?:tam=(\d{2,3})|/tam)\]", RegexOptions.IgnoreCase)]
    private static partial Regex SizeTagRegex();

    [GeneratedRegex(@"\[(?:cor=(?<color>[^\]\s]{1,20})|(?<colorClose>/cor)|tam=(?<size>\d{2,3})|(?<sizeClose>/tam))\]", RegexOptions.IgnoreCase)]
    private static partial Regex AnyTagRegex();

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
