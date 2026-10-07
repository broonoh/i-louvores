using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Services;
using Projecao.Services.Bible;
using Xunit;

public class BibleParserTests
{
    private static readonly string BundledZip = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../src/Projecao/Resources/Bible/bliv-tr_osis.zip"));

    private static ParsedBible Parse(string name, string text) => BibleImportParser.Parse(name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Real_Biblia_Livre_osis_parses_completely_and_cleanly()
    {
        var bible = BibleImportParser.Parse("bliv-tr_osis.zip", File.ReadAllBytes(BundledZip));

        Assert.Equal("OSIS", bible.Format);
        Assert.Equal(66, bible.BookCount);
        Assert.InRange(bible.Verses.Count, 31000, 31200);   // ~31 102 na versificação KJV
        Assert.Empty(bible.Issues);

        string V(int b, int c, int v) => bible.Verses.Single(x => x.BookId == b && x.Chapter == c && x.Verse == v).Text;
        Assert.Equal("No princípio criou Deus os céus e a terra.", V(1, 1, 1));
        Assert.StartsWith("Porque Deus amou ao mundo de tal maneira", V(43, 3, 16));
        Assert.Equal("O SENHOR é meu pastor, nada me faltará.", V(19, 23, 1));          // título "Salmo de Davi:" fora
        Assert.Equal("Ele me faz deitar em pastos verdes, e me leva a águas quietas.", V(19, 23, 2)); // transChange mantido
        Assert.DoesNotContain(bible.Verses, v => v.Text.Contains('<') || v.Text.Contains("  "));
        Assert.Equal(150, bible.Verses.Where(v => v.BookId == 19).Max(v => v.Chapter));
    }

    [Fact]
    public void Osis_container_style()
    {
        var b = Parse("x.xml", """<osis xmlns="http://www.bibletechnologies.net/2003/OSIS/namespace"><osisText><div type="book" osisID="John"><chapter osisID="John.3"><verse osisID="John.3.16">Porque Deus<note>nota</note> amou</verse></chapter></div></osisText></osis>""");
        Assert.Equal(new ParsedVerse(43, 3, 16, "Porque Deus amou"), Assert.Single(b.Verses));
    }

    [Fact]
    public void Zefania_with_notes_and_metadata()
    {
        var b = Parse("arc.xml", """
            <XMLBIBLE biblename="Teste"><INFORMATION><title>Almeida Teste</title><identifier>ALT</identifier></INFORMATION>
            <BIBLEBOOK bnumber="1" bname="Gênesis"><CHAPTER cnumber="1"><VERS vnumber="1">No princípio<NOTE>nota</NOTE> criou</VERS><VERS vnumber="2">E a terra</VERS></CHAPTER></BIBLEBOOK>
            <BIBLEBOOK bname="Apocalipse"><CHAPTER cnumber="22"><VERS vnumber="21">Amém.</VERS></CHAPTER></BIBLEBOOK>
            </XMLBIBLE>
            """);
        Assert.Equal(("Zefania", "ALT", "Almeida Teste"), (b.Format, b.SuggestedAbbreviation!, b.SuggestedName!));
        Assert.Equal("No princípio criou", b.Verses[0].Text);
        Assert.Equal(new ParsedVerse(66, 22, 21, "Amém."), b.Verses[2]);
    }

    [Fact]
    public void OpenSong_by_name()
    {
        var b = Parse("os.xml", """<bible><b n="Salmos"><c n="23"><v n="1">O Senhor é o meu pastor</v></c></b><b n="I João"><c n="4"><v n="8">Deus é amor</v></c></b></bible>""");
        Assert.Equal([new ParsedVerse(19, 23, 1, "O Senhor é o meu pastor"), new ParsedVerse(62, 4, 8, "Deus é amor")], b.Verses);
    }

    [Fact]
    public void Json_books_with_chapters_and_flat_list()
    {
        var nested = Parse("pt.json", """[{"abbrev":"gn","chapters":[["v1","v2"],["c2v1"]]},{"abbrev":"jo","chapters":[["João 1:1"]]},{"abbrev":"jó","chapters":[["Jó 1:1"]]}]""");
        Assert.Equal(5, nested.Verses.Count);
        Assert.Equal(new ParsedVerse(1, 2, 1, "c2v1"), nested.Verses[2]);
        Assert.Equal(43, nested.Verses[3].BookId); // "jo" = João
        Assert.Equal(18, nested.Verses[4].BookId); // "jó" = Jó

        var flat = Parse("v.json", """{"verses":[{"book":"Rm","chapter":8,"verse":28,"text":"E sabemos"}]}""");
        Assert.Equal(new ParsedVerse(45, 8, 28, "E sabemos"), Assert.Single(flat.Verses));
    }

    [Fact]
    public void Csv_tsv_and_vpl()
    {
        var csv = Parse("b.csv", "livro;capitulo;versiculo;texto\nGn;1;1;No princípio; criou\n1Co;13;4;O amor\nXyz;1;1;?");
        Assert.Equal(2, csv.Verses.Count);
        Assert.Equal("No princípio; criou", csv.Verses[0].Text); // separador dentro do texto
        Assert.Equal(46, csv.Verses[1].BookId);
        Assert.Single(csv.Issues);

        var vpl = Parse("b.txt", "GEN 1:1 No princípio\nPSA 23:1 O SENHOR\nlinha inválida\n");
        Assert.Equal([new ParsedVerse(1, 1, 1, "No princípio"), new ParsedVerse(19, 23, 1, "O SENHOR")], vpl.Verses);
    }

    [Fact]
    public void Duplicates_and_empty_verses_are_reported()
    {
        var b = Parse("d.txt", "Gn 1:1 A\nGn 1:1 B\nGn 1:2  \n");
        Assert.Equal("A", Assert.Single(b.Verses).Text);
        Assert.Contains(b.Issues, i => i.Contains("repetido"));
    }

    [Theory]
    [InlineData("Gênesis", 1)] [InlineData("genesis", 1)] [InlineData("GEN", 1)] [InlineData("Gen", 1)]
    [InlineData("1 Samuel", 9)] [InlineData("I Samuel", 9)] [InlineData("1Sm", 9)] [InlineData("II Reis", 12)]
    [InlineData("Salmo", 19)] [InlineData("Psalms", 19)] [InlineData("Cântico dos Cânticos", 22)] [InlineData("Jó", 18)]
    [InlineData("Jo", 43)] [InlineData("João", 43)] [InlineData("1 João", 62)] [InlineData("3Jo", 64)]
    [InlineData("Atos dos Apóstolos", 44)] [InlineData("Apocalipse", 66)] [InlineData("REV", 66)] [InlineData("66", 66)]
    public void Book_resolver(string name, int expected) => Assert.Equal(expected, BibleBookResolver.Resolve(name));

    [Theory]
    [InlineData("Tobias")] [InlineData("67")] [InlineData("")] [InlineData("xyz")]
    public void Book_resolver_rejects_unknown(string name) => Assert.Null(BibleBookResolver.Resolve(name));

    [Fact]
    public void Xml_with_dtd_is_rejected()
    {
        var b = Parse("evil.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><XMLBIBLE>&e;</XMLBIBLE>");
        Assert.Empty(b.Verses);
        Assert.Contains("inválido", Assert.Single(b.Issues));
    }
}

public class BibleServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly BibleService _service;

    public BibleServiceTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using (var db = new AppDbContext(_options)) DbInitializer.Initialize(db);
        var root = Directory.CreateTempSubdirectory().FullName;
        _service = new BibleService(new Factory(_options), new SettingsStore(new AppPaths(root, root)), NullLogger<BibleService>.Instance);
    }

    private static ParsedBible Sample(params (int b, int c, int v, string t)[] verses) =>
        new(verses.Select(x => new ParsedVerse(x.b, x.c, x.v, x.t)).ToList(), [], "teste", null, null);

    [Fact]
    public async Task Import_query_replace_and_delete()
    {
        var r = await _service.ImportAsync(Sample((1, 1, 1, "A"), (1, 1, 2, "B"), (1, 2, 1, "C"), (43, 3, 16, "D")), "arc", "Almeida RC", null, replace: false);
        Assert.Equal(4, r.Verses);

        var v = Assert.Single(await _service.GetVersionsAsync());
        Assert.Equal(("ARC", 4, 2), (v.Abbreviation, v.VerseCount, v.BookCount)); // sigla normalizada
        Assert.Equal(2, await _service.GetChapterCountAsync(v.Id, 1));
        Assert.Equal(["A", "B"], (await _service.GetChapterAsync(v.Id, 1, 1)).Select(x => x.Text));
        Assert.Equal([1, 43], (await _service.GetBooksWithTextAsync(v.Id)).Order());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ImportAsync(Sample((1, 1, 1, "X")), "ARC", "Outra", null, replace: false));

        await _service.ImportAsync(Sample((1, 1, 1, "Novo")), "ARC", "Almeida Revista e Corrigida", null, replace: true);
        v = Assert.Single(await _service.GetVersionsAsync());
        Assert.Equal(("Almeida Revista e Corrigida", 1), (v.Name, v.VerseCount));

        await _service.DeleteVersionAsync(v.Id);
        Assert.Empty(await _service.GetVersionsAsync());
    }

    [Fact]
    public async Task SearchTextAsync_ignores_accents_case_punctuation_and_oh()
    {
        var r = await _service.ImportAsync(Sample(
            (43, 3, 16, "Ó Deus, tanto amou ao mundo, que deu o seu Filho unigênito."),
            (19, 23, 1, "O SENHOR é meu pastor, nada me faltará.")), "arc", "Almeida RC", null, replace: false);

        foreach (var q in new[] { "oh deus", "o deus", "Ó DEUS", "tanto amou ao mundo que deu", "unigenito" })
            Assert.Single(await _service.SearchTextAsync(r.VersionId, q));

        Assert.Empty(await _service.SearchTextAsync(r.VersionId, "inexistente"));

        // Corrigir o texto também atualiza a coluna normalizada usada na pesquisa.
        var verse = (await _service.GetChapterAsync(r.VersionId, 19, 23)).Single();
        await _service.UpdateVersesAsync(new Dictionary<int, string> { [verse.Id] = "O Senhor é meu pastor, nada me faltara." });
        Assert.Single(await _service.SearchTextAsync(r.VersionId, "faltara"));
    }

    [Fact]
    public async Task Full_bible_import_is_fast()
    {
        var zip = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Projecao/Resources/Bible/bliv-tr_osis.zip"));
        var bible = BibleImportParser.Parse("b.zip", File.ReadAllBytes(zip));
        var r = await _service.ImportAsync(bible, "BLIVRE", "Bíblia Livre", "crédito", replace: false);
        Assert.Equal(bible.Verses.Count, r.Verses);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(10), $"importação demorou {r.Elapsed}");
        Assert.Equal("crédito", (await _service.GetVersionAsync(r.VersionId))!.Copyright);
    }

    [Fact]
    public async Task Reading_continues_across_chapters_and_books()
    {
        var zip = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Projecao/Resources/Bible/bliv-tr_osis.zip"));
        var r = await _service.ImportAsync(BibleImportParser.Parse("b.zip", File.ReadAllBytes(zip)), "BLIVRE", "Bíblia Livre", null, replace: false);
        var version = (await _service.GetVersionAsync(r.VersionId))!;
        var books = await _service.GetBooksAsync();

        var projection = new Projecao.Services.ProjectionService(NullLogger<Projecao.Services.ProjectionService>.Instance)
        {
            ReadingContinuation = _service.GetAdjacentChapter
        };

        Projecao.Models.Projection.QueueItem Reading(int bookId, int chapter) =>
            Projecao.Services.QueueItemFactory.FromVerses(version, books.First(b => b.Id == bookId), chapter,
                _service.GetChapterAsync(r.VersionId, bookId, chapter).Result, reading: true);

        // João 3 (36 versículos): no último, Próximo → João 4:1, no MESMO item da fila
        var joao3 = Reading(43, 3);
        projection.ProjectNow(joao3, slideIndex: 35);
        projection.StartProjection();
        Assert.Equal("João 3:36", projection.State.Live.Slide?.Heading);
        projection.Next();
        Assert.Equal("João 4:1", projection.State.Live.Slide?.Heading);
        // Projetado diretamente (ProjectNow), sem ＋ Adicionar: não entra na fila visível.
        Assert.Empty(projection.State.Queue);
        Assert.Equal(joao3.Id, projection.State.CurrentItemId);
        Assert.StartsWith("João 4", projection.State.CurrentItem!.Title);

        // Anterior no versículo 1 → volta ao último de João 3
        projection.Previous();
        Assert.Equal("João 3:36", projection.State.Live.Slide?.Heading);

        // Fim de livro: Malaquias 4:6 → Mateus 1:1
        projection.ProjectNow(Reading(39, 4), slideIndex: 5);
        projection.Next();
        Assert.Equal("Mateus 1:1", projection.State.Live.Slide?.Heading);

        // Limites: Apocalipse 22 no fim e Gênesis 1 no início não mudam de capítulo
        var ap22 = Reading(66, 22);
        projection.ProjectNow(ap22, slideIndex: ap22.Slides.Count - 1);
        projection.Next();
        Assert.Equal("Apocalipse 22:21", projection.State.Live.Slide?.Heading);
        Assert.Null(_service.GetAdjacentChapter(Reading(1, 1), -1));
        Assert.True(projection.State.CanGoNext == projection.State.CurrentItem!.Reading is not null);
    }

    [Fact]
    public void Schema_upgrade_adds_missing_column_to_old_database()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            // Base "antiga" (v0.3): BibleVersions sem a coluna Copyright
            cmd.CommandText = "CREATE TABLE BibleVersions (Id INTEGER PRIMARY KEY, Abbreviation TEXT, Name TEXT, Language TEXT);";
            cmd.ExecuteNonQuery();
        }
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        SchemaUpgrader.Upgrade(db);
        SchemaUpgrader.Upgrade(db); // idempotente
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('BibleVersions') WHERE name='Copyright'";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    public void Dispose() => _conn.Dispose();

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
