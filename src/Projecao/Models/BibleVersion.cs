namespace Projecao.Models;

/// <summary>Versão/tradução da Bíblia (ex.: ARC — Almeida Revista e Corrigida).</summary>
public class BibleVersion
{
    public int Id { get; set; }

    /// <summary>Sigla única (ex.: "ARC", "NVI").</summary>
    public required string Abbreviation { get; set; }

    public required string Name { get; set; }

    public string Language { get; set; } = "pt";

    /// <summary>Crédito/licença a mostrar (obrigatório em versões Creative Commons, p. ex. Bíblia Livre).</summary>
    public string? Copyright { get; set; }

    public ICollection<BibleVerse> Verses { get; set; } = [];
}
