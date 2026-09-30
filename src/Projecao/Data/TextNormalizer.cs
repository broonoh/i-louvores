using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Projecao.Data;

/// <summary>
/// Normaliza texto para pesquisa: minúsculas, sem acentos e com espaços colapsados.
/// "Coração  É\nTeu" → "coracao e teu".
/// </summary>
public static partial class TextNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }

        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Normalização para PESQUISA de louvores: além de minúsculas e sem acentos, ignora pontuação
    /// (vírgulas, pontos, aspas, parênteses, hífens, "*", "/"…) e trata "Ó", "Oh" e "Óh" como iguais.
    /// "ÓH NUNCA, NUNCA CESSARÃO!" → "o nunca nunca cessarao"; "minh'alma" → "minhalma".
    /// A mesma função é aplicada ao texto guardado e ao que se escreve na pesquisa.
    /// </summary>
    public static string NormalizeForSearch(string? value)
    {
        var text = Normalize(ColorTagRegex().Replace(value ?? string.Empty, string.Empty)); // [cor=…]…[/cor]
        if (text.Length == 0)
            return text;

        text = ApostropheRegex().Replace(text, string.Empty);       // minh'alma → minhalma
        text = PunctuationRegex().Replace(text, " ");               // , . ; : ! ? " ( ) - – * / …
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w == "oh" ? "o" : w);                      // ó / oh / óh → o
        return string.Join(' ', words);
    }

    /// <summary>Escapa os curingas do LIKE (% _ \) para pesquisa literal.</summary>
    public static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\[(?:cor=[^\]\s]{1,20}|/cor)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ColorTagRegex();

    [GeneratedRegex(@"['’‘`´]")]
    private static partial Regex ApostropheRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]+")]
    private static partial Regex PunctuationRegex();
}
