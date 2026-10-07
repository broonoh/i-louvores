using System.Globalization;
using Projecao.Models.Projection;

namespace Projecao.Services;

/// <summary>
/// Converte o <see cref="ProjectionStyle"/> em variáveis CSS (usadas pelo telão e pelas pré-visualizações).
/// Cores e fontes passam por <see cref="CssSafe"/> — valores inválidos caem no padrão.
/// </summary>
public static class ProjectionCss
{
    private const string SongFallback = "'Franklin Gothic Medium', 'Libre Franklin', 'Noto Sans', sans-serif";
    private const string BibleFallback = "Arial, 'Liberation Sans', 'Noto Sans', sans-serif";

    public static string Vars(ProjectionStyle s)
    {
        var d = ProjectionStyle.Default;
        string C(string? v, string def) => CssSafe.Color(v) ?? def;
        string F(string? v, string fallback) => CssSafe.FontFamily(v) is { } f ? $"{f}, {fallback}" : fallback;
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        return string.Join(';',
            $"--song-font:{F(s.SongFont, SongFallback)}",
            $"--song-color:{C(s.SongColor, d.SongColor)}",
            $"--song-chorus:{C(s.ChorusColor, d.ChorusColor)}",
            $"--song-paren:{C(s.ParenColor, d.ParenColor)}",
            $"--song-men:{C(s.MenColor, d.MenColor)}",
            $"--song-women:{C(s.WomenColor, d.WomenColor)}",
            $"--song-title-color:{C(s.SongTitleColor, d.SongTitleColor)}",
            $"--song-title-font:{F(s.SongTitleFont, SongFallback)}",
            $"--song-title-align:{AlignKeyword(s.SongTitleAlign)}",
            $"--song-title-top:{N(Math.Clamp(s.SongTitleTopPct, 0, 60))}%",
            $"--song-weight:{(s.SongBold ? 600 : 400)}",
            $"--song-transform:{(s.SongUppercase ? "uppercase" : "none")}",
            $"--song-align:{AlignKeyword(s.SongAlign)}",
            $"--song-line-height:{N(Math.Clamp(s.SongLineHeight, 1, 2))}",
            $"--bible-font:{F(s.BibleFont, BibleFallback)}",
            $"--bible-color:{C(s.BibleColor, d.BibleColor)}",
            $"--bible-ref-color:{C(s.BibleRefColor, d.BibleRefColor)}",
            $"--bible-ref-font:{F(s.BibleRefFont, BibleFallback)}",
            $"--bible-ref-align:{AlignKeyword(s.BibleRefAlign)}",
            $"--bible-ref-top:{N(Math.Clamp(s.BibleRefTopPct, 0, 60))}%",
            $"--bible-align:{AlignKeyword(s.BibleAlign)}",
            $"--bible-line-height:{N(Math.Clamp(s.BibleLineHeight, 1, 2))}",
            $"--clock-color:{C(s.ClockColor, d.ClockColor)}",
            $"--song-max:{N(Math.Clamp(s.SongMaxSizePct, 3, 20))}",
            $"--bible-max:{N(Math.Clamp(s.BibleMaxSizePct, 3, 20))}",
            $"--song-title-max:{N(Math.Clamp(s.SongTitleMaxSizePct, 3, 15))}",
            $"--bible-ref-max:{N(Math.Clamp(s.BibleRefMaxSizePct, 3, 15))}");
    }

    private static string AlignKeyword(string? align) => align switch
    {
        "center" => "center",
        "right" => "right",
        _ => "left"
    };

    /// <summary>Tamanho máximo (em % da altura do ecrã) para o ajuste automático do texto deste slide.</summary>
    public static double MaxSizePct(ProjectionStyle s, ProjectionSlide slide) =>
        Math.Clamp(slide.Layout == SlideLayout.Bible ? s.BibleMaxSizePct : slide.Markup ? s.SongMaxSizePct : 11, 3, 20);

    /// <summary>Tamanho máximo da faixa (título do louvor / referência bíblica) deste slide.</summary>
    public static double HeadingMaxSizePct(ProjectionStyle s, ProjectionSlide slide) => slide.Layout switch
    {
        SlideLayout.Bible => Math.Clamp(s.BibleRefMaxSizePct, 3, 15),
        SlideLayout.SongFirst => Math.Clamp(s.SongTitleMaxSizePct, 3, 15),
        _ => 6
    };
}
