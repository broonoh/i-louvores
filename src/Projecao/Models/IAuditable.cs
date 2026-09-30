namespace Projecao.Models;

/// <summary>
/// Entidades com carimbo de data/hora preenchido automaticamente pelo <c>AppDbContext</c>.
/// Todas as datas são gravadas em UTC.
/// </summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}
