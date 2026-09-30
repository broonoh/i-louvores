using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Models;
using Projecao.Services;
using Projecao.Services.Import;
using Xunit;

public class SongImportParserTests
{
    private static ParseResult Txt(string text) => SongImportParser.Parse("louvores.txt", Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Txt_with_header_and_stanzas()
    {
        var r = Txt("Título: Grande é o Senhor\r\nNúmero: 12\r\nTom: G\r\nColetânea: Avulsos\r\n\r\nLinha 1\r\nLinha 2\r\n\r\n\r\n\r\nLinha 3\r\n");
        var s = Assert.Single(r.Songs);
        Assert.Equal("Grande é o Senhor", s.Title);
        Assert.Equal(12, s.Number);
        Assert.Equal("G", s.MusicalKey);
        Assert.Equal("Avulsos", s.Collection);
        Assert.Equal("Linha 1\nLinha 2\n\nLinha 3", s.Lyrics); // CRLF normalizado, brancos extra colapsados
        Assert.Equal(2, s.StanzaCount);
    }

    [Fact]
    public void Txt_numbered_first_line_and_multiple_songs()
    {
        var r = Txt("12 - Primeiro\n\nA\n\nB\n---\n13. Segundo\nC\n===\nTerceiro sem número\n\nD");
        Assert.Equal(3, r.Songs.Count);
        Assert.Equal((12, "Primeiro"), (r.Songs[0].Number!.Value, r.Songs[0].Title));
        Assert.Equal((13, "Segundo", "C"), (r.Songs[1].Number!.Value, r.Songs[1].Title, r.Songs[1].Lyrics));
        Assert.Null(r.Songs[2].Number);
        Assert.Equal("Terceiro sem número", r.Songs[2].Title);
    }

    [Fact]
    public void Txt_lyric_lines_with_colon_are_not_headers()
    {
        var s = Assert.Single(Txt("Aleluia\n\nSenhor: Tu és santo\nRei: dos reis").Songs);
        Assert.Equal("Aleluia", s.Title);
        Assert.Contains("Senhor: Tu és santo", s.Lyrics);
    }

    [Fact]
    public void Windows1252_file_keeps_accents()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Coração em Adoração\n\nÓ Jesus, és tão bom");
        var s = Assert.Single(SongImportParser.Parse("antigo.txt", bytes).Songs);
        Assert.Equal("Coração em Adoração", s.Title);
        Assert.StartsWith("Ó Jesus, és", s.Lyrics);
    }

    [Fact]
    public void Csv_semicolon_quoted_multiline_and_aliases()
    {
        var csv = "Nº;Título;Letra;Coletânea;Tom\n" +
                  "5;\"Louvor \"\"A\"\"\";\"L1\nL2\n\nL3\";Coletânea 2018;D\n" +
                  "6;Louvor B;Uma\\nDuas;;\n" +
                  ";;;;\n";
        var r = SongImportParser.Parse("lista.csv", Encoding.UTF8.GetBytes(csv));
        Assert.Empty(r.Issues);
        Assert.Equal(2, r.Songs.Count);
        Assert.Equal("Louvor \"A\"", r.Songs[0].Title);
        Assert.Equal("L1\nL2\n\nL3", r.Songs[0].Lyrics);
        Assert.Equal("Coletânea 2018", r.Songs[0].Collection);
        Assert.Equal("Uma\nDuas", r.Songs[1].Lyrics); // "\n" literal
        Assert.Null(r.Songs[1].Collection);
    }

    [Fact]
    public void Csv_without_required_columns_reports_issue()
    {
        var r = SongImportParser.Parse("x.csv", Encoding.UTF8.GetBytes("nome,autor\nA,B"));
        Assert.Empty(r.Songs);
        Assert.Contains("titulo", Assert.Single(r.Issues).Message);
    }

    [Fact]
    public void OpenLyrics_follows_verse_order_and_strips_chords()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <song xmlns="http://openlyrics.info/namespace/2009/song" version="0.9">
              <properties>
                <titles><title>Santo</title></titles>
                <authors><author>Autor A</author><author>Autor B</author></authors>
                <songbooks><songbook name="Hinário" entry="42"/></songbooks>
                <verseOrder>v1 c v2 c</verseOrder>
              </properties>
              <lyrics>
                <verse name="v1"><lines><chord name="G"/>Verso um<br/>linha dois</lines></verse>
                <verse name="c"><lines>Refrão</lines></verse>
                <verse name="v2"><lines>Verso dois</lines></verse>
              </lyrics>
            </song>
            """;
        var s = Assert.Single(SongImportParser.Parse("santo.xml", Encoding.UTF8.GetBytes(xml)).Songs);
        Assert.Equal(("Santo", 42, "Hinário", "Autor A, Autor B"), (s.Title, s.Number!.Value, s.Collection!, s.Author!));
        Assert.Equal("Verso um\nlinha dois\n\nRefrão\n\nVerso dois\n\nRefrão", s.Lyrics);
    }

    [Fact]
    public void Xml_with_dtd_is_rejected()
    {
        const string evil = "<?xml version=\"1.0\"?><!DOCTYPE song [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><song>&x;</song>";
        var r = SongImportParser.Parse("evil.xml", Encoding.UTF8.GetBytes(evil));
        Assert.Empty(r.Songs);
        Assert.Single(r.Issues);
    }

    [Fact]
    public void Unsupported_extension_reports_issue()
    {
        var r = SongImportParser.Parse("PT_Avulsos.xbY", [1, 2, 3]);
        Assert.Empty(r.Songs);
        Assert.Contains("não suportado", Assert.Single(r.Issues).Message);
    }
}

public class SongImporterTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public SongImporterTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
        db.Songs.Add(new Song { Title = "Existente", Number = 1, Collection = "Avulsos", Lyrics = "velha", Author = "Mantido" });
        db.Songs.Add(new Song { Title = "Sem Número", Collection = "Avulsos", Lyrics = "x" });
        db.SaveChanges();
    }

    private SongImporter Importer => new(new Factory(_options));

    [Fact]
    public async Task Plan_detects_new_existing_and_repeated()
    {
        ImportedSong[] songs =
        [
            new("Existente (nova letra)", "nova", "a.txt", Number: 1),          // mesmo nº na coletânea padrão
            new("SEM NÚMERO", "y", "a.txt"),                                    // mesmo título normalizado
            new("Novo", "z", "a.txt", Number: 1, Collection: "Coletânea 2018"), // nº 1 mas outra coletânea
            new("Novo repetido", "z", "b.txt", Number: 1, Collection: "Coletânea 2018"),
            new("Duas Versões", "letra A", "c.txt"),
            new("DUAS VERSÕES", "letra B diferente", "c .txt"),
            new("Duas versões", "letra A", "d.txt"),
        ];

        var skip = await Importer.PlanAsync(songs, "Avulsos", updateExisting: false);
        Assert.Equal([ImportAction.Skip, ImportAction.Skip, ImportAction.Create, ImportAction.Skip, ImportAction.Create, ImportAction.Create, ImportAction.Skip],
            skip.Select(p => p.Action));
        Assert.Equal("Repetido nos ficheiros (letra igual)", skip[3].Note); // mesmo nº e mesma letra
        Assert.Equal("DUAS VERSÕES (2)", skip[5].Song.Title);               // mesmo título, letra diferente → nova versão
        Assert.Equal("Repetido nos ficheiros (letra igual)", skip[6].Note);

        var upd = await Importer.PlanAsync(songs, "Avulsos", updateExisting: true);
        Assert.Equal([ImportAction.Update, ImportAction.Update, ImportAction.Create, ImportAction.Skip, ImportAction.Create, ImportAction.Create, ImportAction.Skip],
            upd.Select(p => p.Action));
    }

    [Fact]
    public async Task Apply_creates_and_updates_in_one_transaction()
    {
        var plan = await Importer.PlanAsync(
            [new("Existente", "nova letra\n\nestrofe 2", "a.txt", Number: 1), new("Café com Louvor", "a\n\nb", "a.txt", Number: 2)],
            "Avulsos", updateExisting: true);

        var result = await Importer.ApplyAsync(plan);
        Assert.Equal(new ImportResult(1, 1, 0), result);

        await using var db = new AppDbContext(_options);
        var updated = db.Songs.Single(s => s.Number == 1);
        Assert.Equal("nova letra\n\nestrofe 2", updated.Lyrics);
        Assert.Equal("Mantido", updated.Author); // metadado ausente no ficheiro não apaga o existente
        var created = db.Songs.Single(s => s.Number == 2);
        Assert.Equal("cafe com louvor", created.NormalizedTitle); // pesquisa sem acentos funciona já
        Assert.Equal("Avulsos", created.Collection);
    }

    public void Dispose() => _conn.Dispose();

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}

public class GalleryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly AppPaths _paths;
    private readonly MediaLibraryService _media;

    public GalleryTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "Documentos"), Path.Combine(_root, "Dados"));
        _media = new MediaLibraryService(_paths);
    }

    [Fact]
    public async Task Import_sanitizes_name_avoids_overwrite_and_rejects_unsupported()
    {
        var a = await _media.ImportAsync("../../fundo.jpg", new MemoryStream([1, 2, 3]));
        var b = await _media.ImportAsync("fundo.jpg", new MemoryStream([4]));
        Assert.Equal("fundo.jpg", a.RelativePath);          // "../.." removido: fica dentro da Galeria
        Assert.Equal("fundo (2).jpg", b.RelativePath);      // não substitui o existente
        Assert.StartsWith("/galeria/fundo%20%282%29.jpg?v=", b.Url);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _media.ImportAsync("virus.exe", new MemoryStream([0])));
        Assert.Equal(2, _media.List().Count);               // sem temporários visíveis
        Assert.Empty(Directory.GetFiles(_media.GalleryFolder, ".importar-*"));
    }

    [Fact]
    public async Task Gallery_selection_persists_and_follows_file_lifecycle()
    {
        await _media.ImportAsync("primeiro.jpg", new MemoryStream([1]));
        await _media.ImportAsync("resto.webm", new MemoryStream([1]));
        var projection = new ProjectionService(NullLogger<ProjectionService>.Instance);
        var settings = new SettingsStore(_paths);
        var gallery = new GalleryService(_media, projection, settings, NullLogger<GalleryService>.Instance);

        gallery.Select(BackgroundSlot.FirstSlide, "primeiro.jpg");
        gallery.Select(BackgroundSlot.Slides, "resto.webm");
        Assert.Equal("primeiro.jpg", projection.State.GlobalFirstSlideBackground?.RelativePath);
        Assert.True(projection.State.GlobalBackground?.IsVideo);

        // Nova sessão: escolhas restauradas a partir do config.json
        var projection2 = new ProjectionService(NullLogger<ProjectionService>.Instance);
        new GalleryService(_media, projection2, new SettingsStore(_paths), NullLogger<GalleryService>.Instance).Initialize();
        Assert.Equal("resto.webm", projection2.State.GlobalBackground?.RelativePath);

        // Ficheiro apagado: some do telão mas a escolha fica guardada (pen USB pode voltar)
        File.Delete(Path.Combine(_media.GalleryFolder, "resto.webm"));
        gallery.Reapply();
        Assert.Null(projection.State.GlobalBackground);
        Assert.Equal("resto.webm", settings.GalleryBackground);

        Assert.Throws<FileNotFoundException>(() => gallery.Select(BackgroundSlot.Slides, "naoexiste.png"));
    }

    [Fact]
    public async Task Folder_watcher_raises_changed()
    {
        var tcs = new TaskCompletionSource();
        _media.Changed += () => tcs.TrySetResult();
        File.WriteAllBytes(Path.Combine(_media.GalleryFolder, "novo.png"), [1]);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, done);
    }

    public void Dispose()
    {
        _media.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
