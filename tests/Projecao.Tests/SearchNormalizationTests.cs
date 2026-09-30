using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Models;
using Projecao.Services;
using Projecao.Services.Songs;
using Xunit;

public class SearchNormalizationTests
{
    [Theory]
    [InlineData("ÓH NUNCA, NUNCA CESSARÃO!", "o nunca nunca cessarao")]
    [InlineData("Oh, que amor!", "o que amor")]
    [InlineData("Ó Senhor", "o senhor")]
    [InlineData("MINH'ALMA ESTÁ", "minhalma esta")]
    [InlineData("AQUELE QUE HÁ DE VIR, VIRÁ!", "aquele que ha de vir vira")]
    [InlineData("*CORO\n(H) Ele pensava em ti... (2X)", "coro h ele pensava em ti 2x")]
    [InlineData("ALELUIA — AMÉM; “GLÓRIA”", "aleluia amem gloria")]
    [InlineData("  ", "")]
    public void Normalizes_for_search(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.NormalizeForSearch(input));

    [Fact]
    public async Task Search_ignores_accents_case_punctuation_and_oh()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var db = new AppDbContext(options))
        {
            DbInitializer.Initialize(db);
            db.Songs.Add(new Song { Title = "ÓH NUNCA, NUNCA CESSARÃO", Lyrics = "ÓH NUNCA, NUNCA CESSARÃO,\nOS HINOS DE LOUVOR." });
            db.Songs.Add(new Song { Title = "AQUELE QUE HÁ DE VIR, VIRÁ!", Lyrics = "Minh'alma espera, Senhor." });
            db.SaveChanges();
        }

        var root = Directory.CreateTempSubdirectory().FullName;
        var songs = new SongService(new Factory(options), new ProjectionService(NullLogger<ProjectionService>.Instance),
            new MediaLibraryService(new AppPaths(root, root)));

        foreach (var q in new[] { "oh nunca nunca cessarao", "ó nunca, nunca", "OH NUNCA", "nunca cessarão" })
            Assert.Equal("ÓH NUNCA, NUNCA CESSARÃO", Assert.Single(await songs.SearchAsync(q)).Title);

        Assert.Single(await songs.SearchAsync("aquele que ha de vir vira"));      // sem vírgula nem "!"
        Assert.Single(await songs.SearchAsync("minhalma espera senhor"));       // letra, sem apóstrofo
        Assert.Single(await songs.SearchAsync("hinos de louvor."));             // ponto final na pesquisa
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("nao", "Nossa fé, não vacila", true)]              // sem acento encontra com acento
    [InlineData("tanto amou", "Deus, tanto amou o mundo", true)]   // pontuação entre as palavras
    [InlineData("oh senhor", "Ó Senhor", true)]                    // "oh" ⇄ "ó"
    [InlineData("ó senhor", "Oh, Senhor", true)]                   // e vice-versa
    [InlineData("xyz", "Nada a ver", false)]
    public void LoosePattern_matches_like_NormalizeForSearch(string term, string text, bool matches)
    {
        var pattern = TextNormalizer.LoosePattern(term);
        Assert.Equal(matches, System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public void LoosePattern_of_blank_term_is_empty() => Assert.Equal("", TextNormalizer.LoosePattern("  "));

    [Fact]
    public void Upgrade_recomputes_search_columns_of_existing_songs()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var db = new AppDbContext(options))
        {
            DbInitializer.Initialize(db);
            // Simula uma base da versão anterior: colunas normalizadas com pontuação e user_version = 1
            db.Database.ExecuteSqlRaw("INSERT INTO Songs (Title, Lyrics, NormalizedTitle, NormalizedLyrics, CreatedAt, UpdatedAt) VALUES ('ÓH, JESUS!', 'x', 'oh, jesus!', 'x', '2026-01-01', '2026-01-01')");
            db.Database.ExecuteSqlRaw("PRAGMA user_version = 1;");
        }
        using (var db = new AppDbContext(options))
        {
            SchemaUpgrader.Upgrade(db);
            Assert.Equal("o jesus", db.Songs.Single(s => s.Title == "ÓH, JESUS!").NormalizedTitle);
        }
    }

    [Fact]
    public void Upgrade_recomputes_search_column_of_existing_bible_verses()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var db = new AppDbContext(options))
        {
            DbInitializer.Initialize(db);
            // Simula uma base da versão anterior: NormalizedText desatualizado e user_version = 2
            db.Database.ExecuteSqlRaw("INSERT INTO BibleVersions (Abbreviation, Name, Language) VALUES ('ARC', 'Almeida', 'pt')");
            db.Database.ExecuteSqlRaw("INSERT INTO BibleVerses (VersionId, BookId, Chapter, Verse, Text, NormalizedText) " +
                "VALUES (1, 1, 1, 1, 'Ó Deus, tanto amou!', 'errado')");
            db.Database.ExecuteSqlRaw("PRAGMA user_version = 2;");
        }
        using (var db = new AppDbContext(options))
        {
            SchemaUpgrader.Upgrade(db);
            Assert.Equal("o deus tanto amou", db.BibleVerses.Single().NormalizedText);
        }
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
