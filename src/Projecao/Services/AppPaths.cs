namespace Projecao.Services;

/// <summary>
/// Caminhos da aplicação (Linux):
/// - Galeria em ~/Documentos/I-LOUVORES/Galeria (o utilizador copia lá imagens/vídeos).
/// - Base de dados e configuração em ~/.local/share/i-louvores: NUNCA em Documentos,
///   porque ferramentas de sincronização (Nextcloud, Drive…) podem corromper
///   ficheiros SQLite abertos, sobretudo em modo WAL.
/// Versões ≤ 0.3 do I-LOUVORES usavam pastas "Glorifica" (nome antigo); são migradas automaticamente no arranque.
/// </summary>
public sealed class AppPaths
{
    public AppPaths()
        : this(
            Path.Combine(DocumentsFolder(), "I-LOUVORES"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-louvores"),
            legacyDocumentsRoot: Path.Combine(DocumentsFolder(), "Glorifica"),
            legacyDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glorifica"))
    {
    }

    /// <summary>
    /// SpecialFolder.MyDocuments pode devolver "" num utilizador sem sessão gráfica ainda iniciada
    /// (falta o ~/.config/user-dirs.dirs criado pelo ambiente de trabalho no 1.º login) — nesse caso
    /// o caminho ficaria relativo e derrubava o servidor local. A reserva é ~ (a própria pasta pessoal),
    /// igual ao que "xdg-user-dir DOCUMENTS" devolve sem configuração (usado pelo install.sh):
    /// as duas ficam de acordo sobre onde procurar a Galeria.
    /// </summary>
    private static string DocumentsFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs))
            return docs;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? "." : home;
    }

    /// <summary>Pastas explícitas (testes, instalação portátil em pen USB…).</summary>
    public AppPaths(string documentsRoot, string dataFolder, string? legacyDocumentsRoot = null, string? legacyDataFolder = null)
    {
        DocumentsRoot = documentsRoot;
        GalleryFolder = Path.Combine(DocumentsRoot, "Galeria");
        DataFolder = dataFolder;
        DatabasePath = Path.Combine(DataFolder, "i-louvores.db");

        MigrateLegacy(legacyDocumentsRoot, legacyDataFolder);

        Directory.CreateDirectory(GalleryFolder);
        Directory.CreateDirectory(DataFolder);
    }

    /// <summary>
    /// Move as pastas do nome antigo ("Glorifica") para as novas, só se as novas ainda não existirem
    /// e se as antigas forem mesmo do I-LOUVORES (sem o Config.ini de outro programa com o mesmo nome de pasta).
    /// </summary>
    private void MigrateLegacy(string? legacyDocs, string? legacyData)
    {
        if (legacyDocs is not null && !Directory.Exists(DocumentsRoot) && Directory.Exists(Path.Combine(legacyDocs, "Galeria"))
            && !File.Exists(Path.Combine(legacyDocs, "Config.ini")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DocumentsRoot)!);
            Directory.Move(legacyDocs, DocumentsRoot);
        }

        var legacyDb = legacyData is null ? null : Path.Combine(legacyData, "glorifica.db");
        if (legacyDb is not null && !Directory.Exists(DataFolder) && File.Exists(legacyDb))
        {
            Directory.Move(legacyData!, DataFolder);
            // A base e os ficheiros WAL/SHM mudam de nome juntos (a app ainda não abriu a base).
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var from = Path.Combine(DataFolder, "glorifica.db" + suffix);
                if (File.Exists(from))
                    File.Move(from, DatabasePath + suffix);
            }
        }
    }

    public string DocumentsRoot { get; }

    public string GalleryFolder { get; }

    public string DataFolder { get; }

    public string DatabasePath { get; }
}
