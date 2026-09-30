using Microsoft.EntityFrameworkCore;
using Projecao.Data;
using Projecao.Models;

namespace Projecao.Services.Import;

public enum ImportAction
{
    Create,
    /// <summary>Já existe e a versão do ficheiro é diferente: vai substituir.</summary>
    Update,
    Skip,
    /// <summary>Já existe e é igual: nada a fazer.</summary>
    Unchanged
}

public sealed record ImportPlanItem(ImportedSong Song, string? Collection, ImportAction Action, int? ExistingId, string? Note);

public sealed record ImportResult(int Created, int Updated, int Skipped);

/// <summary>Resumo para a validação antes de gravar.</summary>
public sealed record ImportSummary(int Create, int Update, int Unchanged, int Skip)
{
    public static ImportSummary Of(IReadOnlyList<ImportPlanItem> plan) => new(
        plan.Count(p => p.Action == ImportAction.Create), plan.Count(p => p.Action == ImportAction.Update),
        plan.Count(p => p.Action == ImportAction.Unchanged), plan.Count(p => p.Action == ImportAction.Skip));
}

/// <summary>
/// Decide o que fazer com cada louvor lido (criar, atualizar, ignorar) e grava tudo
/// numa única transação — ou entra tudo, ou nada.
///
/// Identidade de um louvor: (coletânea, número) quando há número; senão (coletânea, título normalizado).
/// </summary>
public sealed class SongImporter(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<IReadOnlyList<ImportPlanItem>> PlanAsync(
        IReadOnlyList<ImportedSong> songs, string? defaultCollection, bool updateExisting, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Songs.AsNoTracking()
            .Select(s => new { s.Id, s.Collection, s.Number, s.NormalizedTitle, s.Lyrics, s.Title, s.AltNumber, s.Author, s.MusicalKey, s.Rhythm, s.Category })
            .ToListAsync(ct);
        var byId = existing.ToDictionary(e => e.Id);

        var byNumber = existing.Where(e => e.Number != null)
            .GroupBy(e => NumberKey(e.Collection, e.Number!.Value)).ToDictionary(g => g.Key, g => g.First().Id);
        var byTitle = existing
            .GroupBy(e => TitleKey(e.Collection, e.NormalizedTitle)).ToDictionary(g => g.Key, g => g.First().Id);

        var seen = new Dictionary<string, string>(); // chave → letra normalizada
        var plan = new List<ImportPlanItem>(songs.Count);
        defaultCollection = string.IsNullOrWhiteSpace(defaultCollection) ? null : defaultCollection.Trim();

        foreach (var original in songs)
        {
            var song = original;
            var collection = song.Collection ?? defaultCollection;
            var titleKey = TitleKey(collection, TextNormalizer.NormalizeForSearch(song.Title));
            var key = song.Number is { } n ? NumberKey(collection, n) : titleKey;
            var lyricsKey = TextNormalizer.Normalize(song.Lyrics);

            if (seen.TryGetValue(key, out var otherLyrics))
            {
                if (otherLyrics == lyricsKey)
                {
                    plan.Add(new ImportPlanItem(song, collection, ImportAction.Skip, null, "Repetido nos ficheiros (letra igual)"));
                    continue;
                }

                // Mesmo título, letra diferente (ex.: duas versões no Glorifica): importa as duas.
                for (var i = 2; ; i++)
                {
                    var candidate = song with { Title = $"{original.Title} ({i})", Number = null };
                    var candidateKey = TitleKey(collection, TextNormalizer.NormalizeForSearch(candidate.Title));
                    if (!seen.ContainsKey(candidateKey))
                    {
                        song = candidate;
                        titleKey = key = candidateKey;
                        break;
                    }
                }
            }
            seen[key] = lyricsKey;

            int? existingId = song.Number is { } num && byNumber.TryGetValue(NumberKey(collection, num), out var idN) ? idN
                : byTitle.TryGetValue(titleKey, out var idT) ? idT
                : null;

            var note = song.Title != original.Title ? "Título repetido com letra diferente — importado como nova versão" : null;
            if (existingId is null)
            {
                plan.Add(new ImportPlanItem(song, collection, ImportAction.Create, null, note));
                continue;
            }

            var current = byId[existingId.Value];
            var same = SameText(current.Lyrics, song.Lyrics) && current.Title == song.Title
                       && (song.Number ?? current.Number) == current.Number
                       && (song.AltNumber ?? current.AltNumber) == current.AltNumber
                       && (song.Author ?? current.Author) == current.Author
                       && (song.MusicalKey ?? current.MusicalKey) == current.MusicalKey
                       && (song.Rhythm ?? current.Rhythm) == current.Rhythm
                       && (song.Category ?? current.Category) == current.Category;

            plan.Add(same
                ? new ImportPlanItem(song, collection, ImportAction.Unchanged, existingId, "Igual ao que já existe")
                : updateExisting
                    ? new ImportPlanItem(song, collection, ImportAction.Update, existingId, Describe(current.Title, current.Lyrics, song))
                    : new ImportPlanItem(song, collection, ImportAction.Skip, existingId, "Já existe (versão diferente — marque \"Atualizar\" para substituir)"));
        }

        return plan;
    }

    public async Task<ImportResult> ApplyAsync(IReadOnlyList<ImportPlanItem> plan, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var updateIds = plan.Where(p => p.Action == ImportAction.Update).Select(p => p.ExistingId!.Value).ToList();
        var toUpdate = await db.Songs.Where(s => updateIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        int created = 0, updated = 0;
        foreach (var item in plan)
        {
            var s = item.Song;
            switch (item.Action)
            {
                case ImportAction.Create:
                    db.Songs.Add(new Song
                    {
                        Title = s.Title, Lyrics = s.Lyrics, Collection = item.Collection, Number = s.Number, AltNumber = s.AltNumber,
                        Author = s.Author, Category = s.Category, MusicalKey = s.MusicalKey, Rhythm = s.Rhythm
                    });
                    created++;
                    break;

                case ImportAction.Update when toUpdate.TryGetValue(item.ExistingId!.Value, out var song):
                    // Letra e título substituídos; metadados só quando o ficheiro os traz.
                    song.Title = s.Title;
                    song.Lyrics = s.Lyrics;
                    song.Collection = item.Collection;
                    song.Number = s.Number ?? song.Number;
                    song.AltNumber = s.AltNumber ?? song.AltNumber;
                    song.Author = s.Author ?? song.Author;
                    song.Category = s.Category ?? song.Category;
                    song.MusicalKey = s.MusicalKey ?? song.MusicalKey;
                    song.Rhythm = s.Rhythm ?? song.Rhythm;
                    updated++;
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(created, updated, plan.Count - created - updated);
    }

    /// <summary>Compara letras ignorando espaços e quebras de linha no fim das linhas.</summary>
    private static bool SameText(string a, string b) =>
        string.Join('\n', a.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim()
        == string.Join('\n', b.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();

    private static string Describe(string oldTitle, string oldLyrics, ImportedSong song)
    {
        var changes = new List<string>();
        if (oldTitle != song.Title) changes.Add($"título: \"{oldTitle}\" → \"{song.Title}\"");
        if (!SameText(oldLyrics, song.Lyrics))
        {
            var oldStanzas = SongImportParser.SplitStanzas(oldLyrics).Count;
            var newStanzas = SongImportParser.SplitStanzas(song.Lyrics).Count;
            changes.Add(oldStanzas == newStanzas ? "letra alterada" : $"letra alterada ({oldStanzas} → {newStanzas} estrofes)");
        }
        if (changes.Count == 0) changes.Add("dados alterados (nº/tom/autor)");
        return "Será substituído: " + string.Join("; ", changes);
    }

    private static string NumberKey(string? collection, int number) => $"{TextNormalizer.Normalize(collection)}|#{number}";

    private static string TitleKey(string? collection, string normalizedTitle) => $"{TextNormalizer.Normalize(collection)}|{normalizedTitle}";
}
