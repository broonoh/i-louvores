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
        var text = Normalize(ColorTagRegex().Replace(value ?? string.Empty, string.Empty)); // [cor=…]…[/cor] / [tam=…]…[/tam]
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

    private static readonly Dictionary<char, string> AccentClasses = new()
    {
        ['a'] = "[aàáâã]", ['e'] = "[eèéê]", ['i'] = "[iìíî]", ['o'] = "[oòóôõ]", ['u'] = "[uùúû]", ['c'] = "[cç]",
    };

    /// <summary>
    /// Regex (para destacar resultados) equivalente à tolerância de <see cref="NormalizeForSearch"/>:
    /// ignora maiúsculas/acentos letra a letra e aceita qualquer pontuação entre as palavras do termo,
    /// tratando "o"/"ó"/"oh"/"óh" como iguais. Devolve "" se o termo normalizar para vazio.
    /// </summary>
    public static string LoosePattern(string term)
    {
        var words = NormalizeForSearch(term).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return string.Empty;

        string WordPattern(string w) =>
            w == "o"
                ? $"(?:{string.Concat(w.Select(CharClass))}|oh|óh)"
                : string.Concat(w.Select(CharClass));

        return string.Join(@"[\s,.;:!?""'()\-–*/]*", words.Select(WordPattern));
    }

    private static string CharClass(char c) => AccentClasses.TryGetValue(c, out var cls) ? cls : Regex.Escape(c.ToString());

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\[(?:cor=[^\]\s]{1,20}|/cor|tam=\d{2,3}|/tam)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ColorTagRegex();

    [GeneratedRegex(@"['’‘`´]")]
    private static partial Regex ApostropheRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]+")]
    private static partial Regex PunctuationRegex();
}
