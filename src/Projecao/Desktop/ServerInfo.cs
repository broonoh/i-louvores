namespace Projecao.Desktop;

/// <summary>Endereço real do servidor local (porta escolhida pelo SO no arranque).</summary>
public sealed class ServerInfo
{
    public string BaseUrl { get; internal set; } = string.Empty;
}
