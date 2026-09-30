using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Models;
using Projecao.Services;
using Projecao.Services.Backup;
using Projecao.Services.Bible;
using Xunit;

public class BackupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    private AppPaths Paths(string name) => new(Path.Combine(_root, name, "docs"), Path.Combine(_root, name, "data"));

    private static DbContextOptions<AppDbContext> Options(AppPaths p) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={p.DatabasePath}").Options;

    [Fact]
    public async Task Export_then_restore_on_another_computer()
    {
        // Computador A: louvor + versão da Bíblia + imagem na Galeria
        var a = Paths("A");
        await using (var db = new AppDbContext(Options(a)))
        {
            DbInitializer.Initialize(db);
            db.Songs.Add(new Song { Title = "Só do computador A", Lyrics = "x" });
            db.BibleVersions.Add(new BibleVersion { Abbreviation = "ARC", Name = "Almeida" });
            await db.SaveChangesAsync();
        }
        File.WriteAllBytes(Path.Combine(a.GalleryFolder, "fundo.jpg"), [1, 2, 3]);
        new SettingsStore(a).GalleryBackground = "fundo.jpg";

        var result = await new BackupService(a, NullLogger<BackupService>.Instance).ExportAsync(includeGallery: true);
        Assert.True(File.Exists(result.FilePath));
        Assert.Contains("ARC", result.Manifest.BibleVersions);
        Assert.Equal(1, result.Manifest.GalleryFiles);
        SqliteConnection.ClearAllPools();

        // Computador B: base diferente
        var b = Paths("B");
        await using (var db = new AppDbContext(Options(b))) DbInitializer.Initialize(db);
        SqliteConnection.ClearAllPools();

        var backupB = new BackupService(b, NullLogger<BackupService>.Instance);
        await using (var zip = File.OpenRead(result.FilePath))
        {
            var manifest = await backupB.PrepareRestoreAsync(zip);
            Assert.Equal(2, manifest.Songs);
        }
        Assert.True(backupB.HasPendingRestore);

        // "Próximo arranque"
        Assert.True(BackupService.ApplyPendingRestore(b));
        Assert.False(backupB.HasPendingRestore);
        await using (var db = new AppDbContext(Options(b)))
        {
            Assert.True(db.Songs.Any(s => s.Title == "Só do computador A"));
            Assert.True(db.BibleVersions.Any(v => v.Abbreviation == "ARC"));
        }
        Assert.Equal("fundo.jpg", new SettingsStore(b).GalleryBackground);
        Assert.True(File.Exists(Path.Combine(b.GalleryFolder, "fundo.jpg")));
        Assert.Single(Directory.GetFiles(b.DataFolder, "i-louvores.db.antes-do-restauro-*")); // base anterior guardada
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Rejects_foreign_or_malicious_zip()
    {
        var p = Paths("C");
        var svc = new BackupService(p, NullLogger<BackupService>.Instance);

        var notOurs = Path.Combine(_root, "x.zip");
        using (var z = System.IO.Compression.ZipFile.Open(notOurs, System.IO.Compression.ZipArchiveMode.Create))
            z.CreateEntry("qualquer.txt");
        await using (var s = File.OpenRead(notOurs))
            await Assert.ThrowsAsync<InvalidDataException>(() => svc.PrepareRestoreAsync(s));

        var slip = Path.Combine(_root, "slip.zip");
        using (var z = System.IO.Compression.ZipFile.Open(slip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("manifest.json").Open()))
                w.Write("""{"App":"I-LOUVORES","AppVersion":"0.4.0","CreatedUtc":"2026-01-01T00:00:00Z","Songs":0,"Notices":0,"BibleVersions":[],"GalleryFiles":0,"IncludesGallery":false}""");
            using (var w = new StreamWriter(z.CreateEntry("../../fora.txt").Open())) w.Write("x");
        }
        await using (var s = File.OpenRead(slip))
            await Assert.ThrowsAsync<InvalidDataException>(() => svc.PrepareRestoreAsync(s));
        Assert.False(File.Exists(Path.Combine(_root, "C", "fora.txt")));
        Assert.False(svc.HasPendingRestore);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}

public class BibleEditTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public BibleEditTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options);
        DbInitializer.Initialize(db);
    }

    [Fact]
    public async Task Search_fix_and_live_reading_updates()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var projection = new ProjectionService(NullLogger<ProjectionService>.Instance);
        var bible = new BibleService(new Factory(_options), new SettingsStore(new AppPaths(root, root)), NullLogger<BibleService>.Instance, projection);

        var parsed = new ParsedBible([new ParsedVerse(43, 3, 16, "Porque Deus amou o mudno de tal maneira"), new ParsedVerse(43, 3, 17, "Porque Deus enviou")], [], "t", null, null);
        var r = await bible.ImportAsync(parsed, "ARC", "Almeida", null, replace: false);

        var found = Assert.Single(await bible.SearchTextAsync(r.VersionId, "MUDNO"));   // sem distinguir maiúsculas
        Assert.Equal(16, found.Verse);

        // Capítulo em leitura no telão
        var version = (await bible.GetVersionAsync(r.VersionId))!;
        var book = (await bible.GetBooksAsync()).First(b => b.Id == 43);
        var item = QueueItemFactory.FromVerses(version, book, 3, await bible.GetChapterAsync(r.VersionId, 43, 3), reading: true);
        projection.ProjectNow(item);
        projection.StartProjection();

        var changed = await bible.UpdateVersesAsync(new Dictionary<int, string> { [found.Id] = "Porque Deus amou  o mundo de tal maneira " });
        Assert.Equal(1, changed);
        Assert.Equal("Porque Deus amou o mundo de tal maneira", projection.State.Live.Slide?.Text); // espaços normalizados
        Assert.Equal(item.Id, projection.State.CurrentItemId);
        Assert.Empty(await bible.SearchTextAsync(r.VersionId, "mudno"));

        await Assert.ThrowsAsync<ArgumentException>(() => bible.UpdateVersesAsync(new Dictionary<int, string> { [found.Id] = "   " }));
        Directory.Delete(root, recursive: true);
    }

    public void Dispose() => _conn.Dispose();

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
