using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Models;
using Projecao.Models.Projection;
using Projecao.Services;
using Xunit;

public class ProjectionServiceTests
{
    private static ProjectionService NewService() => new(NullLogger<ProjectionService>.Instance);

    private static QueueItem Item(string title, int slides, BackgroundMedia? bg = null, BackgroundMedia? first = null) => new()
    {
        Kind = ProjectionContentKind.Song,
        Title = title,
        Slides = Enumerable.Range(1, slides).Select(i => new ProjectionSlide($"{title}-{i}")).ToList(),
        Background = bg,
        FirstSlideBackground = first
    };

    [Fact]
    public void Next_and_previous_cross_item_boundaries()
    {
        var p = NewService();
        var a = Item("A", 2); var b = Item("B", 3);
        p.Enqueue(a); p.Enqueue(b);
        p.StartProjection();
        Assert.Equal("A-1", p.State.Live.Slide?.Text);
        p.Next(); p.Next();
        Assert.Equal("B-1", p.State.Live.Slide?.Text);
        p.Previous();
        Assert.Equal("A-2", p.State.Live.Slide?.Text);
        p.Next(); p.Next(); p.Next(); p.Next(); // fim da fila: fica no último
        Assert.Equal("B-3", p.State.Live.Slide?.Text);
        Assert.False(p.State.CanGoNext);
    }

    [Fact]
    public void Freeze_keeps_live_frame_but_blank_still_works()
    {
        var p = NewService();
        p.Enqueue(Item("A", 3)); p.StartProjection();
        p.ToggleFreeze();
        p.Next(); p.Next();
        Assert.Equal("A-1", p.State.Live.Slide?.Text);
        Assert.Equal(2, p.State.CurrentSlideIndex);
        p.ToggleBlank();
        Assert.True(p.State.Live.IsBlank);
        p.ToggleBlank(); p.ToggleFreeze();
        Assert.Equal("A-3", p.State.Live.Slide?.Text);
    }

    [Fact]
    public void Nothing_is_shown_before_projecting()
    {
        var p = NewService();
        p.Enqueue(Item("A", 1));
        Assert.Null(p.State.Live.Slide);
        p.StartProjection();
        Assert.NotNull(p.State.Live.Slide);
    }

    [Fact]
    public void Start_projection_uses_staged_item_when_queue_empty()
    {
        var p = NewService();
        var a = Item("A", 1);
        p.Stage(a);
        p.StartProjection();
        Assert.Equal(a.Id, p.State.CurrentItemId);
        Assert.Equal(a, p.State.CurrentItem);
        // Projetado diretamente, sem ＋ Adicionar: não entra na fila visível.
        Assert.Empty(p.State.Queue);
    }

    [Fact]
    public void Removing_current_selects_following_item()
    {
        var p = NewService();
        var a = Item("A", 1); var b = Item("B", 1); var c = Item("C", 1);
        p.Enqueue(a); p.Enqueue(b); p.Enqueue(c);
        p.Select(b.Id);
        p.Remove(b.Id);
        Assert.Equal(c.Id, p.State.CurrentItemId);
        p.Remove(c.Id);
        Assert.Equal(a.Id, p.State.CurrentItemId);
        p.Remove(a.Id);
        Assert.Null(p.State.CurrentItemId);
    }

    [Fact]
    public void Enqueue_same_item_twice_does_not_duplicate()
    {
        var p = NewService();
        var a = Item("A", 1);
        p.Enqueue(a); p.Enqueue(a);
        Assert.Single(p.State.Queue);
    }

    [Fact]
    public void Content_version_bumps_on_slide_change_and_refresh_only()
    {
        var p = NewService();
        p.Enqueue(Item("A", 2)); p.StartProjection();
        var v = p.State.Live.ContentVersion;
        p.ShowClock();
        Assert.Equal(v, p.State.Live.ContentVersion);
        p.Next();
        Assert.Equal(v + 1, p.State.Live.ContentVersion);
        p.Refresh();
        Assert.Equal(v + 2, p.State.Live.ContentVersion);
    }

    [Fact]
    public void First_slide_background_differs_from_rest()
    {
        var p = NewService();
        var first = new BackgroundMedia("https://g/primeiro.jpg", false);
        var rest = new BackgroundMedia("https://g/resto.jpg", false);
        p.SetGlobalFirstSlideBackground(first);
        p.SetGlobalBackground(rest);
        p.Enqueue(Item("A", 2)); p.StartProjection();
        Assert.Equal(first, p.State.Live.Background);
        p.Next();
        Assert.Equal(rest, p.State.Live.Background);

        // Item com fundo próprio (sem fundo de 1.º slide) ignora o global de 1.º slide.
        var own = new BackgroundMedia("https://g/proprio.mp4", true);
        var b = Item("B", 2, bg: own);
        p.ProjectNow(b);
        Assert.Equal(own, p.State.Live.Background);
    }

    [Fact]
    public void Safe_area_is_clamped_and_forces_refit()
    {
        var p = NewService();
        p.Enqueue(Item("A", 1)); p.StartProjection();
        var v = p.State.Live.ContentVersion;
        p.SetSafeArea(50);
        Assert.Equal(10, p.State.SafeAreaPercent);
        Assert.Equal(v + 1, p.State.Live.ContentVersion);
    }

    [Fact]
    public void Changing_tab_clears_queue_but_keeps_screen()
    {
        var p = NewService();
        p.Enqueue(Item("Louvor", 3)); p.Enqueue(Item("Outro", 1));
        p.StartProjection(); p.Next();
        p.Stage(Item("Escolhido", 1));

        p.ClearQueueKeepScreen();
        Assert.Empty(p.State.Queue);
        Assert.Null(p.State.Staged);
        Assert.Null(p.State.CurrentItem);
        Assert.Equal("Louvor-2", p.State.Live.Slide?.Text);   // telão não apagou

        p.ProjectNow(Item("Biblia", 2));                        // algo novo substitui
        Assert.Equal("Biblia-1", p.State.Live.Slide?.Text);
    }

    [Fact]
    public void Faulty_subscriber_does_not_block_others()
    {
        var p = NewService();
        var called = false;
        p.OnStateChanged += () => throw new InvalidOperationException();
        p.OnStateChanged += () => called = true;
        p.ToggleBlank();
        Assert.True(called);
    }

    [Fact]
    public void Concurrent_mutations_are_consistent()
    {
        var p = NewService();
        Parallel.For(0, 500, i => p.Enqueue(Item($"I{i}", 1)));
        Assert.Equal(500, p.State.Queue.Count);
        Assert.Equal(500, p.State.Queue.Select(q => q.Id).Distinct().Count());
    }
}

public class DataTests
{
    [Fact]
    public void Settings_are_persisted_and_survive_corruption()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var paths = new AppPaths(Path.Combine(root, "Documentos"), Path.Combine(root, "Dados"));

        var store = new SettingsStore(paths)
        {
            PreferredMonitor = new MonitorPreference("LF24T35@0,0", "LF24T35"),
            SafeAreaPercent = 5
        };

        var reloaded = new SettingsStore(paths);
        Assert.Equal("LF24T35", reloaded.PreferredMonitor?.DeviceName);
        Assert.Equal(5, reloaded.SafeAreaPercent);

        File.WriteAllText(Path.Combine(paths.DataFolder, "config.json"), "{ corrompido");
        Assert.Null(new SettingsStore(paths).PreferredMonitor);
    }

    [Fact]
    public void Database_initializes_and_accent_insensitive_search_works()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

        using (var db = new AppDbContext(options))
        {
            DbInitializer.Initialize(db);
            db.Songs.Add(new Song { Title = "Coração Grato", Number = 7, Collection = "Coletânea 2018", Lyrics = "Ó Senhor, Tu és\nmeu REFÚGIO\n\nsegunda estrofe" });
            db.SaveChanges();
        }

        using (var db = new AppDbContext(options))
        {
            Assert.Equal(66, db.BibleBooks.Count());
            Assert.Equal(150, db.BibleBooks.Single(b => b.Abbreviation == "Sl").ChapterCount);

            var term = $"%{TextNormalizer.EscapeLike(TextNormalizer.Normalize("CORACAO"))}%";
            Assert.Single(db.Songs.Where(s => EF.Functions.Like(s.NormalizedTitle, term, "\\")).ToList());

            var lyric = $"%{TextNormalizer.Normalize("és meu refugio")}%"; // atravessa quebra de linha
            Assert.Single(db.Songs.Where(s => EF.Functions.Like(s.NormalizedLyrics, lyric, "\\")).ToList());

            var song = db.Songs.Single(s => s.Number == 7);
            Assert.Equal(2, song.GetStanzas().Count);
            Assert.NotEqual(default, song.CreatedAt);

            var item = QueueItemFactory.FromSong(song);
            Assert.Equal("7 - Coração Grato", item.Title);
        }
    }

    [Fact]
    public void Like_wildcards_are_escaped()
    {
        Assert.Equal(@"100\% \_x", TextNormalizer.EscapeLike("100% _x"));
    }

    [Fact]
    public void Media_resolve_blocks_path_traversal()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var media = new MediaLibraryService(new AppPaths(Path.Combine(root, "Documentos"), Path.Combine(root, "Dados")));
        File.WriteAllText(Path.Combine(media.GalleryFolder, "fundo 1.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "segredo.jpg"), "x"); // fora da Galeria

        var ok = media.Resolve("fundo 1.jpg");
        Assert.StartsWith("/galeria/fundo%201.jpg?v=", ok?.Url);
        Assert.Equal("fundo 1.jpg", ok?.RelativePath);
        Assert.Null(media.Resolve("../../segredo.jpg"));
        Assert.Null(media.Resolve("naoexiste.png"));
        Assert.Single(media.List());
    }

    [Fact]
    public void Css_values_are_sanitized()
    {
        Assert.Equal("#FFAA00", CssSafe.Color("#FFAA00"));
        Assert.Null(CssSafe.Color("red; background:url(x)"));
        Assert.Null(CssSafe.FontFamily("Arial'; }"));
        Assert.Equal("'Segoe UI'", CssSafe.FontFamily("Segoe UI"));
    }

    [Fact]
    public void Monitor_default_prefers_saved_then_secondary()
    {
        var panel = new MonitorInfo("LC24RG50@1920,0", "LC24RG50", "Monitor 1 · LC24RG50", 1920, 0, 1920, 1080, 144, true);
        var tv = new MonitorInfo("LF24T35@0,0", "LF24T35", "Monitor 2 · LF24T35", 0, 0, 1920, 1080, 75, false);
        Assert.Equal(tv, MonitorSelection.PickDefault([panel, tv], null));
        Assert.Equal(panel, MonitorSelection.PickDefault([panel, tv], new MonitorPreference(panel.Id, "")));
        // Ecrãs reorganizados (a posição mudou) mas o modelo coincide
        Assert.Equal(tv, MonitorSelection.PickDefault([panel, tv], new MonitorPreference("LF24T35@3840,0", "LF24T35")));
        Assert.Equal("Monitor 2 · LF24T35 — 1920x1080 @ 75Hz", tv.Description);
        Assert.EndsWith("(painel)", panel.Description);
    }
}
