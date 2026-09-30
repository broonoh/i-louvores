using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Services;
using Projecao.Services.Backup;
using Projecao.Services.Import;
using Projecao.Services.Songs;
using Xunit;

public class UpdateAndScaleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly AppPaths _paths;
    private readonly DbContextOptions<AppDbContext> _options;

    public UpdateAndScaleTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "docs"), Path.Combine(_root, "data"));
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_paths.DatabasePath}").Options;
        using var db = new AppDbContext(_options);
        DbInitializer.Initialize(db);
    }

    private static List<ImportedSong> Collection(int count, Func<int, string>? lyrics = null) =>
        Enumerable.Range(1, count).Select(n => new ImportedSong($"LOUVOR {n}", lyrics?.Invoke(n) ?? $"ESTROFE A {n}\n\nESTROFE B {n}", "f.pptx",
            Number: n, Collection: "Coletânea 2026")).ToList();

    [Fact]
    public async Task Update_validation_detects_changed_unchanged_and_new_with_safety_copy()
    {
        var importer = new SongImporter(new Factory(_options));
        await importer.ApplyAsync(await importer.PlanAsync(Collection(5), null, updateExisting: true));

        // Nova versão da coletânea: nº 2 com letra corrigida, nº 4 com estrofe a mais, nº 6 novo
        var v2 = Collection(6, n => n switch
        {
            2 => "ESTROFE A 2 CORRIGIDA\n\nESTROFE B 2",
            4 => "ESTROFE A 4\n\nESTROFE B 4\n\nESTROFE C 4",
            _ => $"ESTROFE A {n}\n\nESTROFE B {n}"
        });
        var plan = await importer.PlanAsync(v2, null, updateExisting: true);
        Assert.Equal(new ImportSummary(Create: 1, Update: 2, Unchanged: 3, Skip: 0), ImportSummary.Of(plan));
        Assert.Contains("2 → 3 estrofes", plan.Single(p => p.Song.Number == 4).Note);

        // Sem "Atualizar", as versões diferentes são só ignoradas (com aviso)
        var noUpdate = await importer.PlanAsync(v2, null, updateExisting: false);
        Assert.Equal(2, ImportSummary.Of(noUpdate).Skip);

        var copy = new BackupService(_paths, NullLogger<BackupService>.Instance).CreateSafetyCopy("atualizacao-louvores");
        Assert.True(File.Exists(copy));

        var r = await importer.ApplyAsync(plan);
        Assert.Equal((1, 2), (r.Created, r.Updated));
        await using var db = new AppDbContext(_options);
        Assert.Equal("ESTROFE A 2 CORRIGIDA\n\nESTROFE B 2", db.Songs.Single(s => s.Number == 2).Lyrics);
        Assert.Equal(6 + 1, db.Songs.Count()); // + louvor de exemplo
        SqliteConnection.ClearAllPools();

        // A cópia de segurança tem a versão antiga (dá para desfazer)
        await using var old = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={copy}").Options);
        Assert.Equal("ESTROFE A 2\n\nESTROFE B 2", old.Songs.Single(s => s.Number == 2).Lyrics);
    }

    [Fact]
    public async Task Handles_more_than_3000_songs_quickly()
    {
        var importer = new SongImporter(new Factory(_options));
        var sw = Stopwatch.StartNew();
        await importer.ApplyAsync(await importer.PlanAsync(Collection(3500), null, updateExisting: true));
        var firstImport = sw.Elapsed;

        sw.Restart();
        var plan = await importer.PlanAsync(Collection(3500, n => n % 100 == 0 ? $"NOVA LETRA {n}" : $"ESTROFE A {n}\n\nESTROFE B {n}"), null, updateExisting: true);
        var r = await importer.ApplyAsync(plan);
        var update = sw.Elapsed;
        Assert.Equal(35, r.Updated);

        var songs = new SongService(new Factory(_options), new ProjectionService(NullLogger<ProjectionService>.Instance),
            new MediaLibraryService(_paths));
        sw.Restart();
        var all = await songs.SearchAsync(null);
        var byNumber = await songs.SearchAsync("2999");
        var byText = await songs.SearchAsync("nova letra 1200");
        var search = sw.Elapsed;

        Assert.Equal(3501, all.Count);                                  // sem limite de 500/5000
        Assert.Equal("LOUVOR 2999", Assert.Single(byNumber).Title);
        Assert.Equal("LOUVOR 1200", Assert.Single(byText).Title);
        Assert.True(firstImport < TimeSpan.FromSeconds(20), $"importar 3500: {firstImport}");
        Assert.True(update < TimeSpan.FromSeconds(20), $"atualizar 3500: {update}");
        Assert.True(search < TimeSpan.FromSeconds(5), $"pesquisas: {search}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
