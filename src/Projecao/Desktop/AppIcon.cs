using Photino.NET;

namespace Projecao.Desktop;

/// <summary>Ícone da janela (X11). No Wayland o ícone vem do .desktop instalado (app_id = i-louvores).</summary>
internal static class AppIcon
{
    public static void Apply(PhotinoWindow window)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "i-louvores.png");
        if (File.Exists(path)) // nunca impedir o arranque por falta do ícone
            window.SetIconFile(path);
    }
}
