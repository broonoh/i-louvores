using Microsoft.EntityFrameworkCore;
using Projecao.Data;
using Projecao.Models;
using Projecao.Models.Projection;

namespace Projecao.Services.Songs;

/// <summary>CRUD de louvores (aba Editor). Ao gravar, atualiza o louvor se estiver na fila de projeção.</summary>
public sealed class SongService(IDbContextFactory<AppDbContext> dbFactory, ProjectionService projection, MediaLibraryService media)
{
    /// <summary>Louvores criados/alterados/apagados (as abas Louvores e Editor recarregam).</summary>
    public event Action? SongsChanged;

    public async Task<IReadOnlyList<Song>> SearchAsync(string? query, int take = 100_000, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = await SongSearch.FilterAsync(db, db.Songs.AsNoTracking(), query, inLyrics: true, ct);
        return await q.OrderBy(s => s.Collection).ThenBy(s => s.Number).ThenBy(s => s.Title).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Songs.Where(s => s.Collection != null).Select(s => s.Collection!).Distinct().OrderBy(c => c).ToListAsync(ct);
    }

    /// <summary>Outro louvor com o mesmo número na mesma coletânea (aviso, não bloqueia).</summary>
    public async Task<Song?> FindNumberConflictAsync(Song song, CancellationToken ct = default)
    {
        if (song.Number is null) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id != song.Id && s.Number == song.Number && s.Collection == song.Collection, ct);
    }

    public async Task<Song> SaveAsync(Song input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        Song entity;
        if (input.Id == 0)
        {
            entity = new Song { Title = input.Title };
            db.Songs.Add(entity);
        }
        else
        {
            entity = await db.Songs.FirstOrDefaultAsync(s => s.Id == input.Id, ct)
                     ?? throw new InvalidOperationException("Este louvor já não existe (foi apagado noutra janela?).");
        }

        entity.Title = input.Title.Trim();
        entity.Number = input.Number;
        entity.AltNumber = input.AltNumber;
        entity.Collection = Blank(input.Collection);
        entity.Author = Blank(input.Author);
        entity.Category = Blank(input.Category);
        entity.MusicalKey = Blank(input.MusicalKey);
        entity.Rhythm = Blank(input.Rhythm);
        entity.BackgroundFile = Blank(input.BackgroundFile);
        entity.FirstSlideBackgroundFile = Blank(input.FirstSlideBackgroundFile);
        entity.Lyrics = (input.Lyrics ?? string.Empty).Replace("\r\n", "\n").TrimEnd();

        await db.SaveChangesAsync(ct);
        RefreshInQueue(entity);
        SongsChanged?.Invoke();
        return entity;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Songs.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        SongsChanged?.Invoke();
    }

    /// <summary>Letra corrigida → o item na fila (e o projetor, se estiver no ar) passa a mostrar a versão nova.</summary>
    private void RefreshInQueue(Song song)
    {
        bool Matches(QueueItem q) => q.Kind == ProjectionContentKind.Song && q.SourceId == song.Id;
        var state = projection.State;
        var candidates = state.Queue.Where(Matches);
        if (state.AdHoc is { } adHoc && Matches(adHoc))
            candidates = candidates.Append(adHoc);

        foreach (var item in candidates.ToList())
        {
            var fresh = QueueItemFactory.FromSong(song, media.Resolve(song.BackgroundFile), media.Resolve(song.FirstSlideBackgroundFile));
            projection.ReplaceItem(fresh with { Id = item.Id });
        }
    }

    private static void Validate(Song s)
    {
        if (string.IsNullOrWhiteSpace(s.Title)) throw new ArgumentException("O título é obrigatório.");
        if (s.Title.Trim().Length > 200) throw new ArgumentException("O título tem mais de 200 caracteres.");
        if (s.Number is < 0 || s.AltNumber is < 0) throw new ArgumentException("Os números não podem ser negativos.");
        if (s.MusicalKey?.Length > 10) throw new ArgumentException("Tom: máximo 10 caracteres (ex.: G, Am, F#).");
        if (s.Rhythm?.Length > 50) throw new ArgumentException("Ritmo: máximo 50 caracteres.");
        if (s.Collection?.Length > 100 || s.Category?.Length > 100) throw new ArgumentException("Coletânea/categoria: máximo 100 caracteres.");
        if (s.Author?.Length > 200) throw new ArgumentException("Autor: máximo 200 caracteres.");
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
