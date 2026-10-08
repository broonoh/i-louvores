using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Projecao.Desktop;

/// <summary>
/// Integração com o ambiente de trabalho Linux.
///
/// No Wayland o ícone da janela NÃO vem do programa: o compositor (KWin, Mutter…) procura
/// um ficheiro .desktop com o mesmo nome do app_id da janela. Sem ele aparece o ícone
/// genérico (o "W" amarelo no KDE). Por isso:
///   1. fixamos o app_id/classe GTK como "i-louvores" antes de criar as janelas;
///   2. garantimos ~/.local/share/applications/i-louvores.desktop e o ícone no tema hicolor
///      (o instalador já os cria; isto cobre execuções pelo IDE ou pelo "dotnet run").
/// </summary>
internal static class DesktopIntegration
{
    public const string AppId = "i-louvores";

    public static void Apply(ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
            return; // sem GTK/Wayland: o ícone da janela já vem do .exe/AppIcon, sem atalho a registar aqui

        SetProgramIdentity();
        try
        {
            EnsureDesktopEntry(logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Não foi possível registar o ícone no ambiente de trabalho");
        }
    }

    /// <summary>Processo do projetor: só a identidade (o registo já foi feito pelo painel).</summary>
    public static void SetIdentityOnly()
    {
        if (!OperatingSystem.IsWindows())
            SetProgramIdentity();
    }

    /// <summary>Tem de correr ANTES de o GTK ser inicializado (antes da 1.ª PhotinoWindow).</summary>
    private static void SetProgramIdentity()
    {
        try
        {
            g_set_prgname(AppId);
            gdk_set_program_class(AppId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sem GTK (ex.: testes) — nada a fazer.
        }
    }

    private static void EnsureDesktopEntry(ILogger? logger)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        var changed = false;

        // Ícone (o pacote instala vários tamanhos; aqui garantimos o de 256 px).
        // Atualiza também quando o ícone da app mudou numa versão nova.
        var iconSource = Path.Combine(AppContext.BaseDirectory, "i-louvores.png");
        var iconTarget = Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", $"{AppId}.png");
        if (File.Exists(iconSource) && (!File.Exists(iconTarget) || !SameContent(iconSource, iconTarget)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iconTarget)!);
            File.Copy(iconSource, iconTarget, overwrite: true);
            changed = true;
        }

        // .desktop — só se ainda não existir OU se for um atalho escrito por uma execução de
        // desenvolvimento (não pelo instalador) que ficou a apontar para um caminho antigo —
        // ex.: depois de mudar a framework-alvo (.NET 9 → 10), o atalho ficava preso à build
        // antiga, com o ícone congelado na versão de então. Um atalho do instalador (Exec dentro
        // de "i-louvores-app") nunca é tocado aqui.
        var desktopFile = Path.Combine(dataHome, "applications", $"{AppId}.desktop");
        var installedAppExe = Path.Combine(dataHome, "i-louvores-app", AppId);
        var currentExec = TryReadExec(desktopFile);
        var isDevEntry = currentExec is null || !currentExec.Equals(installedAppExe, StringComparison.Ordinal);

        if (isDevEntry && Environment.ProcessPath is { } exe)
        {
            // Via "dotnet i-louvores.dll" o ProcessPath é o dotnet: usa o apphost ao lado da dll.
            var apphost = Path.Combine(AppContext.BaseDirectory, AppId);
            var exec = File.Exists(apphost) ? apphost : exe;

            var desired = $"""
                [Desktop Entry]
                Type=Application
                Name=I-LOUVORES
                GenericName=Projeção de louvores e Bíblia
                Comment=Projeta louvores, passagens bíblicas, avisos e cronómetro num projetor ou TV
                Exec="{exec}"
                Icon={AppId}
                Terminal=false
                Categories=Office;Presentation;
                StartupWMClass={AppId}

                """;

            if (!File.Exists(desktopFile) || File.ReadAllText(desktopFile) != desired)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(desktopFile)!);
                File.WriteAllText(desktopFile, desired);
                changed = true;
            }
        }

        if (changed)
        {
            logger?.LogInformation("Ícone e atalho do I-LOUVORES registados em {Path}", dataHome);
            // Atualiza as caches do menu (KDE: kbuildsycoca; outros: update-desktop-database).
            // NÃO gerar icon-theme.cache na pasta do utilizador: sem index.theme fica incompleta
            // e esconde ícones que lá são adicionados depois.
            foreach (var (cmd, args) in new[] { ("kbuildsycoca6", "--noincremental"), ("kbuildsycoca5", "--noincremental"),
                         ("update-desktop-database", Path.GetDirectoryName(desktopFile)!) })
                TryRun(cmd, args);
        }
    }

    private static string? TryReadExec(string desktopFile)
    {
        if (!File.Exists(desktopFile))
            return null;

        return File.ReadLines(desktopFile)
            .FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal))
            ?["Exec=".Length..].Trim('"');
    }

    private static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static void TryRun(string command, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(command, arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            });
            p?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // comando não existe nesta distribuição
        }
    }

    [DllImport("libglib-2.0.so.0")] private static extern void g_set_prgname([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libgdk-3.so.0")] private static extern void gdk_set_program_class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
