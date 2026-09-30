using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Projecao.Services.Backup;

public sealed record BackupManifest(string App, string AppVersion, DateTime CreatedUtc, int Songs, int Notices,
    IReadOnlyList<string> BibleVersions, int GalleryFiles, bool IncludesGallery);

public sealed record BackupResult(string FilePath, long SizeBytes, BackupManifest Manifest);

/// <summary>
/// Cópia de segurança / transferência para outro computador: um .zip com
///   dados/i-louvores.db   (louvores, Bíblias, avisos — cópia consistente via API de backup do SQLite)
///   dados/config.json     (fundos escolhidos, tema, monitor…)
///   galeria/…             (opcional: imagens e vídeos)
///   manifest.json         (o que contém)
/// A importação não troca a base em uso: prepara-a e é aplicada no próximo arranque
/// (ApplyPendingRestore), guardando a base anterior ao lado.
/// </summary>
public sealed class BackupService(AppPaths paths, ILogger<BackupService> logger)
{
    public const string PendingFolderName = "restauro-pendente";

    public string BackupFolder => Path.Combine(paths.DocumentsRoot, "Backups");

    public async Task<BackupResult> ExportAsync(bool includeGallery, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupFolder);
        var file = Path.Combine(BackupFolder, $"i-louvores-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        var tempDb = Path.Combine(Path.GetTempPath(), $"i-louvores-export-{Guid.NewGuid():N}.db");

        try
        {
            // Cópia consistente mesmo com a app a usar a base (inclui o que está no WAL).
            await using (var source = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly"))
            await using (var target = new SqliteConnection($"Data Source={tempDb};Pooling=False"))
            {
                await source.OpenAsync(ct);
                await target.OpenAsync(ct);
                source.BackupDatabase(target);
            }

            var manifest = await ReadManifestInfoAsync(tempDb, includeGallery, ct);
            var tempZip = file + ".tmp";
            await using (var zipStream = new FileStream(tempZip, FileMode.Create))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tempDb, "dados/i-louvores.db", CompressionLevel.Optimal);

                var config = Path.Combine(paths.DataFolder, "config.json");
                if (File.Exists(config))
                    zip.CreateEntryFromFile(config, "dados/config.json");

                if (includeGallery && Directory.Exists(paths.GalleryFolder))
                    foreach (var f in Directory.EnumerateFiles(paths.GalleryFolder, "*", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(f).StartsWith('.')) continue;
                        var rel = Path.GetRelativePath(paths.GalleryFolder, f).Replace('\\', '/');
                        // Imagens/vídeos já vêm comprimidos: guardar sem recomprimir é mais rápido.
                        zip.CreateEntryFromFile(f, $"galeria/{rel}", CompressionLevel.NoCompression);
                    }

                var entry = zip.CreateEntry("manifest.json");
                await using var w = entry.Open();
                await JsonSerializer.SerializeAsync(w, manifest, new JsonSerializerOptions { WriteIndented = true }, ct);
            }
            File.Move(tempZip, file, overwrite: true);

            logger.LogInformation("Cópia de segurança criada: {File}", file);
            return new BackupResult(file, new FileInfo(file).Length, manifest);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    /// <summary>
    /// Cópia rápida da base antes de uma operação que substitui dados (ex.: atualização de louvores).
    /// Fica em dados/copias/…; mantém as últimas <paramref name="keep"/>.
    /// </summary>
    public string CreateSafetyCopy(string reason, int keep = 10)
    {
        var folder = Path.Combine(paths.DataFolder, "copias");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"i-louvores-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.db");

        using (var source = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly"))
        using (var dest = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            source.Open();
            dest.Open();
            source.BackupDatabase(dest);
        }

        foreach (var old in new DirectoryInfo(folder).GetFiles("i-louvores-*.db").OrderByDescending(f => f.CreationTimeUtc).Skip(keep))
            old.Delete();

        logger.LogInformation("Cópia de segurança antes de \"{Reason}\": {File}", reason, target);
        return target;
    }

    /// <summary>Valida o .zip e deixa-o preparado para ser aplicado no próximo arranque.</summary>
    public async Task<BackupManifest> PrepareRestoreAsync(Stream zipContent, CancellationToken ct = default)
    {
        var pending = Path.Combine(paths.DataFolder, PendingFolderName);
        if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        Directory.CreateDirectory(pending);

        try
        {
            using var zip = new ZipArchive(zipContent, ZipArchiveMode.Read);
            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Não é uma cópia de segurança do I-LOUVORES (falta manifest.json).");
            BackupManifest manifest;
            await using (var ms = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, cancellationToken: ct)
                           ?? throw new InvalidDataException("manifest.json inválido.");
            if (manifest.App != "I-LOUVORES")
                throw new InvalidDataException("Este ficheiro não foi criado pelo I-LOUVORES.");

            var root = Path.GetFullPath(pending + Path.DirectorySeparatorChar);
            foreach (var entry in zip.Entries.Where(e => e.Length > 0 || !string.IsNullOrEmpty(e.Name)))
            {
                var target = Path.GetFullPath(Path.Combine(pending, entry.FullName));
                if (!target.StartsWith(root, StringComparison.Ordinal)) // proteção "zip slip"
                    throw new InvalidDataException($"Caminho inválido no zip: {entry.FullName}");
                if (string.IsNullOrEmpty(entry.Name)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            var db = Path.Combine(pending, "dados", "i-louvores.db");
            if (!File.Exists(db))
                throw new InvalidDataException("A cópia não contém a base de dados.");
            await using (var check = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False"))
            {
                await check.OpenAsync(ct);
                await using var cmd = check.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                if ((string?)await cmd.ExecuteScalarAsync(ct) != "ok")
                    throw new InvalidDataException("A base de dados da cópia está danificada.");
            }

            logger.LogInformation("Restauro preparado ({Songs} louvores, Bíblias: {Bibles})", manifest.Songs, string.Join(", ", manifest.BibleVersions));
            return manifest;
        }
        catch
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            throw;
        }
    }

    public bool HasPendingRestore => Directory.Exists(Path.Combine(paths.DataFolder, PendingFolderName));

    public void CancelPendingRestore()
    {
        var pending = Path.Combine(paths.DataFolder, PendingFolderName);
        if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
    }

    /// <summary>
    /// Chamado no arranque ANTES de abrir a base. Guarda a base atual como
    /// i-louvores.db.antes-do-restauro-AAAAMMDD-HHmm e coloca a da cópia no lugar.
    /// </summary>
    public static bool ApplyPendingRestore(AppPaths paths, ILogger? logger = null)
    {
        var pending = Path.Combine(paths.DataFolder, PendingFolderName);
        var newDb = Path.Combine(pending, "dados", "i-louvores.db");
        if (!File.Exists(newDb))
            return false;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var current = paths.DatabasePath + suffix;
            if (File.Exists(current))
                File.Move(current, $"{paths.DatabasePath}.antes-do-restauro-{stamp}{suffix}", overwrite: true);
        }
        File.Copy(newDb, paths.DatabasePath);

        var config = Path.Combine(pending, "dados", "config.json");
        if (File.Exists(config))
            File.Copy(config, Path.Combine(paths.DataFolder, "config.json"), overwrite: true);

        var gallery = Path.Combine(pending, "galeria");
        if (Directory.Exists(gallery))
            foreach (var f in Directory.EnumerateFiles(gallery, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(paths.GalleryFolder, Path.GetRelativePath(gallery, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(f, target); // não substitui ficheiros já existentes
            }

        Directory.Delete(pending, recursive: true);
        logger?.LogInformation("Cópia de segurança restaurada (base anterior guardada com o sufixo antes-do-restauro-{Stamp})", stamp);
        return true;
    }

    private async Task<BackupManifest> ReadManifestInfoAsync(string db, bool includeGallery, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);

        async Task<int> Count(string sql)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = sql;
            return Convert.ToInt32(await c.ExecuteScalarAsync(ct));
        }

        var versions = new List<string>();
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT Abbreviation FROM BibleVersions ORDER BY Abbreviation";
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) versions.Add(r.GetString(0));
        }

        var galleryFiles = includeGallery && Directory.Exists(paths.GalleryFolder)
            ? Directory.EnumerateFiles(paths.GalleryFolder, "*", SearchOption.AllDirectories).Count(f => !Path.GetFileName(f).StartsWith('.'))
            : 0;

        return new BackupManifest("I-LOUVORES", typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "?",
            DateTime.UtcNow, await Count("SELECT COUNT(*) FROM Songs"), await Count("SELECT COUNT(*) FROM Notices"),
            versions, galleryFiles, includeGallery);
    }
}
