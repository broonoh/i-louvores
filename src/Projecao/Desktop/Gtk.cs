using System.Runtime.InteropServices;
using Projecao.Models;

namespace Projecao.Desktop;

/// <summary>
/// Interop mínimo com GTK3/GDK3 (as mesmas bibliotecas que o Photino carrega).
/// TODAS as funções têm de ser chamadas na thread de UI do GTK
/// (dentro de <c>PhotinoWindow.Invoke</c> ou de um handler da janela).
/// </summary>
internal static class Gtk
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGdk = "libgdk-3.so.0";
    private const string LibGlib = "libglib-2.0.so.0";

    /// <summary>
    /// Procura a GtkWindow deste processo pelo título. O Photino não expõe o handle
    /// nativo no Linux (<c>WindowHandle</c> só existe no Windows).
    /// </summary>
    public static IntPtr FindWindowByTitle(string title)
    {
        var list = gtk_window_list_toplevels();
        try
        {
            for (var node = list; node != IntPtr.Zero;)
            {
                var item = Marshal.PtrToStructure<GList>(node);
                if (Marshal.PtrToStringUTF8(gtk_window_get_title(item.Data)) == title)
                    return item.Data;
                node = item.Next;
            }
        }
        finally
        {
            g_list_free(list);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Ecrã inteiro num monitor específico. Funciona em Wayland e X11: o compositor
    /// (KWin, Mutter…) recebe o pedido "fullscreen neste output". No Wayland as janelas
    /// não podem posicionar-se sozinhas, por isso mover para (x,y) não serve.
    /// </summary>
    public static bool FullscreenOnMonitorAt(IntPtr window, int x, int y)
    {
        if (window == IntPtr.Zero)
            return false;

        var display = gdk_display_get_default();
        var count = gdk_display_get_n_monitors(display);
        for (var i = 0; i < count; i++)
        {
            gdk_monitor_get_geometry(gdk_display_get_monitor(display, i), out var g);
            if (x >= g.X && x < g.X + g.Width && y >= g.Y && y < g.Y + g.Height)
            {
                gtk_window_fullscreen_on_monitor(window, gtk_window_get_screen(window), i);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Lista os monitores. <paramref name="panelWindow"/> (opcional) marca como "principal"
    /// o monitor onde está o painel — no Wayland o GDK3 não sabe qual é o primário.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> QueryMonitors(IntPtr panelWindow)
    {
        var display = gdk_display_get_default();
        if (display == IntPtr.Zero)
            return [];

        var panelMonitor = IntPtr.Zero;
        if (panelWindow != IntPtr.Zero && gtk_widget_get_window(panelWindow) is var gdkWindow && gdkWindow != IntPtr.Zero)
            panelMonitor = gdk_display_get_monitor_at_window(display, gdkWindow);

        var count = gdk_display_get_n_monitors(display);
        var result = new List<MonitorInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var monitor = gdk_display_get_monitor(display, i);
            gdk_monitor_get_geometry(monitor, out var g);
            var model = Marshal.PtrToStringUTF8(gdk_monitor_get_model(monitor));
            var scale = Math.Max(1, gdk_monitor_get_scale_factor(monitor));
            var isPanel = panelMonitor != IntPtr.Zero ? monitor == panelMonitor : gdk_monitor_is_primary(monitor);
            var device = string.IsNullOrWhiteSpace(model) ? $"monitor-{i}" : model;

            result.Add(new MonitorInfo(
                Id: $"{device}@{g.X},{g.Y}",
                DeviceName: device,
                FriendlyName: string.Empty,
                X: g.X,
                Y: g.Y,
                Width: g.Width * scale,      // geometria é lógica; resolução real = × escala
                Height: g.Height * scale,
                RefreshRate: (int)Math.Round(gdk_monitor_get_refresh_rate(monitor) / 1000.0), // mHz → Hz
                IsPrimary: isPanel));
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GList { public IntPtr Data, Next, Prev; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdkRectangle { public int X, Y, Width, Height; }

    [DllImport(LibGtk)] private static extern IntPtr gtk_window_list_toplevels();
    [DllImport(LibGtk)] private static extern IntPtr gtk_window_get_title(IntPtr window);
    [DllImport(LibGtk)] private static extern IntPtr gtk_window_get_screen(IntPtr window);
    [DllImport(LibGtk)] private static extern void gtk_window_fullscreen_on_monitor(IntPtr window, IntPtr screen, int monitor);
    [DllImport(LibGtk)] private static extern IntPtr gtk_widget_get_window(IntPtr widget);
    [DllImport(LibGlib)] private static extern void g_list_free(IntPtr list);
    [DllImport(LibGdk)] private static extern IntPtr gdk_display_get_default();
    [DllImport(LibGdk)] private static extern int gdk_display_get_n_monitors(IntPtr display);
    [DllImport(LibGdk)] private static extern IntPtr gdk_display_get_monitor(IntPtr display, int index);
    [DllImport(LibGdk)] private static extern IntPtr gdk_display_get_monitor_at_window(IntPtr display, IntPtr gdkWindow);
    [DllImport(LibGdk)] private static extern void gdk_monitor_get_geometry(IntPtr monitor, out GdkRectangle rect);
    [DllImport(LibGdk)] private static extern IntPtr gdk_monitor_get_model(IntPtr monitor);
    [DllImport(LibGdk)] private static extern int gdk_monitor_get_refresh_rate(IntPtr monitor);
    [DllImport(LibGdk)] private static extern int gdk_monitor_get_scale_factor(IntPtr monitor);
    [DllImport(LibGdk)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool gdk_monitor_is_primary(IntPtr monitor);
}
