namespace Projecao.Models;

public enum Testament
{
    Old,
    New
}

/// <summary>
/// Livro bíblico. O Id é a ordem canónica (1 = Gênesis … 66 = Apocalipse) e é
/// partilhado por todas as versões, o que simplifica a navegação em cascata.
/// </summary>
public class BibleBook
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Abbreviation { get; set; }

    public Testament Testament { get; set; }

    public int ChapterCount { get; set; }
}
