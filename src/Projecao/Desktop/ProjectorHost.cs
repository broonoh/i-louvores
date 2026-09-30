using System.Globalization;
using Photino.NET;

namespace Projecao.Desktop;

/// <summary>
/// Processo da janela do projetor: sem bordas, ecrã inteiro no monitor pedido,
/// a mostrar a página /projetor do servidor local.
///
/// Recebe comandos do painel pelo stdin (uma linha por comando):
///   monitor X Y   → passa para o monitor que contém o ponto (X,Y)
///   close         → fecha
/// Se o stdin fechar (o painel terminou ou falhou), fecha-se sozinho: nunca fica
/// um projetor "órfão" no telão.
/// </summary>
internal static class ProjectorHost
{
    public const string Flag = "--projetor";
    public const string WindowTitle = "I-LOUVORES - Projetor";

    public static int Run(string[] args)
    {
        var url = Arg(args, "--url") ?? throw new ArgumentException("--url em falta");
        var x = int.Parse(Arg(args, "--x") ?? "0", CultureInfo.InvariantCulture);
        var y = int.Parse(Arg(args, "--y") ?? "0", CultureInfo.InvariantCulture);

        var window = new PhotinoWindow()
            .SetLogVerbosity(0)
            .SetTitle(WindowTitle)
            .SetChromeless(true)
            .SetMediaAutoplayEnabled(true)     // vídeos de fundo em loop
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(false)
            .SetSize(1280, 720)
            .Load(url);

        AppIcon.Apply(window);

        window.RegisterWindowCreatedHandler((_, _) =>
        {
            Gtk.FullscreenOnMonitorAt(Gtk.FindWindowByTitle(WindowTitle), x, y);
            StartCommandReader(window);
        });

        window.WaitForClose();
        return 0;
    }

    private static void StartCommandReader(PhotinoWindow window)
    {
        var reader = new Thread(() =>
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["monitor", var xs, var ys]
                    && int.TryParse(xs, CultureInfo.InvariantCulture, out var nx)
                    && int.TryParse(ys, CultureInfo.InvariantCulture, out var ny))
                {
                    window.Invoke(() => Gtk.FullscreenOnMonitorAt(Gtk.FindWindowByTitle(WindowTitle), nx, ny));
                }
                else if (parts is ["close"])
                {
                    break;
                }
            }

            // "close" ou EOF (painel morreu) → fechar.
            window.Invoke(window.Close);
        })
        {
            IsBackground = true,
            Name = "projector-stdin"
        };
        reader.Start();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
