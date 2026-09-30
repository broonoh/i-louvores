using Projecao.Desktop;

namespace Projecao;

public static class Program
{
    /// <summary>
    /// O mesmo executável tem dois papéis:
    ///   Projecao               → painel de controlo (servidor + janela principal)
    ///   Projecao --projetor …  → janela do projetor (lançada pelo painel)
    /// Main é síncrono de propósito: o GTK tem de correr na thread principal.
    /// </summary>
    public static int Main(string[] args)
    {
        var isProjector = args.Contains(ProjectorHost.Flag);

        // Reinício pedido pela importação de dados: espera a instância anterior fechar
        // (para a base poder ser trocada com segurança).
        var waitIndex = Array.IndexOf(args, "--aguardar-pid");
        if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); }
            catch (ArgumentException) { /* já terminou */ }
        }

        // Identidade da janela (app_id "i-louvores") antes de o GTK arrancar → ícone certo no Wayland.
        if (!isProjector)
            DesktopIntegration.Apply();
        else
            DesktopIntegration.SetIdentityOnly();

        return isProjector ? ProjectorHost.Run(args) : DesktopHost.Run(args);
    }
}
