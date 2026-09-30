using System.IO.Compression;
using System.Text;
using Projecao.Services.Import;
using Projecao.Services.Songs;
using Xunit;

public class PptxImportTests
{
    // Caixa de texto com posição (em fração do slide) — como nos PowerPoints das coletâneas.
    private static string Box(double x, double y, double w, double h, params string[] paragraphs)
    {
        const long W = 12192000, H = 6858000;
        var ps = string.Concat(paragraphs.Select(p => $"<a:p><a:r><a:t>{System.Security.SecurityElement.Escape(p)}</a:t></a:r></a:p>"));
        return $"""<p:sp><p:nvSpPr><p:cNvPr id="1" name="Rectangle"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="{(long)(x * W)}" y="{(long)(y * H)}"/><a:ext cx="{(long)(w * W)}" cy="{(long)(h * H)}"/></a:xfrm></p:spPr><p:txBody>{ps}</p:txBody></p:sp>""";
    }

    private static byte[] Pptx(params string[][] slides)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string xml) { using var w = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8); w.Write(xml); }
            const string ns = """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" """;
            var ids = string.Concat(slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 1}\"/>"));
            Put("ppt/presentation.xml", $"""<p:presentation {ns}><p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx="12192000" cy="6858000"/></p:presentation>""");
            var rels = string.Concat(slides.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Target=\"slides/slide{i + 1}.xml\"/>"));
            Put("ppt/_rels/presentation.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{rels}</Relationships>""");
            for (var i = 0; i < slides.Length; i++)
                Put($"ppt/slides/slide{i + 1}.xml", $"""<p:sld {ns}><p:cSld><p:spTree>{string.Concat(slides[i])}</p:spTree></p:cSld></p:sld>""");
        }
        return ms.ToArray();
    }

    [Fact]
    public void Parses_titles_numbers_stanzas_and_annotations()
    {
        var bytes = Pptx(
            [Box(.1, .03, .8, .1, "COLETÂNEA DE LOUVORES – IGREJA – EDIÇÃO 2018"), Box(.7, .9, .2, .05, "ATUALIZAÇÃO 04.09.2026")],
            [Box(.24, .034, .52, .08, "01 – O SANGUE DE JESUS"),
             Box(.06, .28, .88, .45, "LINHA UM,", "LINHA DOIS,", "", "LINHA TRÊS"),
             Box(.84, .40, .07, .09, "BIS")],                                                        // ao lado da linha 2
            [Box(.03, .2, .93, .52, "CORO", "REFRÃO A,", "REFRÃO B"), Box(.93, .97, .07, .04, "Índice")],
            [Box(.32, .036, .41, .08, "02 - CLAMO A TI"),
             Box(.21, .21, .58, .6, "VENHA SOBRE NÓS,", "SEJA SOBRE NÓS"),
             Box(.80, .22, .08, .06, "VARÕES")]);                                                    // ao lado da linha 1

        var r = SongImportParser.Parse("01 COLETANEA.pptx", bytes);
        Assert.DoesNotContain(r.Issues, i => !i.Message.Contains("não tem letra"));
        Assert.Equal(2, r.Songs.Count);

        var s1 = r.Songs[0];
        Assert.Equal((1, "O SANGUE DE JESUS", "Coletânea 2018"), (s1.Number!.Value, s1.Title, s1.Collection!));
        var slides = SongMarkup.BuildSlides(s1.Lyrics);
        Assert.Equal(2, slides.Count);                                          // linha em branco NÃO parte o slide
        Assert.Equal("LINHA UM,\nLINHA DOIS, (BIS)\n/LINHA TRÊS", slides[0]);
        Assert.Equal("*CORO\nREFRÃO A,\nREFRÃO B", slides[1]);                  // sem repetição automática
        Assert.DoesNotContain("Índice", s1.Lyrics);

        var s2 = r.Songs[1];
        Assert.Equal((2, "CLAMO A TI"), (s2.Number!.Value, s2.Title));
        Assert.StartsWith("(H) VENHA SOBRE NÓS,", s2.Lyrics);
    }

    [Fact]
    public void Avulsos_file_name_sets_collection_and_invalid_file_is_reported()
    {
        var r = SongImportParser.Parse("02 LOUVORES AVULSOS.pptx", Pptx([Box(.1, .02, .85, .1, "A ESPERANÇA"), Box(.2, .25, .6, .5, "DOCE ESPERANÇA,", "BENDITA LUZ")]));
        Assert.Equal(("A ESPERANÇA", "Avulsos"), (r.Songs.Single().Title, r.Songs.Single().Collection!));

        var bad = SongImportParser.Parse("x.pptx", [1, 2, 3]);
        Assert.Empty(bad.Songs);
        Assert.Single(bad.Issues);
    }
}
