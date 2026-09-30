using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Projecao.Data;
using Projecao.Models;
using Projecao.Services;
using Projecao.Services.Songs;
using Xunit;

public class CollectionNumberSearchTests
{
    private static readonly string[] Collections = ["CIAS", "Coletânea 2018", "Avulsos"];

    [Theory]
    [InlineData("12", 12, null)]
    [InlineData("cias 12", 12, "CIAS")]
    [InlineData("cias12", 12, "CIAS")]
    [InlineData("12 cias", 12, "CIAS")]
    [InlineData("coletanea 2018 7", 7, "Coletânea 2018")]
    [InlineData("col 2018 7", 7, "Coletânea 2018")]
    [InlineData("avulsos 300", 300, "Avulsos")]
    public void Parses_collection_and_number(string term, int number, string? collection)
    {
        var q = SongSearch.ParseNumberQuery(term, Collections);
        Assert.NotNull(q);
        Assert.Equal(number, q.Number);
        if (collection is null) Assert.Null(q.Collections);
        else Assert.Equal(collection, Assert.Single(q.Collections!));
    }

    [Theory]
    [InlineData("salmo 23")]          // não há coletânea "Salmo": pesquisa pelo título
    [InlineData("coletanea 2018")]    // é o nome da coletânea, não o nº 2018
    [InlineData("alto preco")]
    public void Not_a_number_query(string term) => Assert.Null(SongSearch.ParseNumberQuery(term, Collections));

    [Fact]
    public async Task Search_by_collection_and_number()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var db = new AppDbContext(options))
        {
            DbInitializer.Initialize(db);
            db.Songs.Add(new Song { Title = "LOUVOR CIAS", Lyrics = "a", Collection = "CIAS", Number = 12 });
            db.Songs.Add(new Song { Title = "LOUVOR 2018", Lyrics = "b", Collection = "Coletânea 2018", Number = 12 });
            db.Songs.Add(new Song { Title = "SALMO 23", Lyrics = "c", Collection = "Avulsos", Number = 5 });
            db.SaveChanges();
        }

        var root = Directory.CreateTempSubdirectory().FullName;
        var songs = new SongService(new Factory(options), new ProjectionService(NullLogger<ProjectionService>.Instance),
            new MediaLibraryService(new AppPaths(root, root)));

        Assert.Equal(2, (await songs.SearchAsync("12")).Count);
        Assert.Equal("LOUVOR CIAS", Assert.Single(await songs.SearchAsync("CIAS 12")).Title);
        Assert.Equal("LOUVOR CIAS", Assert.Single(await songs.SearchAsync("cias-12")).Title);
        Assert.Equal("LOUVOR 2018", Assert.Single(await songs.SearchAsync("Coletânea 2018 12")).Title);
        Assert.Equal("SALMO 23", Assert.Single(await songs.SearchAsync("salmo 23")).Title);
        Directory.Delete(root, recursive: true);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
