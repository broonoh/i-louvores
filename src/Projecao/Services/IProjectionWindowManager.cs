using Projecao.Models;

namespace Projecao.Services;

/// <summary>Abre, posiciona e fecha a janela de projeção (implementação em Desktop/ProjectorProcessManager).</summary>
public interface IProjectionWindowManager
{
    bool IsOpen { get; }

    MonitorInfo? CurrentMonitor { get; }

    /// <summary>
    /// Abre a janela (se ainda não estiver aberta) e coloca-a em ecrã inteiro no monitor.
    /// Chamar novamente com outro monitor move a janela.
    /// </summary>
    Task OpenAsync(MonitorInfo monitor);

    Task CloseAsync();
}
