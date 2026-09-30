namespace Projecao.Models;

public enum TextAlignment
{
    Left,
    Center,
    Right,
    Justify
}

/// <summary>
/// Aviso/anúncio com formatação própria. Com <see cref="IsTemplate"/> = true
/// funciona como modelo reutilizável (botões "Carregar/Guardar modelo").
/// </summary>
public class Notice : IAuditable
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public string Content { get; set; } = string.Empty;

    public string FontFamily { get; set; } = "Noto Sans";

    /// <summary>Tamanho máximo da fonte em px (o texto encolhe se não couber).</summary>
    public int FontSize { get; set; } = 72;

    /// <summary>Cor do texto em hexadecimal (#RRGGBB).</summary>
    public string ForeColor { get; set; } = "#FFFFFF";

    /// <summary>Cor sólida de fundo; null = usa o fundo ativo da Galeria.</summary>
    public string? BackColor { get; set; }

    public TextAlignment TextAlign { get; set; } = TextAlignment.Center;

    public bool IsTemplate { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
