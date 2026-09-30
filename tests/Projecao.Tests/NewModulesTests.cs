using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Models;
using Projecao.Models.Projection;
using Projecao.Services;
using Projecao.Services.Notices;
using Projecao.Services.Songs;
using Xunit;

public class NewModulesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public NewModulesTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options);
        DbInitializer.Initialize(db);
    }

    [Fact]
    public void Legacy_glorifica_folders_are_migrated_once()
    {
        var oldDocs = Path.Combine(_root, "Documentos/Glorifica");
        var oldData = Path.Combine(_root, "share/Glorifica");
        Directory.CreateDirectory(Path.Combine(oldDocs, "Galeria"));
        File.WriteAllText(Path.Combine(oldDocs, "Galeria/fundo.jpg"), "x");
        Directory.CreateDirectory(oldData);
        File.WriteAllText(Path.Combine(oldData, "glorifica.db"), "db");
        File.WriteAllText(Path.Combine(oldData, "glorifica.db-wal"), "wal");
        File.WriteAllText(Path.Combine(oldData, "config.json"), "{}");

        var paths = new AppPaths(Path.Combine(_root, "Documentos/I-LOUVORES"), Path.Combine(_root, "share/i-louvores"), oldDocs, oldData);

        Assert.True(File.Exists(Path.Combine(paths.GalleryFolder, "fundo.jpg")));
        Assert.Equal("db", File.ReadAllText(paths.DatabasePath));
        Assert.Equal("wal", File.ReadAllText(paths.DatabasePath + "-wal"));
        Assert.True(File.Exists(Path.Combine(paths.DataFolder, "config.json")));
        Assert.False(Directory.Exists(oldDocs));
    }

    [Fact]
    public void Real_glorifica_folder_is_never_moved()
    {
        var realGlorifica = Path.Combine(_root, "Documentos/Glorifica");
        Directory.CreateDirectory(Path.Combine(realGlorifica, "Galeria"));
        File.WriteAllText(Path.Combine(realGlorifica, "Config.ini"), "[Main]");

        _ = new AppPaths(Path.Combine(_root, "Documentos/I-LOUVORES"), Path.Combine(_root, "d"), realGlorifica, null);
        Assert.True(File.Exists(Path.Combine(realGlorifica, "Config.ini")));
    }

    [Fact]
    public void Notice_html_is_sanitized()
    {
        var html = "<div style=\"text-align:center;position:fixed\"><span style=\"color:#ff0;font-size:1.4em;background:url(x)\">Culto</span>" +
                   "<script>alert(1)</script><img src=x onerror=alert(1)><a href=\"javascript:x\">link</a></div>";
        var clean = NoticeService.Sanitize(html);
        Assert.Contains("Culto", clean);
        Assert.Contains("text-align: center", clean);
        Assert.Contains("font-size: 1.4em", clean);
        Assert.DoesNotContain("script", clean);
        Assert.DoesNotContain("img", clean);
        Assert.DoesNotContain("javascript", clean);
        Assert.DoesNotContain("position", clean);
        Assert.DoesNotContain("url(", clean);
    }

    [Fact]
    public async Task Notices_save_open_and_templates()
    {
        var svc = new NoticeService(new Factory(_options));
        var n = await svc.SaveAsync("Santa Ceia", "<b>Domingo</b><script>x</script>", "#000000", isTemplate: false);
        await svc.SaveAsync("Modelo base", "<i>Texto</i>", null, isTemplate: true);
        await svc.SaveAsync("Santa Ceia", "<b>Sábado</b>", null, isTemplate: false); // mesmo nome = atualiza

        var saved = Assert.Single(await svc.ListAsync(templates: false));
        Assert.Equal((n.Id, "<b>Sábado</b>"), (saved.Id, saved.Content));
        Assert.Single(await svc.ListAsync(templates: true));

        var item = QueueItemFactory.FromNotice(saved);
        Assert.True(item.Slides[0].Html);
    }

    [Fact]
    public async Task Editing_a_song_updates_it_live_in_the_queue()
    {
        var projection = new ProjectionService(NullLogger<ProjectionService>.Instance);
        using var media = new MediaLibraryService(new AppPaths(Path.Combine(_root, "docs"), Path.Combine(_root, "data")));
        var songs = new SongService(new Factory(_options), projection, media);

        var song = await songs.SaveAsync(new Song { Title = "Teste", Lyrics = "Letra com erro" });
        var item = QueueItemFactory.FromSong(song);
        projection.ProjectNow(item);
        projection.StartProjection();

        song.Lyrics = "Letra corrigida\n\nSegunda estrofe";
        await songs.SaveAsync(song);

        Assert.Equal(item.Id, projection.State.CurrentItemId);                // mesmo item na fila
        Assert.Equal("Letra corrigida", projection.State.Live.Slide?.Text);   // projetor já mostra a correção
        Assert.Equal(2, projection.State.CurrentItem!.Slides.Count);

        await Assert.ThrowsAsync<ArgumentException>(() => songs.SaveAsync(new Song { Title = "  " }));
        await songs.DeleteAsync(song.Id);
        Assert.DoesNotContain(await songs.SearchAsync("Teste"), s => s.Id == song.Id);
    }

    [Fact]
    public void Timer_modes_fullscreen_and_overlay()
    {
        var p = new ProjectionService(NullLogger<ProjectionService>.Instance);
        p.StartCountdown(TimeSpan.Zero, fullScreen: true);
        Assert.Equal((OverlayMode.Stopwatch, true), (p.State.Live.Overlay, p.State.Live.OverlayFullScreen));

        p.StartCountdown(TimeSpan.FromMinutes(5));
        Assert.Equal(OverlayMode.Countdown, p.State.Live.Overlay);
        Assert.False(p.State.Live.OverlayFullScreen);
        var target = p.State.CountdownTarget;
        p.SetOverlayFullScreen(true);                   // alternar não reinicia a contagem
        Assert.Equal(target, p.State.CountdownTarget);

        p.ShowClock();
        p.ClearOverlay();
        Assert.Equal(OverlayMode.None, p.State.Live.Overlay);
    }

    [Fact]
    public void Bible_item_uses_its_own_background_even_on_first_slide()
    {
        var p = new ProjectionService(NullLogger<ProjectionService>.Instance);
        var songFirst = new BackgroundMedia("/galeria/louvor1.jpg", false);
        var bibleBg = new BackgroundMedia("/galeria/biblia.jpg", false);
        p.SetGlobalFirstSlideBackground(songFirst);
        var blivre = new BibleVersion { Abbreviation = "BLIVRE", Name = "x", Copyright = "… Licença Creative Commons Atribuição 3.0 Brasil" };
        var joao = new BibleBook { Id = 43, Name = "João", Abbreviation = "Jo" };
        var item = QueueItemFactory.FromVerses(blivre, joao, 3, [new BibleVerse { Verse = 16, Text = "Porque Deus amou" }], bibleBg);
        p.ProjectNow(item);
        p.StartProjection();
        Assert.Equal(bibleBg, p.State.Live.Background);
        Assert.Equal("João 3:16", p.State.Live.Slide?.Heading);        // sem a sigla na referência
        Assert.Equal("BLIVRE", p.State.Live.Slide?.Credit);             // crédito CC BY discreto

        var arc = new BibleVersion { Abbreviation = "ARC", Name = "Almeida", Copyright = "© SBB — uso licenciado" };
        Assert.Null(QueueItemFactory.FromVerses(arc, joao, 3, [new BibleVerse { Verse = 16, Text = "x" }]).Slides[0].Credit);
    }

    [Fact]
    public void Bible_reading_advances_verse_by_verse_through_the_chapter()
    {
        var p = new ProjectionService(NullLogger<ProjectionService>.Instance);
        var version = new BibleVersion { Abbreviation = "ARC", Name = "Almeida" };
        var book = new BibleBook { Id = 19, Name = "Salmos", Abbreviation = "Sl" };
        var chapter = Enumerable.Range(1, 6).Select(v => new BibleVerse { Verse = v, Text = $"v{v}" }).ToList();

        var reading = QueueItemFactory.FromVerses(version, book, 23, chapter, reading: true);
        p.ProjectNow(reading, slideIndex: 2); // começa no versículo 3
        p.StartProjection();
        Assert.Equal("Salmos 23:3", p.State.Live.Slide?.Heading);
        p.Next(); p.Next();
        Assert.Equal("Salmos 23:5", p.State.Live.Slide?.Heading);
        p.Previous();
        Assert.Equal("Salmos 23:4", p.State.Live.Slide?.Heading);
        Assert.Equal("Salmos 23 — leitura (ARC)", reading.Title);

        // Fluxo do painel: escolher o versículo na aba Bíblia e carregar no ▶ Projetar GRANDE do painel
        var panel = new ProjectionService(NullLogger<ProjectionService>.Instance);
        panel.Stage(reading, startSlide: 3);                 // versículo 4 escolhido
        Assert.True(panel.State.CanGoNext);                 // Próximo ativo mesmo com a fila vazia
        panel.StartProjection();
        Assert.Equal("Salmos 23:4", panel.State.Live.Slide?.Heading);
        Assert.True(panel.State.CanGoNext);
        panel.Next();
        Assert.Equal("Salmos 23:5", panel.State.Live.Slide?.Heading);

        // ＋ Adicionar do painel com fila vazia: fica atual já no versículo escolhido
        var add = new ProjectionService(NullLogger<ProjectionService>.Instance);
        add.Stage(reading, startSlide: 1);
        add.AddStaged();
        Assert.Equal((1, reading.Id), (add.State.CurrentSlideIndex, add.State.CurrentItemId!.Value));

        // Próximo com a fila vazia e algo escolhido: começa a projetar
        var fresh = new ProjectionService(NullLogger<ProjectionService>.Instance);
        fresh.Stage(reading, startSlide: 2);
        fresh.Next();
        Assert.Equal(2, fresh.State.CurrentSlideIndex);
    }

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
