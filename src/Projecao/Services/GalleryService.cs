using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Projecao.Services;

/// <summary>Papel de um fundo escolhido na Galeria (como no Glorifica).</summary>
public enum BackgroundSlot
{
    /// <summary>Fundo de todos os slides ("Louvor (Resto)").</summary>
    Slides,

    /// <summary>Fundo só do 1.º slide de cada item ("Louvor (Primeiro Slide)", com a faixa).</summary>
    FirstSlide,

    /// <summary>Fundo das passagens bíblicas (ex.: "FUNDO_BIBLIA", com a faixa da referência).</summary>
    Bible,

    /// <summary>Fundo do relógio/cronómetro em ecrã inteiro (módulo Geral).</summary>
    General
}

/// <summary>
/// Liga a Galeria à projeção: aplica os fundos escolhidos, guarda-os entre sessões
/// e reage a mudanças na pasta (ficheiro substituído → recarrega; apagado → some do telão).
/// </summary>
public sealed class GalleryService(
    MediaLibraryService media,
    ProjectionService projection,
    SettingsStore settings,
    ILogger<GalleryService> logger) : IDisposable
{
    private bool _initialized;

    /// <summary>Restaura os fundos da última sessão e começa a vigiar a pasta.</summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        Reapply();
        media.Changed += Reapply;
    }

    public string? GetSelection(BackgroundSlot slot) => slot switch
    {
        BackgroundSlot.Slides => settings.GalleryBackground,
        BackgroundSlot.FirstSlide => settings.GalleryFirstSlideBackground,
        BackgroundSlot.Bible => settings.GalleryBibleBackground,
        _ => settings.GalleryGeneralBackground
    };

    /// <summary>Fundo resolvido de um papel (null se nenhum ou se o ficheiro não existir).</summary>
    public Projecao.Models.Projection.BackgroundMedia? GetBackground(BackgroundSlot slot) => media.Resolve(GetSelection(slot));

    /// <summary>Escolhe (ou limpa, com null) o fundo de um papel e aplica-o já no projetor.</summary>
    public void Select(BackgroundSlot slot, string? relativePath)
    {
        if (relativePath is not null && media.Resolve(relativePath) is null)
            throw new FileNotFoundException("O ficheiro já não existe na Galeria.", relativePath);

        switch (slot)
        {
            case BackgroundSlot.Slides: settings.GalleryBackground = relativePath; break;
            case BackgroundSlot.FirstSlide: settings.GalleryFirstSlideBackground = relativePath; break;
            case BackgroundSlot.Bible: settings.GalleryBibleBackground = relativePath; break;
            default: settings.GalleryGeneralBackground = relativePath; break;
        }

        Reapply();
    }

    /// <summary>
    /// Volta a resolver os fundos guardados. Mantém a escolha mesmo que o ficheiro
    /// desapareça (ex.: pen USB retirada): quando voltar, reaparece sozinho.
    /// </summary>
    public void Reapply()
    {
        projection.SetGlobalBackground(media.Resolve(settings.GalleryBackground));
        projection.SetGlobalFirstSlideBackground(media.Resolve(settings.GalleryFirstSlideBackground));
        projection.SetGeneralBackground(media.Resolve(settings.GalleryGeneralBackground));
    }

    /// <summary>Abre a pasta da Galeria no gestor de ficheiros do sistema.</summary>
    public bool OpenFolder()
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(media.GalleryFolder);
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível abrir {Folder}", media.GalleryFolder);
            return false;
        }
    }

    public void Dispose() => media.Changed -= Reapply;
}
