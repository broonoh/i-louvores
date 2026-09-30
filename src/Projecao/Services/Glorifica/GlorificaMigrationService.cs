using Microsoft.Extensions.Logging;
using Projecao.Services.Import;

namespace Projecao.Services.Glorifica;

/// <summary>Uma instalação do Glorifica encontrada neste computador.</summary>
public sealed record GlorificaInstallation(string DocumentsFolder, string Label, int SongCount, int ImageCount, bool HasConfig);

public sealed record GlorificaMigrationResult(ImportResult Songs, int ImagesCopied, IReadOnlyList<string> Notes);

/// <summary>
/// Migração a partir do Glorifica (Windows, também a correr em Linux via Bottles/Wine):
///   • letras de Louvores/raw/*.txt + metadados de LouvoresRaw.ini;
///   • imagens da Galeria e modelos de Etc/ (estilos, fundos, tela de espera);
///   • fundos configurados no Config.ini (louvor 1.º slide / restantes / Bíblia).
/// Os ficheiros .xbY (cifrados) não são lidos.
/// </summary>
public sealed class GlorificaMigrationService(
    SongImporter songImporter,
    MediaLibraryService media,
    GalleryService gallery,
    ILogger<GlorificaMigrationService> logger)
{
    public const string TemplatesFolder = "Glorifica - Modelos";

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    /// <summary>Procura pastas "Documents/Glorifica" em prefixos Wine conhecidos e em Documentos.</summary>
    public IReadOnlyList<GlorificaInstallation> Detect(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<(string Path, string Label)>();

        void Wine(string prefixRoot, string label, bool bottles)
        {
            if (!Directory.Exists(prefixRoot)) return;
            var prefixes = bottles ? SafeDirs(prefixRoot) : [prefixRoot];
            foreach (var prefix in prefixes)
                foreach (var user in SafeDirs(Path.Combine(prefix, "drive_c", "users")))
                    foreach (var docs in new[] { "Documents", "My Documents", "Meus Documentos", "Documentos" })
                        candidates.Add((Path.Combine(user, docs, "Glorifica"), $"{label}: {Path.GetFileName(prefix)}"));
        }

        Wine(Path.Combine(home, ".var/app/com.usebottles.bottles/data/bottles/bottles"), "Bottles", bottles: true);
        Wine(Path.Combine(home, ".local/share/bottles/bottles"), "Bottles", bottles: true);
        Wine(Path.Combine(home, "PlayOnLinux's virtual drives"), "PlayOnLinux", bottles: true);
        foreach (var dir in SafeDirs(home).Where(d => Path.GetFileName(d).StartsWith(".wine", StringComparison.Ordinal)))
            Wine(dir, "Wine", bottles: false);

        // Cópia de uma pasta do Windows (ex.: pen USB) — só se tiver Config.ini ou Louvores/raw do Glorifica.
        foreach (var docs in new[] { "Documentos", "Documents" })
            candidates.Add((Path.Combine(home, docs, "Glorifica"), "Documentos"));

        return candidates
            .Where(c => Directory.Exists(c.Path) && (File.Exists(Path.Combine(c.Path, "Config.ini")) || Directory.Exists(RawFolder(c.Path))))
            .DistinctBy(c => RealPath(c.Path)) // Bottles/Wine ligam users/<nome> → users/steamuser
            .Select(c => new GlorificaInstallation(c.Path, c.Label,
                Directory.Exists(RawFolder(c.Path)) ? Directory.GetFiles(RawFolder(c.Path), "*.txt").Length : 0,
                ImageSources(c.Path).Count,
                File.Exists(Path.Combine(c.Path, "Config.ini"))))
            .Where(i => i.SongCount > 0 || i.ImageCount > 0)
            .ToList();
    }

    /// <summary>Lê as letras (sem gravar) para pré-visualização.</summary>
    public ParseResult ReadSongs(string documentsFolder)
    {
        var raw = RawFolder(documentsFolder);
        if (!Directory.Exists(raw))
            return new ParseResult([], [new ImportIssue(documentsFolder, "Pasta Louvores/raw não encontrada.")]);

        var ini = Path.Combine(raw, "LouvoresRaw.ini");
        var files = Directory.GetFiles(raw, "*.txt").OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)));
        return SongImportParser.ParseGlorificaRaw(files, File.Exists(ini) ? File.ReadAllBytes(ini) : null);
    }

    public async Task<GlorificaMigrationResult> MigrateAsync(string documentsFolder, string? collection, bool updateExisting,
        bool importImages, bool applyBackgrounds, CancellationToken ct = default)
    {
        var notes = new List<string>();

        // 1) Letras
        var parsed = ReadSongs(documentsFolder);
        notes.AddRange(parsed.Issues.Select(i => $"{i.Source}: {i.Message}"));
        var plan = await songImporter.PlanAsync(parsed.Songs, collection, updateExisting, ct);
        var songs = await songImporter.ApplyAsync(plan, ct);

        // 2) Imagens (galeria + modelos)
        var copied = 0;
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // origem relativa → destino na Galeria
        if (importImages)
        {
            foreach (var (source, target) in ImageSources(documentsFolder))
            {
                var relativeSource = Path.GetRelativePath(documentsFolder, source);
                var existing = Path.Combine(media.GalleryFolder, target);
                if (File.Exists(existing) && new FileInfo(existing).Length == new FileInfo(source).Length)
                {
                    mapped[relativeSource] = target; // já existe igual (ex.: copiado antes)
                    continue;
                }

                await using var input = File.OpenRead(source);
                var file = await media.ImportAsync(Path.GetFileName(target), input, ct, Path.GetDirectoryName(target));
                mapped[relativeSource] = file.RelativePath;
                copied++;
            }
        }

        // 3) Fundos configurados no Glorifica
        if (applyBackgrounds && File.Exists(Path.Combine(documentsFolder, "Config.ini")))
        {
            var ini = SongImportParser.ReadIni(SongImportParser.Decode(await File.ReadAllBytesAsync(Path.Combine(documentsFolder, "Config.ini"), ct)));
            void Apply(string section, string key, BackgroundSlot slot)
            {
                var winPath = ini.GetValueOrDefault(section)?.GetValueOrDefault(key.ToLowerInvariant());
                if (MapWindowsPath(winPath) is { } rel && mapped.TryGetValue(rel, out var target))
                {
                    gallery.Select(slot, target);
                    notes.Add($"Fundo \"{SlotLabel(slot)}\" definido: {target}");
                }
            }
            Apply("Louvores", "ImgFundo_LouvorPath1", BackgroundSlot.FirstSlide);
            Apply("Louvores", "ImgFundo_LouvorPath2", BackgroundSlot.Slides);
            Apply("Bíblia", "ImgFundo_BibliaPath1", BackgroundSlot.Bible);
            Apply("Geral", "ImgFundo_GeralPath1", BackgroundSlot.General);
        }

        logger.LogInformation("Migração Glorifica: {Created} louvores novos, {Updated} atualizados, {Images} imagens",
            songs.Created, songs.Updated, copied);
        return new GlorificaMigrationResult(songs, copied, notes);
    }

    /// <summary>
    /// Imagens a copiar e o seu destino na Galeria. Nos modelos com várias resoluções
    /// (Styles/1/1024x768, 1280x720, 1920x1080) fica só a maior.
    /// </summary>
    public static IReadOnlyList<(string Source, string Target)> ImageSources(string documentsFolder)
    {
        var list = new List<(string, string)>();

        var galeria = Path.Combine(documentsFolder, "Galeria");
        foreach (var f in SafeFiles(galeria))
            list.Add((f, Path.GetFileName(f)));

        var etc = Path.Combine(documentsFolder, "Etc");
        foreach (var f in SafeFiles(Path.Combine(etc, "Bg")))
            list.Add((f, Path.Combine(TemplatesFolder, $"Fundo - {Path.GetFileName(f)}")));

        // Styles/<n>/<resolução>/styleN_1.jpg (1 = 1.º slide, 2 = restantes)
        foreach (var style in SafeDirs(Path.Combine(etc, "Styles")))
            if (BestResolution(style) is { } dir)
                foreach (var f in SafeFiles(dir))
                    list.Add((f, Path.Combine(TemplatesFolder, $"Estilo {Path.GetFileName(style)} - {Path.GetFileName(f)}")));

        foreach (var group in new[] { ("Tela de Espera", "Tela de espera"), ("Geral", "Geral") })
            foreach (var sub in SafeDirs(Path.Combine(etc, group.Item1)))
                if (BestResolution(sub) is { } dir)
                    foreach (var f in SafeFiles(dir))
                        list.Add((f, Path.Combine(TemplatesFolder, $"{group.Item2} {Path.GetFileName(sub)} - {Path.GetFileName(f)}")));

        return list;
    }

    private static string? BestResolution(string folder) =>
        SafeDirs(folder)
            .Select(d => (Dir: d, Parts: Path.GetFileName(d).Split('x')))
            .Where(x => x.Parts.Length == 2 && int.TryParse(x.Parts[0], out _) && int.TryParse(x.Parts[1], out _))
            .OrderByDescending(x => int.Parse(x.Parts[0]) * int.Parse(x.Parts[1]))
            .Select(x => x.Dir)
            .FirstOrDefault();

    /// <summary>"C:\users\x\Documents\Glorifica\Galeria\A.jpg" → "Galeria/A.jpg" (relativo à pasta do Glorifica).</summary>
    public static string? MapWindowsPath(string? windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) return null;
        var normalized = windowsPath.Replace('\\', '/');
        var marker = normalized.IndexOf("/Glorifica/", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? null : normalized[(marker + "/Glorifica/".Length)..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static string SlotLabel(BackgroundSlot slot) => slot switch
    {
        BackgroundSlot.FirstSlide => "1.º slide",
        BackgroundSlot.Slides => "slides",
        BackgroundSlot.Bible => "Bíblia",
        _ => "Geral"
    };

    /// <summary>Caminho real, resolvendo links simbólicos em qualquer componente.</summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = target.FullName;
        }
        return current;
    }

    private static string RawFolder(string documentsFolder) => Path.Combine(documentsFolder, "Louvores", "raw");

    private static IEnumerable<string> SafeDirs(string path) =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.Ordinal) : [];

    private static IEnumerable<string> SafeFiles(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path).Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.Ordinal)
            : [];
}
