using Microsoft.Extensions.Logging;
using Projecao.Models.Projection;

namespace Projecao.Services;

/// <summary>Ficheiro de mídia da Galeria.</summary>
public sealed record MediaFile(string Name, string RelativePath, string Url, bool IsVideo, long SizeBytes, DateTime ModifiedUtc)
{
    public BackgroundMedia ToBackground() => new(Url, IsVideo, RelativePath);
}

/// <summary>
/// Acesso à pasta Documentos/I-LOUVORES/Galeria.
///
/// A página não pode abrir caminhos locais (file://). O servidor local publica a
/// pasta em <see cref="UrlPrefix"/> (ver DesktopHost), e aqui convertemos
/// caminhos em URLs relativos a esse prefixo.
///
/// A pasta é vigiada: quando alguém copia/apaga ficheiros pelo gestor de ficheiros,
/// <see cref="Changed"/> dispara (agrupado em 400 ms) e a Galeria atualiza sozinha.
/// </summary>
public sealed class MediaLibraryService : IDisposable
{
    public const string UrlPrefix = "/galeria";

    /// <summary>Limite por ficheiro importado (vídeos de fundo raramente passam disto).</summary>
    public const long MaxImportBytes = 1L << 30; // 1 GiB

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    // WebKitGTK usa GStreamer: WebM (VP8/VP9) funciona sempre; MP4/H.264 precisa do
    // gstreamer1-plugin-openh264 (ou dos codecs do RPM Fusion).
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".webm" };

    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private readonly ILogger<MediaLibraryService>? _logger;

    public MediaLibraryService(AppPaths paths, ILogger<MediaLibraryService>? logger = null)
    {
        _logger = logger;
        GalleryFolder = paths.GalleryFolder;
        Directory.CreateDirectory(GalleryFolder);

        _debounce = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            _watcher = new FileSystemWatcher(GalleryFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Ex.: limite de inotify esgotado. A Galeria continua a funcionar com o botão ⟳.
            _logger?.LogWarning(ex, "Sem vigilância automática da Galeria");
            _watcher = null;
        }
    }

    /// <summary>Conteúdo da pasta mudou (ficheiros adicionados, apagados ou substituídos).</summary>
    public event Action? Changed;

    public string GalleryFolder { get; }

    /// <summary>Lista imagens e vídeos da Galeria (inclui subpastas), por nome.</summary>
    public IReadOnlyList<MediaFile> List()
    {
        if (!Directory.Exists(GalleryFolder))
            return [];

        return Directory.EnumerateFiles(GalleryFolder, "*", SearchOption.AllDirectories)
            .Where(IsSupported)
            .Where(f => !Path.GetFileName(f).StartsWith('.')) // ocultos e temporários de importação
            .Select(full => ToMediaFile(full))
            .OrderBy(f => f.RelativePath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Converte um caminho relativo guardado (ex.: Song.BackgroundFile) num fundo.
    /// Devolve null se o ficheiro não existir ou se o caminho tentar sair da Galeria.
    /// </summary>
    public BackgroundMedia? Resolve(string? relativePath) =>
        TryGetFullPath(relativePath) is { } full && File.Exists(full) && IsSupported(full)
            ? ToMediaFile(full).ToBackground()
            : null;

    /// <summary>
    /// Copia um ficheiro para a Galeria. O nome é higienizado e, se já existir,
    /// recebe um sufixo " (2)", " (3)"… Escreve primeiro num ficheiro oculto e só
    /// depois renomeia, para o projetor nunca ler um ficheiro a meio da cópia.
    /// </summary>
    public async Task<MediaFile> ImportAsync(string originalName, Stream content, CancellationToken cancellationToken = default,
        string? subfolder = null)
    {
        var name = SanitizeFileName(originalName);
        if (!IsSupported(name))
            throw new InvalidOperationException($"Formato não suportado: {Path.GetExtension(name)}. Use JPG, PNG, WEBP, GIF, MP4 ou WEBM.");

        var folder = GalleryFolder;
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            folder = TryGetFullPath(subfolder) ?? throw new InvalidOperationException("Subpasta inválida.");
            Directory.CreateDirectory(folder);
        }

        var target = UniquePath(Path.Combine(folder, name));
        var temp = Path.Combine(GalleryFolder, $".importar-{Guid.NewGuid():N}{Path.GetExtension(name)}");

        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxImportBytes)
                        throw new InvalidOperationException($"Ficheiro maior que {MaxImportBytes / (1024 * 1024)} MB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            File.Move(temp, target);
            return ToMediaFile(target);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    /// <summary>Mantém só o nome (sem pastas) e troca caracteres problemáticos.</summary>
    public static string SanitizeFileName(string originalName)
    {
        var name = Path.GetFileName(originalName.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimStart('.');
        return string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(clean)) ? $"midia{Path.GetExtension(clean)}" : clean;
    }

    private string? TryGetFullPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var root = Path.GetFullPath(GalleryFolder + Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        // Proteção contra "../../" (path traversal).
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private MediaFile ToMediaFile(string fullPath)
    {
        var info = new FileInfo(fullPath);
        var relative = Path.GetRelativePath(GalleryFolder, fullPath);
        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        // ?v= muda quando o ficheiro é substituído → o WebKit não usa a versão em cache.
        var url = $"{UrlPrefix}/{string.Join('/', segments.Select(Uri.EscapeDataString))}?v={info.LastWriteTimeUtc.Ticks:x}";
        return new MediaFile(info.Name, relative, url, IsVideo(relative), info.Length, info.LastWriteTimeUtc);
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // Ignora os temporários da nossa própria importação.
        if (Path.GetFileName(e.FullPath).StartsWith(".importar-", StringComparison.Ordinal))
            return;
        _debounce.Change(400, Timeout.Infinite);
    }

    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action>() ?? [])
        {
            try { handler(); }
            catch (Exception ex) { _logger?.LogError(ex, "Erro num subscritor de MediaLibraryService.Changed"); }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
