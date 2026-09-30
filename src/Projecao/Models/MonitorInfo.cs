namespace Projecao.Models;

/// <summary>
/// Monitor ligado ao computador. X/Y na disposição lógica do ambiente de trabalho;
/// Width/Height em pixels reais.
/// </summary>
/// <param name="Id">Identificador estável na sessão (modelo + posição, ex.: <c>LF24T35@0,0</c>).</param>
/// <param name="DeviceName">Modelo informado pelo monitor (EDID), ex.: <c>LC24RG50</c>.</param>
/// <param name="IsPrimary">Monitor onde está o painel de controlo.</param>
public sealed record MonitorInfo(
    string Id,
    string DeviceName,
    string FriendlyName,
    int X,
    int Y,
    int Width,
    int Height,
    int RefreshRate,
    bool IsPrimary)
{
    /// <summary>Texto do seletor, ex.: "Monitor 2 · LF24T35 — 1920x1080 @ 75Hz".</summary>
    public string Description =>
        $"{FriendlyName} — {Width}x{Height}" +
        (RefreshRate > 0 ? $" @ {RefreshRate}Hz" : string.Empty) +
        (IsPrimary ? " (painel)" : string.Empty);
}
