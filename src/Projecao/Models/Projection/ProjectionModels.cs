using Projecao.Models;

namespace Projecao.Models.Projection;

/// <summary>Tipo de conteúdo de um item da fila de exibição.</summary>
public enum ProjectionContentKind
{
    Song,
    Bible,
    Notice,
    Media
}

/// <summary>Sobreposição exibida por cima do conteúdo (módulo "Geral").</summary>
public enum OverlayMode
{
    None,
    Clock,
    /// <summary>Contagem regressiva até <c>CountdownTarget</c>.</summary>
    Countdown,
    /// <summary>Contagem progressiva desde <c>CountdownTarget</c> (cronómetro iniciado em 00:00:00).</summary>
    Stopwatch
}

/// <summary>
/// Aparência do que se projeta (aba Configuração). Os valores por omissão seguem o Config.ini do Glorifica.
/// Tamanhos em % da altura do ecrã (máximo — o texto encolhe sozinho se não couber).
/// </summary>
public sealed record ProjectionStyle
{
    // Louvores
    public string SongFont { get; init; } = "Franklin Gothic Medium";
    public double SongMaxSizePct { get; init; } = 11;
    public string SongColor { get; init; } = "#ffffff";
    public string ChorusColor { get; init; } = "#ffff00";
    public string ParenColor { get; init; } = "#fff833";
    public string MenColor { get; init; } = "#ffc600";
    public string WomenColor { get; init; } = "#f7d868";
    public string SongTitleColor { get; init; } = "#fff3d6";
    public bool SongUppercase { get; init; } = true;
    /// <summary>"block" = linhas à esquerda num bloco centrado (Glorifica); "center" = cada linha centrada.</summary>
    public string SongAlign { get; init; } = "block";
    public bool SongBold { get; init; } = true;

    // Bíblia
    public string BibleFont { get; init; } = "Arial";
    public double BibleMaxSizePct { get; init; } = 11;
    public string BibleColor { get; init; } = "#ffffff";
    public string BibleRefColor { get; init; } = "#ffff00";
    public string BibleAlign { get; init; } = "center";

    // Geral
    public string ClockColor { get; init; } = "#f7f0d2";

    public static ProjectionStyle Default { get; } = new();
}

/// <summary>
/// Fundo (imagem ou vídeo em loop) já convertido em URL servível.
/// <paramref name="RelativePath"/> identifica o ficheiro na Galeria (para marcar "SELECIONADO!").
/// </summary>
public sealed record BackgroundMedia(string Url, bool IsVideo, string? RelativePath = null);

/// <summary>Formatação opcional de um slide (usada sobretudo nos Avisos).</summary>
public sealed record SlideStyle(
    string? FontFamily = null,
    int? FontSizePx = null,
    string? ForeColor = null,
    string? BackColor = null,
    TextAlignment TextAlign = TextAlignment.Center);

/// <summary>
/// Um "ecrã" projetado.
/// <paramref name="Heading"/>: texto da faixa superior (título do louvor no 1.º slide, referência bíblica).
/// <paramref name="Markup"/>: o texto usa a sintaxe de letras do Glorifica (Coro, (H)/(M), parênteses…).
/// <paramref name="Layout"/>: disposição (áreas de título/texto) — ver ProjectionView.
/// <paramref name="Html"/>: <c>Text</c> é HTML já sanitizado (avisos formatados).
/// <paramref name="Credit"/>: texto discreto no canto inferior direito (crédito de licença, ex.: "BLIVRE";
/// ou "FIM" no último slide de um louvor).
/// </summary>
public sealed record ProjectionSlide(
    string Text,
    string? Caption = null,
    SlideStyle? Style = null,
    string? Heading = null,
    bool Markup = false,
    SlideLayout Layout = SlideLayout.Full,
    bool Html = false,
    string? Credit = null);

/// <summary>Áreas de texto baseadas nos modelos do Glorifica (coordenadas em 1920x1080).</summary>
public enum SlideLayout
{
    /// <summary>Texto em quase todo o ecrã.</summary>
    Full,
    /// <summary>1.º slide do louvor: título na faixa superior + letra por baixo.</summary>
    SongFirst,
    /// <summary>Bíblia: referência na faixa + versículo por baixo.</summary>
    Bible
}

/// <summary>Leitura bíblica contínua: permite ao Próximo/Anterior passar de capítulo sozinho.</summary>
public sealed record BibleReadingRef(int VersionId, int BookId, int Chapter);

/// <summary>Item da fila de exibição (um louvor, uma passagem, um aviso ou uma mídia).</summary>
public sealed record QueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required ProjectionContentKind Kind { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<ProjectionSlide> Slides { get; init; }

    /// <summary>Fundo próprio do item; quando null usa o fundo global da Galeria.</summary>
    public BackgroundMedia? Background { get; init; }

    /// <summary>Fundo exclusivo do 1.º slide (ex.: com faixa "LOUVOR"); null = igual aos restantes.</summary>
    public BackgroundMedia? FirstSlideBackground { get; init; }

    /// <summary>Id da entidade de origem na base (Song.Id, Notice.Id…).</summary>
    public int? SourceId { get; init; }

    /// <summary>Preenchido nas leituras bíblicas (capítulo inteiro): o Próximo continua no capítulo seguinte.</summary>
    public BibleReadingRef? Reading { get; init; }

    public string KindLabel => Kind switch
    {
        ProjectionContentKind.Song => "Louvor",
        ProjectionContentKind.Bible => "Bíblia",
        ProjectionContentKind.Notice => "Aviso",
        ProjectionContentKind.Media => "Mídia",
        _ => Kind.ToString()
    };
}

/// <summary>
/// Fotografia imutável do que está no projetor neste momento.
/// É isto que a <c>ProjectionView</c> desenha — nada mais.
/// </summary>
public sealed record LiveFrame(
    ProjectionSlide? Slide,
    BackgroundMedia? Background,
    bool IsBlank,
    OverlayMode Overlay,
    DateTimeOffset? CountdownTarget,
    long ContentVersion,
    bool OverlayFullScreen = false,
    BackgroundMedia? GeneralBackground = null)
{
    public static LiveFrame Empty { get; } = new(null, null, false, OverlayMode.None, null, 0);
}

/// <summary>
/// Estado completo (imutável) da projeção. Cada alteração gera uma nova instância,
/// por isso os componentes podem ler <c>ProjectionService.State</c> sem locks.
/// </summary>
public sealed record ProjectionState
{
    public IReadOnlyList<QueueItem> Queue { get; init; } = [];

    /// <summary>Item escolhido num dos módulos (esquerda) e pronto a "Adicionar" à fila.</summary>
    public QueueItem? Staged { get; init; }

    /// <summary>Slide onde o item preparado começa (ex.: leitura bíblica a partir do versículo escolhido).</summary>
    public int StagedSlideIndex { get; init; }

    public Guid? CurrentItemId { get; init; }

    public int CurrentSlideIndex { get; init; }

    public bool IsProjecting { get; init; }

    public bool IsFrozen { get; init; }

    public bool IsBlank { get; init; }

    /// <summary>Fundo global dos slides (módulo Galeria).</summary>
    public BackgroundMedia? GlobalBackground { get; init; }

    /// <summary>Fundo global do 1.º slide de cada item (como no Glorifica: "Primeiro Slide" vs "Resto").</summary>
    public BackgroundMedia? GlobalFirstSlideBackground { get; init; }

    public OverlayMode Overlay { get; init; }

    public DateTimeOffset? CountdownTarget { get; init; }

    /// <summary>
    /// true = relógio/cronómetro ocupa o ecrã inteiro (sobre o fundo "Geral");
    /// false = "Overlay" no canto, por cima do conteúdo projetado.
    /// </summary>
    public bool OverlayFullScreen { get; init; }

    /// <summary>Fundo do relógio/cronómetro em ecrã inteiro (módulo Geral).</summary>
    public BackgroundMedia? GeneralBackground { get; init; }

    /// <summary>
    /// Margem de segurança (% da largura/altura em cada lado). Muitas TVs cortam
    /// as bordas da imagem (overscan); o texto tem de ficar dentro da área visível.
    /// </summary>
    public double SafeAreaPercent { get; init; }

    /// <summary>Aparência (fontes, cores, tamanhos) — aba Configuração.</summary>
    public ProjectionStyle Style { get; init; } = ProjectionStyle.Default;

    /// <summary>O que o projetor mostra (pode diferir do "atual" quando congelado).</summary>
    public LiveFrame Live { get; init; } = LiveFrame.Empty;

    // ---------------- Propriedades derivadas ----------------

    public int CurrentItemIndex =>
        CurrentItemId is { } id ? IndexOf(id) : -1;

    public QueueItem? CurrentItem =>
        CurrentItemIndex is var i and >= 0 ? Queue[i] : null;

    public ProjectionSlide? CurrentSlide =>
        CurrentItem is { } item && CurrentSlideIndex >= 0 && CurrentSlideIndex < item.Slides.Count
            ? item.Slides[CurrentSlideIndex]
            : null;

    /// <summary>
    /// Fundo efetivo do slide atual. Prioridade: fundo do item → fundo global,
    /// usando a variante "primeiro slide" quando estamos no slide 0.
    /// </summary>
    public BackgroundMedia? CurrentBackground
    {
        get
        {
            var item = CurrentItem;
            if (CurrentSlideIndex == 0)
            {
                var first = item is null ? GlobalFirstSlideBackground
                    : item.FirstSlideBackground ?? (item.Background is null ? GlobalFirstSlideBackground : null);
                if (first is not null)
                    return first;
            }
            return item?.Background ?? GlobalBackground;
        }
    }

    public bool CanGoNext =>
        CurrentItem is { } item
            ? CurrentSlideIndex < item.Slides.Count - 1 || CurrentItemIndex < Queue.Count - 1 || item.Reading is not null
            : Queue.Count > 0 || Staged is not null;

    public bool CanGoPrevious =>
        CurrentItem is { } item && (CurrentSlideIndex > 0 || CurrentItemIndex > 0 || item.Reading is not null);

    public int IndexOf(Guid id)
    {
        for (var i = 0; i < Queue.Count; i++)
            if (Queue[i].Id == id)
                return i;
        return -1;
    }
}
