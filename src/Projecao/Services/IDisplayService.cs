using Projecao.Models;

namespace Projecao.Services;

/// <summary>Enumeração de monitores (implementação nativa em Desktop/GtkDisplayService).</summary>
public interface IDisplayService
{
    /// <summary>Lista os monitores ligados neste momento. O do painel vem primeiro.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Último monitor escolhido pelo operador (persistido entre sessões).</summary>
    MonitorPreference? PreferredMonitor { get; set; }
}

/// <summary>
/// Preferência gravada. Guardamos também o modelo (DeviceName) porque o Id inclui a
/// posição, que muda se o operador reorganizar os ecrãs nas definições do sistema.
/// </summary>
public sealed record MonitorPreference(string Id, string DeviceName);

public static class MonitorSelection
{
    /// <summary>
    /// Escolhe o monitor por omissão: o preferido (se ainda ligado), senão o primeiro
    /// monitor sem o painel, senão o do painel.
    /// </summary>
    public static MonitorInfo? PickDefault(IReadOnlyList<MonitorInfo> monitors, MonitorPreference? preferred)
    {
        if (monitors.Count == 0)
            return null;

        if (preferred is not null)
        {
            var match = monitors.FirstOrDefault(m => m.Id == preferred.Id)
                        ?? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, preferred.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors[0];
    }
}
