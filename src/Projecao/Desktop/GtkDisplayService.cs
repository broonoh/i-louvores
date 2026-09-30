using Photino.NET;
using Projecao.Models;
using Projecao.Services;

namespace Projecao.Desktop;

/// <summary>
/// Monitores via GDK. As consultas são feitas na thread de UI do GTK através da
/// janela do painel (<see cref="PhotinoWindow.Invoke"/>), porque os componentes
/// Blazor correm noutras threads.
/// </summary>
public sealed class GtkDisplayService(SettingsStore settings) : IDisplayService
{
    private PhotinoWindow? _panel;
    private string _panelTitle = string.Empty;

    /// <summary>Chamado pelo DesktopHost depois de criar a janela do painel.</summary>
    internal void Attach(PhotinoWindow panel, string panelTitle)
    {
        _panel = panel;
        _panelTitle = panelTitle;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        if (_panel is null)
            return [];

        IReadOnlyList<MonitorInfo> raw = [];
        _panel.Invoke(() => raw = Gtk.QueryMonitors(Gtk.FindWindowByTitle(_panelTitle)));

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
