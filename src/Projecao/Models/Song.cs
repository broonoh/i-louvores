using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

namespace Projecao.Models;

/// <summary>
/// Louvor/hino. A letra é guardada num único campo de texto, com as estrofes
/// separadas por uma linha em branco — cada estrofe vira um slide.
/// </summary>
public partial class Song : IAuditable
{
    public int Id { get; set; }

    /// <summary>
    /// Coletânea/hinário a que o louvor pertence (ex.: "Coletânea 2018", "Coletânea CIAS 2018",
    /// "Avulsos") — equivalente aos ficheiros PT_*.xbY de outros programas de projeção.
    /// </summary>
    public string? Collection { get; set; }

    /// <summary>Número no hinário principal ("Nº 1").</summary>
    public int? Number { get; set; }

    /// <summary>Número alternativo, p. ex. noutro hinário ("Nº 2").</summary>
    public int? AltNumber { get; set; }

    public required string Title { get; set; }

    public string? Author { get; set; }

    public string? Category { get; set; }

    /// <summary>Tonalidade (ex.: "G", "Am").</summary>
    public string? MusicalKey { get; set; }

    /// <summary>Ritmo/andamento (ex.: "Valsa", "4/4 - 72 bpm").</summary>
    public string? Rhythm { get; set; }

    /// <summary>Letra completa. Estrofes separadas por linha em branco.</summary>
    public string Lyrics { get; set; } = string.Empty;

    /// <summary>Fundo próprio do louvor, relativo à pasta da Galeria (opcional).</summary>
    public string? BackgroundFile { get; set; }

    /// <summary>Fundo só do 1.º slide (ex.: imagem com a faixa "LOUVOR"), relativo à Galeria.</summary>
    public string? FirstSlideBackgroundFile { get; set; }

    // --- Colunas de pesquisa (preenchidas automaticamente no SaveChanges) ---
    // Texto em minúsculas e sem acentos: o LIKE do SQLite só ignora maiúsculas
    // em ASCII e nunca ignora acentos, por isso normalizamos na escrita.

    public string NormalizedTitle { get; set; } = string.Empty;

    public string NormalizedLyrics { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Divide a letra em estrofes (um slide por estrofe).</summary>
    public IReadOnlyList<string> GetStanzas()
    {
        if (string.IsNullOrWhiteSpace(Lyrics))
            return [];

        var text = Lyrics.Replace("\r\n", "\n").Trim();
        return BlankLineRegex().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    [NotMapped]
    public string DisplayNumber => Number?.ToString() ?? "-";

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLineRegex();
}
