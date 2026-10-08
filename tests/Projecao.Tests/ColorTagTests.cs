using Projecao.Data;
using Projecao.Services.Songs;
using Xunit;

public class ColorTagTests
{
    [Fact]
    public void Colored_words_are_parsed_per_segment()
    {
        var slide = SongMarkup.Parse("QUANDO [cor=#ffff00]JESUS[/cor] DERRAMOU (2X)");
        var segs = slide.Lines[0].Segments;
        Assert.Equal(["QUANDO ", "JESUS", " DERRAMOU ", "(2X)"], segs.Select(s => s.Text));
        Assert.Equal([null, "#ffff00", null, null], segs.Select(s => s.Color));
        Assert.True(segs[3].IsParenthetical);
    }

    [Fact]
    public void Unclosed_tag_colors_until_end_of_line_only()
    {
        var slide = SongMarkup.Parse("[cor=#22c55e]LINHA VERDE\nLINHA NORMAL");
        Assert.Equal("#22c55e", slide.Lines[0].Segments.Single().Color);
        Assert.Null(slide.Lines[1].Segments.Single().Color);
    }

    [Fact]
    public void Css_injection_in_tag_is_ignored()
    {
        var slide = SongMarkup.Parse("[cor=red;background:url(x)]TEXTO[/cor]");
        Assert.All(slide.Lines[0].Segments, s => Assert.Null(s.Color));
        var named = SongMarkup.Parse("[cor=red]TEXTO[/cor]");   // só hexadecimal é aceite
        Assert.Null(named.Lines[0].Segments.Single(s => s.Text == "TEXTO").Color);
    }

    [Fact]
    public void Search_ignores_color_tags()
    {
        Assert.Equal("quando jesus derramou", TextNormalizer.NormalizeForSearch("QUANDO [cor=#ffff00]JESUS[/cor] DERRAMOU"));
        Assert.Equal("JESUS", SongMarkup.StripColorTags("[cor=#fff]JESUS[/cor]"));
    }
}

public class ApplyColorTests
{
    private const string Slide = "EU SEI QUE FOI PAGO UM ALTO PREÇO\n(M) PENSAVA EM NÓS";

    private static (int Line, int Offset) Pos(string slide, string word)
    {
        var lines = slide.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].IndexOf(word, StringComparison.Ordinal) is var k and >= 0) return (i, k);
        throw new InvalidOperationException(word);
    }

    [Fact]
    public void Colors_only_the_selected_words_like_EM_NOS()
    {
        var (l, o) = Pos(Slide, "EM NÓS");
        var result = SongMarkup.ApplyColor(Slide, l, o, l, o + "EM NÓS".Length, "#ffff00");
        Assert.Equal("EU SEI QUE FOI PAGO UM ALTO PREÇO\n(M) PENSAVA [cor=#ffff00]EM NÓS[/cor]", result);

        var line = SongMarkup.Parse(result).Lines[1];
        Assert.Equal(MarkupLineKind.Women, line.Kind);                                // continua a ser voz das mulheres
        Assert.Equal("#ffff00", line.Segments.Single(s => s.Text == "EM NÓS").Color);
        Assert.Null(line.Segments.First().Color);                                      // "(M)" e "PENSAVA" sem cor própria
    }

    [Fact]
    public void Offsets_from_parse_map_back_to_source()
    {
        // O ecrã devolve (linha, SourceStart + posição no trecho): tem de bater com o texto original
        var colored = "[cor=#22c55e]EU SEI[/cor] QUE FOI";
        var seg = SongMarkup.Parse(colored).Lines[0].Segments.Single(s => s.Text == " QUE FOI");
        Assert.Equal(" QUE FOI", colored.Substring(seg.SourceStart, seg.Text.Length));
    }

    [Fact]
    public void Recolor_inside_colored_line_and_remove_color()
    {
        var green = SongMarkup.ApplyColor("EU SEI QUE FOI", 0, 0, 0, 14, "#22c55e");
        Assert.Equal("[cor=#22c55e]EU SEI QUE FOI[/cor]", green);

        // "SEI" a amarelo dentro da linha verde: fecha o verde, pinta, reabre o verde
        var parse = SongMarkup.Parse(green).Lines[0].Segments.Single();
        var start = parse.SourceStart + "EU ".Length;
        var mixed = SongMarkup.ApplyColor(green, 0, start, 0, start + 3, "#ffff00");
        var segs = SongMarkup.Parse(mixed).Lines[0].Segments;
        Assert.Equal(["EU ", "SEI", " QUE FOI"], segs.Select(s => s.Text));
        Assert.Equal(["#22c55e", "#ffff00", "#22c55e"], segs.Select(s => s.Color));

        // Tirar a cor da linha toda
        Assert.Equal("EU SEI QUE FOI", SongMarkup.StripColorTags(SongMarkup.ApplyColor(mixed, 0, 0, 0, mixed.Length, null)));
    }

    [Fact]
    public void Multi_line_selection_and_invalid_color()
    {
        var r = SongMarkup.ApplyColor("LINHA UM\nLINHA DOIS", 0, 6, 1, 5, "#ff3b30");
        Assert.Equal("LINHA [cor=#ff3b30]UM[/cor]\n[cor=#ff3b30]LINHA[/cor] DOIS", r);
        Assert.Throws<ArgumentException>(() => SongMarkup.ApplyColor("X", 0, 0, 0, 1, "red;x"));
    }
}

public class SizeTagTests
{
    [Fact]
    public void Sized_words_are_parsed_per_segment()
    {
        var slide = SongMarkup.Parse("QUANDO [tam=130]JESUS[/tam] DERRAMOU");
        var segs = slide.Lines[0].Segments;
        Assert.Equal(["QUANDO ", "JESUS", " DERRAMOU"], segs.Select(s => s.Text));
        Assert.Equal([null, 1.3, null], segs.Select(s => s.Size));
    }

    [Fact]
    public void Color_and_size_tags_combine_independently()
    {
        var slide = SongMarkup.Parse("[cor=#ffff00][tam=150]JESUS[/tam][/cor]");
        var seg = slide.Lines[0].Segments.Single();
        Assert.Equal("#ffff00", seg.Color);
        Assert.Equal(1.5, seg.Size);
    }

    [Fact]
    public void First_change_starts_from_100_percent()
    {
        var result = SongMarkup.ChangeSize("EU SEI QUE FOI", 0, 3, 0, 6, +10);
        Assert.Equal("EU [tam=110]SEI[/tam] QUE FOI", result);
    }

    [Fact]
    public void Repeated_change_adjusts_the_existing_tag_instead_of_nesting()
    {
        var once = SongMarkup.ChangeSize("EU SEI QUE FOI", 0, 3, 0, 6, +10);
        var seg = SongMarkup.Parse(once).Lines[0].Segments.Single(s => s.Text == "SEI");
        var twice = SongMarkup.ChangeSize(once, 0, seg.SourceStart, 0, seg.SourceStart + 3, +10);
        Assert.Equal("EU [tam=120]SEI[/tam] QUE FOI", twice);
    }

    [Fact]
    public void Size_is_clamped_between_50_and_300_percent()
    {
        var tiny = SongMarkup.ChangeSize("ABC", 0, 0, 0, 3, -1000);
        Assert.Equal("[tam=50]ABC[/tam]", tiny);

        var seg = SongMarkup.Parse(tiny).Lines[0].Segments.Single();
        var huge = SongMarkup.ChangeSize(tiny, 0, seg.SourceStart, 0, seg.SourceStart + 3, +1000);
        Assert.Equal("[tam=300]ABC[/tam]", huge);
    }

    [Fact]
    public void Search_ignores_size_tags()
    {
        Assert.Equal("quando jesus derramou", TextNormalizer.NormalizeForSearch("QUANDO [tam=130]JESUS[/tam] DERRAMOU"));
        Assert.Equal("JESUS", SongMarkup.StripSizeTags("[tam=130]JESUS[/tam]"));
    }

    [Fact]
    public void Sizing_or_coloring_the_label_word_keeps_it_recognized_as_a_label()
    {
        // "*Coro" com o próprio "Coro" redimensionado: continua um rótulo (sem "*" nem colchetes à mostra).
        var slide = SongMarkup.Parse("*[tam=110]Coro[/tam]\nLETRA");
        var label = slide.Lines[0];
        Assert.Equal(MarkupLineKind.Label, label.Kind);
        Assert.Equal("Coro", label.Segments.Single().Text);
        Assert.Equal(1.1, label.Segments.Single().Size);
        Assert.True(slide.IsChorus);

        // O mesmo com cor, e sem asterisco (refrão automático).
        var colored = SongMarkup.Parse("[cor=#ffff00]Coro[/cor]\nLETRA");
        Assert.Equal(MarkupLineKind.Label, colored.Lines[0].Kind);
        Assert.Equal("#ffff00", colored.Lines[0].Segments.Single().Color);
    }
}
