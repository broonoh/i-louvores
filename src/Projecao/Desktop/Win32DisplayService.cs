using Photino.NET;
using Projecao.Models;
using Projecao.Services;

namespace Projecao.Desktop;

/// <summary>
/// Monitores via Win32 (user32). Equivalente Windows do <see cref="GtkDisplayService"/>.
/// Ao contrário do Linux, o Photino expõe <c>WindowHandle</c> diretamente, sem precisar
/// de procurar a janela pelo título.
/// </summary>
public sealed class Win32DisplayService(SettingsStore settings) : IDisplayService
{
    private PhotinoWindow? _panel;

    /// <summary>Chamado pelo DesktopHost depois de criar a janela do painel.</summary>
    internal void Attach(PhotinoWindow panel, string panelTitle)
    {
        _panel = panel;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        if (_panel is null)
            return [];

        IReadOnlyList<MonitorInfo> raw = [];
        _panel.Invoke(() => raw = Win32.QueryMonitors(_panel.WindowHandle));

        // Monitor do painel primeiro, depois da esquerda para a direita.
        return raw
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.X)
            .ThenBy(m => m.Y)
            .Select((m, i) => m with { FriendlyName = $"Monitor {i + 1} · {m.DeviceName}" })
            .ToList();
    }

    public MonitorPreference? PreferredMonitor
    {
        get => settings.PreferredMonitor;
        set => settings.PreferredMonitor = value;
    }
}
