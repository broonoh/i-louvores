using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Projecao.Data;
using Projecao.Models;

namespace Projecao.Services.Songs;

/// <summary>
/// Pesquisa de louvores partilhada pelas abas Louvores, Editor e Configuração:
///   • "12"                → nº 12 (ou nº alternativo) de todas as coletâneas;
///   • "CIAS 12", "12 CIAS", "coletânea 2018 12" → nº 12 só nessa coletânea;
///   • resto               → título (e letra, se pedido), ignorando acentos, pontuação e "oh".
/// </summary>
public static partial class SongSearch
{
    public sealed record NumberQuery(int Number, IReadOnlyList<string>? Collections);

    public static async Task<IQueryable<Song>> FilterAsync(AppDbContext db, IQueryable<Song> q, string? query, bool inLyrics,
        CancellationToken ct = default)
    {
        var term = TextNormalizer.NormalizeForSearch(query);
        if (term.Length == 0)
            return q;

        var collections = char.IsDigit(term[0]) || char.IsDigit(term[^1])
            ? await db.Songs.Where(s => s.Collection != null).Select(s => s.Collection!).Distinct().ToListAsync(ct)
            : [];

        if (ParseNumberQuery(term, collections) is { } nq)
        {
            q = q.Where(s => s.Number == nq.Number || s.AltNumber == nq.Number);
            if (nq.Collections is { } names)
                q = q.Where(s => s.Collection != null && names.Contains(s.Collection));
            return q;
        }

        var pattern = $"%{TextNormalizer.EscapeLike(term)}%";
        return inLyrics
            ? q.Where(s => EF.Functions.Like(s.NormalizedTitle, pattern, "\\") || EF.Functions.Like(s.NormalizedLyrics, pattern, "\\"))
            : q.Where(s => EF.Functions.Like(s.NormalizedTitle, pattern, "\\"));
    }

    /// <summary>
    /// Interpreta um termo já normalizado como número (com ou sem coletânea).
    /// Devolve null quando não é pesquisa por número — ex.: "salmo 23" sem coletânea "Salmo",
    /// ou "coletanea 2018" (é o nome da coletânea, não o nº 2018).
    /// </summary>
    public static NumberQuery? ParseNumberQuery(string term, IReadOnlyList<string> collections)
    {
        if (int.TryParse(term, out var only))
            return new NumberQuery(only, null);

        var m = CollectionThenNumberRegex().Match(term);
        if (!m.Success) m = NumberThenCollectionRegex().Match(term);
        if (!m.Success) return null;

        var normalized = collections.Select(c => (Name: c, Norm: TextNormalizer.NormalizeForSearch(c))).ToList();
        if (normalized.Any(c => c.Norm.Contains(term, StringComparison.Ordinal)))
            return null;

        var words = m.Groups["c"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = normalized
            .Where(c => words.All(w => c.Norm.Split(' ').Any(cw => cw.StartsWith(w, StringComparison.Ordinal))))
            .Select(c => c.Name)
            .ToList();

        return matches.Count > 0 ? new NumberQuery(int.Parse(m.Groups["n"].Value), matches) : null;
    }

    [GeneratedRegex(@"^(?<c>.*?\D)\s*(?<n>\d{1,4})$")]
    private static partial Regex CollectionThenNumberRegex();

    [GeneratedRegex(@"^(?<n>\d{1,4})\s*(?<c>\D.*)$")]
    private static partial Regex NumberThenCollectionRegex();
}
