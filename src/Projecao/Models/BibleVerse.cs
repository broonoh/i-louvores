namespace Projecao.Models;

/// <summary>
/// Versículo de uma versão da Bíblia.
/// Chave natural única: (VersionId, BookId, Chapter, Verse).
/// </summary>
public class BibleVerse
{
    public int Id { get; set; }

    public int VersionId { get; set; }
    public BibleVersion? Version { get; set; }

    public int BookId { get; set; }
    public BibleBook? Book { get; set; }

    public int Chapter { get; set; }

    public int Verse { get; set; }

    public required string Text { get; set; }

    /// <summary>Texto sem acentos/pontuação, para a pesquisa por palavra (ver <see cref="Data.TextNormalizer.NormalizeForSearch"/>).</summary>
    public string NormalizedText { get; set; } = string.Empty;
}
