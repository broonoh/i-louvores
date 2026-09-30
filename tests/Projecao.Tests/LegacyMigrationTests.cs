using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Services;
using Projecao.Services.Import;
using Projecao.Services.LegacyImport;
using Xunit;

public class LegacyMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    private static readonly Encoding Win1252 = CodePagesEncoding();

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    /// <summary>Cria uma instalação antiga falsa dentro de um "Bottles".</summary>
    private string FakeInstall()
    {
        var docs = Path.Combine(_root, "home/.var/app/com.usebottles.bottles/data/bottles/bottles/Legado/drive_c/users/ana/Documents/Glorifica");
        var raw = Path.Combine(docs, "Louvores/raw");
        Directory.CreateDirectory(raw);
        File.WriteAllBytes(Path.Combine(raw, "Águas que curam.txt"), Win1252.GetBytes("Águas que curam\r\n\r\nCoro\r\nÉ o Senhor\r\n\r\nV2"));
        File.WriteAllBytes(Path.Combine(raw, "BENDIREI.txt"), Win1252.GetBytes("Bendirei ao Senhor"));
        File.WriteAllBytes(Path.Combine(raw, "LouvoresRaw.ini"), Win1252.GetBytes(
            "[Águas que curam]\r\nNome=Águas que curam\r\nNumeroAntigo=N/D\r\nNumeroNovo=12\r\nTom=Ab\r\nRitimo=N/D\r\nAutor=N/D\r\n"));

        Directory.CreateDirectory(Path.Combine(docs, "Galeria"));
        File.WriteAllBytes(Path.Combine(docs, "Galeria/Louvor(Primeiro Slide) novo.jpg"), [1, 2]);
        File.WriteAllBytes(Path.Combine(docs, "Galeria/Louvor(Resto) novo.jpg"), [3]);
        File.WriteAllBytes(Path.Combine(docs, "Galeria/FUNDO_BIBLIA.jpg"), [4]);
        foreach (var res in new[] { "1024x768", "1920x1080" })
        {
            Directory.CreateDirectory(Path.Combine(docs, $"Etc/Styles/1/{res}"));
            File.WriteAllBytes(Path.Combine(docs, $"Etc/Styles/1/{res}/style1_1.jpg"), res == "1920x1080" ? [9, 9, 9] : [1]);
        }
        File.WriteAllBytes(Path.Combine(docs, "Config.ini"), Win1252.GetBytes(
            "[Louvores]\r\nImgFundo_LouvorPath1=C:\\users\\ana\\Documents\\Glorifica\\Galeria\\Louvor(Primeiro Slide) novo.jpg\r\n" +
            "ImgFundo_LouvorPath2=C:\\users\\ana\\Documents\\Glorifica\\Galeria\\Louvor(Resto) novo.jpg\r\n" +
            "[Bíblia]\r\nImgFundo_BibliaPath1=C:\\users\\ana\\Documents\\Glorifica\\Galeria\\FUNDO_BIBLIA.jpg\r\n"));
        return docs;
    }

    [Fact]
    public async Task Detects_and_migrates_songs_images_and_backgrounds()
    {
        var docs = FakeInstall();
        _conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using (var db = new AppDbContext(options)) DbInitializer.Initialize(db);

        var paths = new AppPaths(Path.Combine(_root, "Documentos/I-LOUVORES"), Path.Combine(_root, "dados"));
        using var media = new MediaLibraryService(paths);
        var settings = new SettingsStore(paths);
        var projection = new ProjectionService(NullLogger<ProjectionService>.Instance);
        var gallery = new GalleryService(media, projection, settings, NullLogger<GalleryService>.Instance);
        var service = new LegacyMigrationService(new SongImporter(new Factory(options)), media, gallery, NullLogger<LegacyMigrationService>.Instance);

        var found = Assert.Single(service.Detect(Path.Combine(_root, "home")));
        Assert.Equal((2, 4, true), (found.SongCount, found.ImageCount, found.HasConfig)); // 3 da galeria + 1 estilo (maior resolução)
        Assert.StartsWith("Bottles", found.Label);

        var songs = service.ReadSongs(docs).Songs;
        var aguas = songs.Single(s => s.Title == "Águas que curam");             // acentos Windows-1252 corretos
        Assert.Equal((12, "Ab"), (aguas.Number!.Value, aguas.MusicalKey!));
        Assert.Null(aguas.AltNumber);                                               // "N/D"
        Assert.Contains("Coro\nÉ o Senhor", aguas.Lyrics);                          // sintaxe mantida

        var result = await service.MigrateAsync(docs, "Avulsos", updateExisting: false, importImages: true, applyBackgrounds: true);
        Assert.Equal(2, result.Songs.Created);
        Assert.Equal(4, result.ImagesCopied);
        Assert.Equal("Louvor(Primeiro Slide) novo.jpg", settings.GalleryFirstSlideBackground);
        Assert.Equal("FUNDO_BIBLIA.jpg", settings.GalleryBibleBackground);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(media.GalleryFolder, LegacyMigrationService.TemplatesFolder, "Estilo 1 - style1_1.jpg")));

        // Repetir não duplica nada
        var again = await service.MigrateAsync(docs, "Avulsos", false, true, true);
        Assert.Equal((0, 2, 0), (again.Songs.Created, again.Songs.Skipped, again.ImagesCopied));
    }

    [Theory]
    [InlineData(@"C:\users\ana\Documents\Glorifica\Galeria\A b.jpg", "Galeria/A b.jpg")]
    [InlineData(@"C:\outro\sitio.jpg", null)]
    public void Maps_windows_paths(string win, string? expected) =>
        Assert.Equal(expected?.Replace('/', Path.DirectorySeparatorChar), LegacyMigrationService.MapWindowsPath(win));

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
