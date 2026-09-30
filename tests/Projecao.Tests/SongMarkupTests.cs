using Projecao.Services;
using Projecao.Services.Songs;
using Xunit;

public class SongMarkupTests
{
    [Fact]
    public void Plain_lyrics_one_slide_per_stanza()
    {
        Assert.Equal(["A\nB", "C"], SongMarkup.BuildSlides("A\r\nB\r\n\r\n\r\nC\r\n"));
    }

    [Fact]
    public void Tutorial_example_auto_chorus_after_each_following_stanza()
    {
        // Exemplo do tutorial do I-LOUVORES: V1, Coro, V2 → V1, Coro, V2, Coro
        var slides = SongMarkup.BuildSlides("V1\n\nCoro\nA tua palavra\n\nV2");
        Assert.Equal(["V1", "Coro\nA tua palavra", "V2", "Coro\nA tua palavra"], slides);
    }

    [Fact]
    public void Final_stops_without_chorus()
    {
        var slides = SongMarkup.BuildSlides("V1\n\nCoro 2x\nR\n\nV2\n\nFinal\nÚltima\n\nNão deve aparecer");
        Assert.Equal(["V1", "Coro 2x\nR", "V2", "Coro 2x\nR", "Final\nÚltima"], slides);
    }

    [Fact]
    public void Asterisk_chorus_makes_song_manual()
    {
        var slides = SongMarkup.BuildSlides("V1\n\n*Coro\nR\n\nV2\n\n*Coro 2x\nR");
        Assert.Equal(["V1", "*Coro\nR", "V2", "*Coro 2x\nR"], slides);
    }

    [Fact]
    public void Parse_labels_voices_parentheses_and_slash()
    {
        var s = SongMarkup.Parse("*CORO 2X\n(M) Alegrai-vos,\n(H) Regozijai-vos (2x)\n/Instrumentos");
        Assert.True(s.IsChorus);
        Assert.Equal(MarkupLineKind.Label, s.Lines[0].Kind);
        Assert.Equal("CORO 2X", s.Lines[0].Segments[0].Text);             // asterisco não aparece
        Assert.Equal(MarkupLineKind.Women, s.Lines[1].Kind);
        Assert.Equal(MarkupLineKind.Men, s.Lines[2].Kind);
        Assert.Contains(s.Lines[2].Segments, g => g.IsParenthetical && g.Text == "(2x)");
        Assert.Equal(MarkupLineKind.Blank, s.Lines[3].Kind);               // "/" → linha em branco
        Assert.Equal("Instrumentos", s.Lines[4].Segments[0].Text);
        Assert.False(SongMarkup.Parse("Cantar 2x\nTexto").IsChorus);
        Assert.Equal(MarkupLineKind.Label, SongMarkup.Parse("Cantar 2x\nTexto").Lines[0].Kind);
    }

    [Fact]
    public void Song_queue_item_has_title_heading_on_first_slide()
    {
        var item = QueueItemFactory.FromSong(new Projecao.Models.Song { Title = "Agnus Dei", Number = 7, Lyrics = "V1\n\nCoro\nR\n\nV2" });
        Assert.Equal("7 - Agnus Dei", item.Title);
        Assert.Equal(4, item.Slides.Count);
        Assert.Equal("Agnus Dei", item.Slides[0].Heading);
        Assert.Equal(Projecao.Models.Projection.SlideLayout.SongFirst, item.Slides[0].Layout);
        Assert.Null(item.Slides[1].Heading);
        Assert.All(item.Slides, s => Assert.True(s.Markup));
    }
}
