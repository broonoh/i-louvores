using System.Diagnostics;

namespace Projecao.Services;

/// <summary>Fontes instaladas no sistema (fontconfig: <c>fc-list</c>), para a aba Configuração.</summary>
public sealed class FontService
{
    private IReadOnlyList<string>? _cache;

    /// <summary>Fontes sugeridas primeiro (as do Glorifica e equivalentes livres), depois as instaladas.</summary>
    public static readonly string[] Suggested =
        ["Franklin Gothic Medium", "Libre Franklin", "Arial", "Liberation Sans", "Noto Sans", "Calibri", "Carlito",
         "Verdana", "DejaVu Sans", "Open Sans", "Roboto", "Montserrat", "Georgia", "Times New Roman", "Noto Serif", "Impact"];

    public IReadOnlyList<string> GetInstalled()
    {
        if (_cache is not null) return _cache;
        var fonts = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("fc-list", ": family") { RedirectStandardOutput = true, UseShellExecute = false });
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (var line in output.Split('\n'))
                    foreach (var family in line.Split(','))
                        if (family.Trim() is { Length: > 1 } f && !f.StartsWith('.'))
                            fonts.Add(f);
            }
        }
        catch (System.ComponentModel.Win32Exception) { /* sem fontconfig */ }
        return _cache = fonts.ToList();
    }

    public bool IsInstalled(string family) => GetInstalled().Contains(family, StringComparer.CurrentCultureIgnoreCase);
}
