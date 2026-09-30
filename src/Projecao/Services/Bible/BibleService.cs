using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Projecao.Data;
using Projecao.Models;
using Projecao.Models.Projection;

namespace Projecao.Services.Bible;

public sealed record BibleVersionInfo(int Id, string Abbreviation, string Name, string? Copyright, int VerseCount, int BookCount);

public sealed record BibleImportResult(int VersionId, int Verses, TimeSpan Elapsed);

/// <summary>
/// Versões da Bíblia: importação (rápida, numa transação), consultas para o módulo
/// Bíblia e instalação da Bíblia Livre embutida na primeira execução.
/// </summary>
public sealed class BibleService(IDbContextFactory<AppDbContext> dbFactory, SettingsStore settings, ILogger<BibleService> logger,
    ProjectionService? projection = null)
{
    public const string BundledResource = "Projecao.Resources.Bible.bliv-tr_osis.zip";
    public const string BundledAbbreviation = "BLIVRE";
    public const string BundledName = "Bíblia Livre (Textus Receptus)";
    public const string BundledCopyright =
        "Bíblia Livre (BLIVRE), Copyright © Diego Santos, Mario Sérgio e Marco Teles, " +
        "http://sites.google.com/site/biblialivre/ - fevereiro de 2018. Licença Creative Commons Atribuição 3.0 Brasil " +
        "(http://creativecommons.org/licenses/by/3.0/br/). Reprodução permitida desde que devidamente mencionados fonte e autores.";

    /// <summary>Disparado depois de importar/apagar versões (a aba Bíblia recarrega).</summary>
    public event Action? VersionsChanged;

    public async Task<IReadOnlyList<BibleVersionInfo>> GetVersionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BibleVersions.AsNoTracking()
            .OrderBy(v => v.Abbreviation)
            .Select(v => new BibleVersionInfo(v.Id, v.Abbreviation, v.Name, v.Copyright,
                db.BibleVerses.Count(x => x.VersionId == v.Id),
                db.BibleVerses.Where(x => x.VersionId == v.Id).Select(x => x.BookId).Distinct().Count()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BibleBook>> GetBooksAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BibleBooks.AsNoTracking().OrderBy(b => b.Id).ToListAsync(ct);
    }

    /// <summary>Livros que têm texto nesta versão (versões só com NT, por exemplo).</summary>
    public async Task<HashSet<int>> GetBooksWithTextAsync(int versionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.BibleVerses.Where(v => v.VersionId == versionId).Select(v => v.BookId).Distinct().ToListAsync(ct)).ToHashSet();
    }

    public async Task<int> GetChapterCountAsync(int versionId, int bookId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BibleVerses.Where(v => v.VersionId == versionId && v.BookId == bookId)
            .MaxAsync(v => (int?)v.Chapter, ct) ?? 0;
    }

    public async Task<IReadOnlyList<BibleVerse>> GetChapterAsync(int versionId, int bookId, int chapter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BibleVerses.AsNoTracking()
            .Where(v => v.VersionId == versionId && v.BookId == bookId && v.Chapter == chapter)
            .OrderBy(v => v.Verse)
            .ToListAsync(ct);
    }

    public async Task<BibleVersion?> GetVersionAsync(int versionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BibleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
    }

    public async Task<bool> ExistsAsync(string abbreviation, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = abbreviation.Trim().ToUpperInvariant();
        return await db.BibleVersions.AnyAsync(v => v.Abbreviation == key, ct);
    }

    /// <summary>
    /// Grava uma versão. Com <paramref name="replace"/>, substitui a versão com a mesma sigla.
    /// Tudo numa transação com INSERT preparado (~31 mil versículos em cerca de 1 s).
    /// </summary>
    public async Task<BibleImportResult> ImportAsync(ParsedBible parsed, string abbreviation, string name, string? copyright,
        bool replace, CancellationToken ct = default)
    {
        abbreviation = abbreviation.Trim().ToUpperInvariant();
        name = name.Trim();
        if (abbreviation.Length is 0 or > 20) throw new ArgumentException("A sigla deve ter entre 1 e 20 caracteres.");
        if (name.Length is 0 or > 150) throw new ArgumentException("O nome deve ter entre 1 e 150 caracteres.");
        if (parsed.Verses.Count == 0) throw new InvalidOperationException("Não há versículos para importar.");

        var sw = Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        long? existingId;
        await using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM BibleVersions WHERE Abbreviation = $a";
            find.Parameters.AddWithValue("$a", abbreviation);
            existingId = (long?)await find.ExecuteScalarAsync(ct);
        }

        if (existingId is not null && !replace)
            throw new InvalidOperationException($"Já existe uma versão com a sigla {abbreviation}. Marque \"Substituir\" para a atualizar.");

        long versionId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$c", (object?)copyright ?? DBNull.Value);
            if (existingId is { } id)
            {
                cmd.CommandText = "DELETE FROM BibleVerses WHERE VersionId = $id; UPDATE BibleVersions SET Name = $n, Copyright = $c WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct);
                versionId = id;
            }
            else
            {
                cmd.CommandText = "INSERT INTO BibleVersions (Abbreviation, Name, Language, Copyright) VALUES ($a, $n, 'pt', $c); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$a", abbreviation);
                versionId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO BibleVerses (VersionId, BookId, Chapter, Verse, Text) VALUES ($v, $b, $c, $n, $t)";
            var pV = insert.Parameters.Add("$v", SqliteType.Integer);
            var pB = insert.Parameters.Add("$b", SqliteType.Integer);
            var pC = insert.Parameters.Add("$c", SqliteType.Integer);
            var pN = insert.Parameters.Add("$n", SqliteType.Integer);
            var pT = insert.Parameters.Add("$t", SqliteType.Text);
            insert.Prepare();

            pV.Value = versionId;
            foreach (var verse in parsed.Verses)
            {
                pB.Value = verse.BookId;
                pC.Value = verse.Chapter;
                pN.Value = verse.Verse;
                pT.Value = verse.Text;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        sw.Stop();
        logger.LogInformation("Bíblia {Abbrev} importada: {Count} versículos em {Ms} ms", abbreviation, parsed.Verses.Count, sw.ElapsedMilliseconds);
        VersionsChanged?.Invoke();
        return new BibleImportResult((int)versionId, parsed.Verses.Count, sw.Elapsed);
    }

    /// <summary>
    /// Procura texto numa versão (para encontrar um versículo com erro). Sem distinguir
    /// maiúsculas; acentos têm de coincidir. Devolve no máximo <paramref name="limit"/> resultados.
    /// </summary>
    public async Task<IReadOnlyList<BibleVerse>> SearchTextAsync(int versionId, string term, int limit = 200, CancellationToken ct = default)
    {
        term = term.Trim();
        if (term.Length < 2)
            return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pattern = $"%{TextNormalizer.EscapeLike(term)}%";
        return await db.BibleVerses.AsNoTracking()
            .Where(v => v.VersionId == versionId && EF.Functions.Like(v.Text, pattern, "\\"))
            .OrderBy(v => v.BookId).ThenBy(v => v.Chapter).ThenBy(v => v.Verse)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Corrige o texto de versículos (aba Editor). Numa só transação; texto vazio não é aceite.
    /// Se o capítulo estiver em leitura na fila/telão, é atualizado na hora.
    /// </summary>
    public async Task<int> UpdateVersesAsync(IReadOnlyDictionary<int, string> newTextById, CancellationToken ct = default)
    {
        if (newTextById.Count == 0)
            return 0;

        foreach (var (_, text) in newTextById)
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Um versículo não pode ficar vazio.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = newTextById.Keys.ToList();
        var verses = await db.BibleVerses.Where(v => ids.Contains(v.Id)).ToListAsync(ct);
        var changed = 0;
        foreach (var verse in verses)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(newTextById[verse.Id], @"\s+", " ").Trim();
            if (text == verse.Text) continue;
            verse.Text = text;
            changed++;
        }
        await db.SaveChangesAsync(ct);

        if (changed > 0)
        {
            logger.LogInformation("{Count} versículo(s) corrigido(s)", changed);
            foreach (var chapter in verses.Select(v => (v.VersionId, v.BookId, v.Chapter)).Distinct())
                RefreshReadingInQueue(chapter.VersionId, chapter.BookId, chapter.Chapter);
        }
        return changed;
    }

    /// <summary>Leituras desse capítulo na fila passam a mostrar o texto corrigido (mesmo item, mesmo slide).</summary>
    private void RefreshReadingInQueue(int versionId, int bookId, int chapter)
    {
        if (projection is null) return;
        foreach (var item in projection.State.Queue.Where(q => q.Reading is { } r && r.VersionId == versionId && r.BookId == bookId && r.Chapter == chapter).ToList())
        {
            using var db = dbFactory.CreateDbContext();
            var version = db.BibleVersions.AsNoTracking().First(v => v.Id == versionId);
            var book = db.BibleBooks.AsNoTracking().First(b => b.Id == bookId);
            var verses = db.BibleVerses.AsNoTracking().Where(v => v.VersionId == versionId && v.BookId == bookId && v.Chapter == chapter)
                .OrderBy(v => v.Verse).ToList();
            var fresh = QueueItemFactory.FromVerses(version, book, chapter, verses, item.Background, reading: true);
            projection.ReplaceItem(fresh with { Id = item.Id });
        }
    }

    public async Task DeleteVersionAsync(int versionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.BibleVerses.Where(v => v.VersionId == versionId).ExecuteDeleteAsync(ct);
        await db.BibleVersions.Where(v => v.Id == versionId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        VersionsChanged?.Invoke();
    }

    /// <summary>
    /// Capítulo vizinho de uma leitura (+1 seguinte, −1 anterior), atravessando livros:
    /// Malaquias 4 → Mateus 1; Mateus 1 ← Malaquias 4. Null no início de Gênesis / fim de Apocalipse.
    /// Síncrono de propósito: é chamado pelo ProjectionService num clique em Próximo/Anterior (consulta de ~ms).
    /// </summary>
    public QueueItem? GetAdjacentChapter(QueueItem item, int direction)
    {
        if (item.Reading is not { } r)
            return null;

        using var db = dbFactory.CreateDbContext();
        var version = db.BibleVersions.AsNoTracking().FirstOrDefault(v => v.Id == r.VersionId);
        if (version is null)
            return null;

        int bookId = r.BookId, chapter = r.Chapter + direction;
        var lastChapter = db.BibleVerses.Where(v => v.VersionId == r.VersionId && v.BookId == bookId).Max(v => (int?)v.Chapter) ?? 0;

        if (chapter < 1 || chapter > lastChapter)
        {
            // Livro vizinho que tenha texto nesta versão (ex.: versões só com o NT)
            var books = db.BibleVerses.Where(v => v.VersionId == r.VersionId).Select(v => v.BookId).Distinct().ToList();
            int? nextBook = direction > 0 ? books.Where(b => b > r.BookId).Order().Cast<int?>().FirstOrDefault()
                                          : books.Where(b => b < r.BookId).OrderDescending().Cast<int?>().FirstOrDefault();
            if (nextBook is null)
                return null;

            bookId = nextBook.Value;
            chapter = direction > 0 ? 1 : db.BibleVerses.Where(v => v.VersionId == r.VersionId && v.BookId == bookId).Max(v => v.Chapter);
        }

        var book = db.BibleBooks.AsNoTracking().First(b => b.Id == bookId);
        var verses = db.BibleVerses.AsNoTracking()
            .Where(v => v.VersionId == r.VersionId && v.BookId == bookId && v.Chapter == chapter)
            .OrderBy(v => v.Verse).ToList();

        return verses.Count == 0 ? null : QueueItemFactory.FromVerses(version, book, chapter, verses, item.Background, reading: true);
    }

    /// <summary>
    /// Instala a Bíblia Livre embutida na 1.ª execução. Se o utilizador a apagar depois,
    /// não volta a ser instalada (fica registado nas configurações).
    /// </summary>
    public async Task EnsureBundledBibleAsync(CancellationToken ct = default)
    {
        if (settings.BundledBibleInstalled)
            return;

        if (await ExistsAsync(BundledAbbreviation, ct))
        {
            settings.BundledBibleInstalled = true;
            return;
        }

        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledResource);
        if (stream is null)
        {
            logger.LogWarning("Bíblia Livre embutida não encontrada no executável");
            return;
        }

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var parsed = BibleImportParser.Parse("bliv-tr_osis.zip", ms.ToArray());
        await ImportAsync(parsed, BundledAbbreviation, BundledName, BundledCopyright, replace: false, ct);
        settings.BundledBibleInstalled = true;
    }
}
