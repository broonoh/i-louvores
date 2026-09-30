using Microsoft.Extensions.Logging;
using Projecao.Models.Projection;

namespace Projecao.Services;

/// <summary>
/// Fonte única de verdade da projeção (registado como Singleton).
///
/// O painel de controlo (janela principal) chama os métodos de comando; a
/// <c>ProjectionView</c> (janela do projetor) apenas lê <see cref="State"/>.<see cref="ProjectionState.Live"/>
/// e redesenha quando <see cref="OnStateChanged"/> dispara. Como as duas janelas
/// partilham o mesmo contentor de DI, partilham a mesma instância deste serviço.
///
/// Concorrência: o estado é um record imutável substituído atomicamente sob lock.
/// Leitores nunca precisam de lock; o evento é sempre disparado fora do lock.
/// </summary>
public sealed class ProjectionService(ILogger<ProjectionService> logger)
{
    private readonly object _gate = new();
    private ProjectionState _state = new();

    /// <summary>Disparado após qualquer alteração de estado.</summary>
    public event Action? OnStateChanged;

    /// <summary>Estado atual (instância imutável — seguro de ler de qualquer thread).</summary>
    public ProjectionState State => Volatile.Read(ref _state);

    // =====================================================================
    // Fila de exibição
    // =====================================================================

    /// <summary>Prepara um item escolhido num módulo (esquerda) para ser adicionado.</summary>
    public void Stage(QueueItem? item, int startSlide = 0) => Mutate(s => s with
    {
        Staged = item,
        StagedSlideIndex = item is null ? 0 : Clamp(startSlide, item.Slides.Count)
    });

    /// <summary>
    /// Botão "Adicionar": coloca o item preparado no fim da fila. Se a fila não tiver item atual,
    /// ele passa a ser o atual, já no slide preparado (ex.: o versículo escolhido).
    /// </summary>
    public void AddStaged() => Mutate(s =>
    {
        if (s.Staged is not { } staged)
            return s;

        var queue = s.IndexOf(staged.Id) >= 0 ? s.Queue : [.. s.Queue, staged];
        return s.CurrentItemId is null
            ? s with { Queue = queue, CurrentItemId = staged.Id, CurrentSlideIndex = s.StagedSlideIndex }
            : s with { Queue = queue };
    });

    /// <summary>
    /// Adiciona um item ao fim da fila. Se ainda não houver item atual, este passa a sê-lo.
    /// Itens com o mesmo Id não são duplicados.
    /// </summary>
    public void Enqueue(QueueItem item, bool select = false) => Mutate(s =>
    {
        var queue = s.IndexOf(item.Id) >= 0 ? s.Queue : [.. s.Queue, item];
        var shouldSelect = select || s.CurrentItemId is null;
        return s with
        {
            Queue = queue,
            CurrentItemId = shouldSelect ? item.Id : s.CurrentItemId,
            CurrentSlideIndex = shouldSelect ? 0 : s.CurrentSlideIndex
        };
    });

    /// <summary>Adiciona (se preciso) e torna o item atual, já no slide indicado.</summary>
    public void ProjectNow(QueueItem item, int slideIndex = 0) => Mutate(s =>
    {
        var queue = s.IndexOf(item.Id) >= 0 ? s.Queue : [.. s.Queue, item];
        return s with
        {
            Queue = queue,
            CurrentItemId = item.Id,
            CurrentSlideIndex = Clamp(slideIndex, item.Slides.Count)
        };
    });

    /// <summary>Substitui um item existente (ex.: letra editada) mantendo a posição.</summary>
    public void ReplaceItem(QueueItem item) => Mutate(s =>
    {
        var index = s.IndexOf(item.Id);
        if (index < 0)
            return s;

        var queue = s.Queue.ToList();
        queue[index] = item;
        var slide = s.CurrentItemId == item.Id ? Clamp(s.CurrentSlideIndex, item.Slides.Count) : s.CurrentSlideIndex;
        return s with { Queue = queue, CurrentSlideIndex = slide };
    });

    /// <summary>Botão "Deletar". Se for o item atual, o seguinte (ou o anterior) assume.</summary>
    public void Remove(Guid itemId) => Mutate(s =>
    {
        var index = s.IndexOf(itemId);
        if (index < 0)
            return s;

        var queue = s.Queue.Where(q => q.Id != itemId).ToList();
        if (s.CurrentItemId != itemId)
            return s with { Queue = queue };

        Guid? nextId = queue.Count == 0 ? null : queue[Math.Min(index, queue.Count - 1)].Id;
        return s with { Queue = queue, CurrentItemId = nextId, CurrentSlideIndex = 0 };
    });

    /// <summary>Move um item na fila (delta -1 = subir, +1 = descer).</summary>
    public void Move(Guid itemId, int delta) => Mutate(s =>
    {
        var from = s.IndexOf(itemId);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= s.Queue.Count)
            return s;

        var queue = s.Queue.ToList();
        (queue[from], queue[to]) = (queue[to], queue[from]);
        return s with { Queue = queue };
    });

    public void ClearQueue() => Mutate(s => s with { Queue = [], CurrentItemId = null, CurrentSlideIndex = 0 });

    /// <summary>
    /// Ao mudar de aba: esvazia a fila e o "Escolhido", mas o telão CONTINUA a mostrar o que
    /// tinha até se projetar algo novo (não apaga o ecrã a meio de um louvor).
    /// </summary>
    public void ClearQueueKeepScreen() =>
        Mutate(s => s with { Queue = [], CurrentItemId = null, CurrentSlideIndex = 0, Staged = null, StagedSlideIndex = 0 }, keepScreen: true);

    // =====================================================================
    // Navegação
    // =====================================================================

    /// <summary>Botão "Selecionar": torna o item atual.</summary>
    public void Select(Guid itemId, int slideIndex = 0) => Mutate(s =>
    {
        var index = s.IndexOf(itemId);
        if (index < 0)
            return s;

        return s with { CurrentItemId = itemId, CurrentSlideIndex = Clamp(slideIndex, s.Queue[index].Slides.Count) };
    });

    public void GoToSlide(int slideIndex) => Mutate(s =>
        s.CurrentItem is { } item ? s with { CurrentSlideIndex = Clamp(slideIndex, item.Slides.Count) } : s);

    /// <summary>
    /// Fornece o capítulo seguinte (+1) ou anterior (−1) de uma leitura bíblica, ou null se não houver.
    /// Registado no arranque (BibleService); fica fora do serviço para este não depender da base de dados.
    /// </summary>
    public Func<QueueItem, int, QueueItem?>? ReadingContinuation { get; set; }

    /// <summary>Próximo slide; no último slide salta para o início do item seguinte.</summary>
    public void Next()
    {
        if (TryContinueReading(+1))
            return;
        NextCore();
    }

    /// <summary>Slide anterior; no primeiro slide volta ao último slide do item anterior.</summary>
    public void Previous()
    {
        if (TryContinueReading(-1))
            return;
        PreviousCore();
    }

    /// <summary>
    /// Leitura bíblica: no último versículo (Próximo) ou no primeiro (Anterior) troca o conteúdo
    /// do MESMO item da fila pelo capítulo vizinho. A consulta à base é feita fora do lock.
    /// </summary>
    private bool TryContinueReading(int direction)
    {
        var s = State;
        if (s.CurrentItem is not { Reading: not null } item || ReadingContinuation is null)
            return false;

        var atEdge = direction > 0 ? s.CurrentSlideIndex >= item.Slides.Count - 1 : s.CurrentSlideIndex <= 0;
        if (!atEdge)
            return false;

        QueueItem? adjacent;
        try
        {
            adjacent = ReadingContinuation(item, direction);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao carregar o capítulo seguinte da leitura");
            return false;
        }
        if (adjacent is null || adjacent.Slides.Count == 0)
            return false; // Gn 1 / Ap 22: comportamento normal da fila

        var replaced = false;
        Mutate(current =>
        {
            // Só aplica se entretanto nada mudou (outro clique, outra janela…).
            var index = current.IndexOf(item.Id);
            if (index < 0 || current.CurrentItemId != item.Id || current.CurrentSlideIndex != s.CurrentSlideIndex)
                return current;

            var queue = current.Queue.ToList();
            queue[index] = adjacent with { Id = item.Id };
            replaced = true;
            return current with { Queue = queue, CurrentSlideIndex = direction > 0 ? 0 : adjacent.Slides.Count - 1 };
        });
        return replaced;
    }

    private void NextCore() => Mutate(s =>
    {
        if (s.CurrentItem is not { } item)
        {
            if (s.Queue.Count > 0)
                return s with { CurrentItemId = s.Queue[0].Id, CurrentSlideIndex = 0 };

            // Fila vazia mas algo escolhido à esquerda: o Próximo começa por aí.
            return s.Staged is { } staged
                ? s with { Queue = [staged], CurrentItemId = staged.Id, CurrentSlideIndex = s.StagedSlideIndex }
                : s;
        }

        if (s.CurrentSlideIndex < item.Slides.Count - 1)
            return s with { CurrentSlideIndex = s.CurrentSlideIndex + 1 };

        var nextIndex = s.CurrentItemIndex + 1;
        return nextIndex < s.Queue.Count
            ? s with { CurrentItemId = s.Queue[nextIndex].Id, CurrentSlideIndex = 0 }
            : s;
    });

    private void PreviousCore() => Mutate(s =>
    {
        if (s.CurrentItem is null)
            return s;

        if (s.CurrentSlideIndex > 0)
            return s with { CurrentSlideIndex = s.CurrentSlideIndex - 1 };

        var prevIndex = s.CurrentItemIndex - 1;
        if (prevIndex < 0)
            return s;

        var prev = s.Queue[prevIndex];
        return s with { CurrentItemId = prev.Id, CurrentSlideIndex = Math.Max(0, prev.Slides.Count - 1) };
    });

    // =====================================================================
    // Controlo do projetor
    // =====================================================================

    /// <summary>Marca a projeção como ativa. Se nada estiver selecionado, projeta o item preparado.</summary>
    public void StartProjection() => Mutate(s =>
    {
        var next = s with { IsProjecting = true };
        if (next.CurrentItem is null && s.Staged is { } staged)
        {
            next = next with
            {
                Queue = s.IndexOf(staged.Id) >= 0 ? s.Queue : [.. s.Queue, staged],
                CurrentItemId = staged.Id,
                CurrentSlideIndex = s.StagedSlideIndex
            };
        }
        return next;
    });

    public void StopProjection() => Mutate(s => s with { IsProjecting = false, IsFrozen = false, IsBlank = false });

    /// <summary>
    /// Freeze (Alt+F10): o projetor mantém o slide atual enquanto o operador navega livremente.
    /// Ao descongelar, o projetor salta para o slide selecionado nesse momento.
    /// </summary>
    public void ToggleFreeze() => Mutate(s => s with { IsFrozen = !s.IsFrozen });

    /// <summary>Blank Screen (Alt+F11): ecrã preto. Funciona mesmo com Freeze ativo.</summary>
    public void ToggleBlank() => Mutate(s => s with { IsBlank = !s.IsBlank });

    /// <summary>Botão "UPDATE": força o projetor a redesenhar (reajusta texto, recarrega fundo).</summary>
    public void Refresh() => Mutate(s => s, forceRefresh: true);

    /// <summary>Aparência da projeção (aplicada na hora; o texto é reajustado).</summary>
    public void SetStyle(ProjectionStyle style) => Mutate(s => s with { Style = style }, forceRefresh: true);

    /// <summary>Margem de segurança para TVs com overscan (0–10 % de cada lado).</summary>
    public void SetSafeArea(double percent) =>
        Mutate(s => s with { SafeAreaPercent = Math.Clamp(percent, 0, 10) }, forceRefresh: true);

    /// <summary>Fundo global (módulo Galeria). Itens com fundo próprio têm prioridade.</summary>
    public void SetGlobalBackground(BackgroundMedia? background) =>
        Mutate(s => s with { GlobalBackground = background });

    /// <summary>Fundo global do 1.º slide de cada item (null = usa o fundo normal).</summary>
    public void SetGlobalFirstSlideBackground(BackgroundMedia? background) =>
        Mutate(s => s with { GlobalFirstSlideBackground = background });

    // =====================================================================
    // Sobreposições (módulo Geral)
    // =====================================================================

    public void ShowClock(bool fullScreen = false) =>
        Mutate(s => s with { Overlay = OverlayMode.Clock, CountdownTarget = null, OverlayFullScreen = fullScreen });

    /// <summary>Cronómetro: regressivo se houver duração; progressivo (00:00 a subir) se for zero.</summary>
    public void StartCountdown(TimeSpan duration, bool fullScreen = false) => Mutate(s => duration > TimeSpan.Zero
        ? s with { Overlay = OverlayMode.Countdown, CountdownTarget = DateTimeOffset.Now.Add(duration), OverlayFullScreen = fullScreen }
        : s with { Overlay = OverlayMode.Stopwatch, CountdownTarget = DateTimeOffset.Now, OverlayFullScreen = fullScreen });

    /// <summary>Alterna entre ecrã inteiro e "Overlay" sem reiniciar a contagem.</summary>
    public void SetOverlayFullScreen(bool fullScreen) => Mutate(s => s with { OverlayFullScreen = fullScreen });

    public void SetGeneralBackground(BackgroundMedia? background) => Mutate(s => s with { GeneralBackground = background });

    public void ClearOverlay() => Mutate(s => s with { Overlay = OverlayMode.None, CountdownTarget = null });

    // =====================================================================
    // Atalhos de teclado (partilhados pelas duas janelas)
    // =====================================================================

    /// <summary>Executa a ação associada a um atalho vindo do JavaScript.</summary>
    public void ExecuteHotkey(string action)
    {
        switch (action)
        {
            case "freeze": ToggleFreeze(); break;
            case "blank": ToggleBlank(); break;
            case "next": Next(); break;
            case "previous": Previous(); break;
            default: logger.LogDebug("Atalho desconhecido: {Action}", action); break;
        }
    }

    // =====================================================================
    // Infraestrutura
    // =====================================================================

    /// <summary>
    /// Aplica uma alteração, recalcula o frame do projetor e notifica os subscritores.
    /// Se a função devolver a mesma instância, nada é notificado (evita renders inúteis).
    /// </summary>
    private void Mutate(Func<ProjectionState, ProjectionState> change, bool forceRefresh = false, bool keepScreen = false)
    {
        lock (_gate)
        {
            var current = _state;
            var next = change(current);
            if (ReferenceEquals(next, current) && !forceRefresh)
                return;

            next = next with { Live = keepScreen ? current.Live : ComputeLive(current.Live, next, forceRefresh) };
            Volatile.Write(ref _state, next);
        }

        RaiseStateChanged();
    }

    /// <summary>Calcula o que o projetor deve mostrar a partir do estado.</summary>
    private static LiveFrame ComputeLive(LiveFrame previous, ProjectionState s, bool forceRefresh)
    {
        LiveFrame candidate;

        if (s.IsFrozen)
        {
            // Congelado: conteúdo e fundo ficam; Blank e sobreposições continuam a funcionar.
            candidate = previous with
            {
                IsBlank = s.IsBlank, Overlay = s.Overlay, CountdownTarget = s.CountdownTarget,
                OverlayFullScreen = s.OverlayFullScreen, GeneralBackground = s.GeneralBackground
            };
        }
        else
        {
            candidate = new LiveFrame(
                Slide: s.IsProjecting ? s.CurrentSlide : null,
                Background: s.CurrentBackground,
                IsBlank: s.IsBlank,
                Overlay: s.Overlay,
                CountdownTarget: s.CountdownTarget,
                ContentVersion: previous.ContentVersion,
                OverlayFullScreen: s.OverlayFullScreen,
                GeneralBackground: s.GeneralBackground);
        }

        // ContentVersion só muda quando o slide muda (ou em UPDATE): a view usa-a
        // como @key para a transição de fade e para reajustar o tamanho do texto.
        var slideChanged = !Equals(candidate.Slide, previous.Slide);
        return slideChanged || forceRefresh
            ? candidate with { ContentVersion = previous.ContentVersion + 1 }
            : candidate;
    }

    /// <summary>Notifica cada subscritor isoladamente: um componente com erro não derruba os outros.</summary>
    private void RaiseStateChanged()
    {
        var handlers = OnStateChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro num subscritor de ProjectionService.OnStateChanged");
            }
        }
    }

    private static int Clamp(int index, int count) => count <= 0 ? 0 : Math.Clamp(index, 0, count - 1);
}
