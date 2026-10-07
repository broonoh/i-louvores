using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Projecao.Services;

/// <summary>Fontes instaladas no sistema (Linux: fontconfig via <c>fc-list</c>; Windows: registo), para a aba Configuração.</summary>
public sealed class FontService
{
    private IReadOnlyList<string>? _cache;

    /// <summary>Fontes sugeridas primeiro (as usadas pelo I-LOUVORES e equivalentes livres), depois as instaladas.</summary>
    public static readonly string[] Suggested =
        ["Franklin Gothic Medium", "Libre Franklin", "Arial", "Liberation Sans", "Noto Sans", "Calibri", "Carlito",
         "Verdana", "DejaVu Sans", "Open Sans", "Roboto", "Montserrat", "Georgia", "Times New Roman", "Noto Serif", "Impact"];

    public IReadOnlyList<string> GetInstalled()
    {
        if (_cache is not null) return _cache;
        var fonts = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (OperatingSystem.IsWindows())
            AddFromWindowsRegistry(fonts);
        else
            AddFromFontconfig(fonts);
        return _cache = fonts.ToList();
    }

    private static void AddFromFontconfig(SortedSet<string> fonts)
    {
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
    }

    /// <summary>Nomes de valor em "...\Fonts" são "Arial (TrueType)", "Arial Bold (TrueType)"… — fica só a família base.</summary>
    [SupportedOSPlatform("windows")]
    private static void AddFromWindowsRegistry(SortedSet<string> fonts)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                var family = name.Replace(" (TrueType)", "").Replace(" (OpenType)", "");
                foreach (var suffix in new[] { " Bold", " Italic", " Regular", " Light", " Medium", " Semibold" })
                    family = family.Replace(suffix, "");
                if (family.Trim() is { Length: > 1 } f)
                    fonts.Add(f);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { /* sem acesso ao registo */ }
    }

    public bool IsInstalled(string family) => GetInstalled().Contains(family, StringComparer.CurrentCultureIgnoreCase);
}
