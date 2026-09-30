using Projecao.Models;
using Projecao.Models.Projection;
using Projecao.Services.Songs;

namespace Projecao.Services;

/// <summary>Converte entidades da base em itens da fila de exibição.</summary>
public static class QueueItemFactory
{
    /// <summary>
    /// Um slide por estrofe, com as repetições automáticas do refrão (sintaxe Glorifica).
    /// O 1.º slide leva o título na faixa superior; o último leva "FIM" no canto (Credit).
    /// </summary>
    public static QueueItem FromSong(Song song, BackgroundMedia? background = null, BackgroundMedia? firstSlideBackground = null)
    {
        var title = song.Number is { } n ? $"{n} - {song.Title}" : song.Title;
        var stanzas = SongMarkup.BuildSlides(song.Lyrics);
        var last = stanzas.Count - 1;
        IReadOnlyList<ProjectionSlide> slides = stanzas.Count > 0
            ? stanzas.Select((t, i) => i == 0
                ? new ProjectionSlide(t, Heading: song.Title, Markup: true, Layout: SlideLayout.SongFirst, Credit: i == last ? "FIM" : null)
                : new ProjectionSlide(t, Markup: true, Credit: i == last ? "FIM" : null)).ToList()
            : [new ProjectionSlide(string.Empty, Heading: song.Title, Layout: SlideLayout.SongFirst, Credit: "FIM")];

        return new QueueItem
        {
            Kind = ProjectionContentKind.Song,
            Title = title,
            Slides = slides,
            Background = background,
            FirstSlideBackground = firstSlideBackground,
            SourceId = song.Id
        };
    }

    /// <summary>
    /// Um slide por versículo, com a referência na faixa superior ("João 3:16", como no Glorifica).
    /// A sigla da versão não aparece na referência; versões Creative Commons (ex.: Bíblia Livre)
    /// levam a sigla como crédito discreto no canto, como a licença exige.
    /// <paramref name="reading"/>: leitura contínua (capítulo inteiro, o Próximo avança versículo a versículo).
    /// </summary>
    public static QueueItem FromVerses(BibleVersion version, BibleBook book, int chapter, IReadOnlyList<BibleVerse> verses,
        BackgroundMedia? background = null, bool reading = false)
    {
        if (verses.Count == 0)
            throw new ArgumentException("É necessário pelo menos um versículo.", nameof(verses));

        var credit = RequiresAttribution(version) ? version.Abbreviation : null;
        var ordered = verses.OrderBy(v => v.Verse).ToList();
        var slides = ordered
            .Select(v => new ProjectionSlide(v.Text, Heading: $"{book.Name} {chapter}:{v.Verse}", Layout: SlideLayout.Bible, Credit: credit))
            .ToList();

        var first = ordered[0].Verse;
        var last = ordered[^1].Verse;
        var range = first == last ? $"{first}" : $"{first}-{last}";

        return new QueueItem
        {
            Kind = ProjectionContentKind.Bible,
            Title = reading
                ? $"{book.Name} {chapter} — leitura ({version.Abbreviation})"
                : $"{book.Name} {chapter}:{range} ({version.Abbreviation})",
            Slides = slides,
            Reading = reading ? new BibleReadingRef(version.Id, book.Id, chapter) : null,
            // Fundo próprio da Bíblia também no 1.º slide (não usa o fundo "1.º slide" dos louvores).
            Background = background,
            FirstSlideBackground = background
        };
    }

    /// <summary>Licenças Creative Commons "Atribuição" exigem o crédito também na projeção.</summary>
    public static bool RequiresAttribution(BibleVersion version) =>
        version.Copyright?.Contains("Creative Commons", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Aviso formatado (HTML sanitizado). BackColor = cor sólida de fundo (senão, fundo da Galeria).</summary>
    public static QueueItem FromNotice(Notice notice) => FromNoticeHtml(notice.Title, notice.Content, notice.BackColor, notice.Id);

    public static QueueItem FromNoticeHtml(string title, string html, string? backColor, int? sourceId = null) => new()
    {
        Kind = ProjectionContentKind.Notice,
        Title = string.IsNullOrWhiteSpace(title) ? "Aviso" : title,
        Slides = [new ProjectionSlide(Notices.NoticeService.Sanitize(html), Style: new SlideStyle(BackColor: backColor), Html: true)],
        SourceId = sourceId
    };

    /// <summary>Mídia "pura": apenas o fundo, sem texto.</summary>
    public static QueueItem FromMedia(MediaFile media) => new()
    {
        Kind = ProjectionContentKind.Media,
        Title = media.Name,
        Slides = [new ProjectionSlide(string.Empty)],
        Background = media.ToBackground()
    };
}
