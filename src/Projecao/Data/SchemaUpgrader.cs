using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Projecao.Data;

/// <summary>
/// Evolução do schema em bases já existentes. O EnsureCreated só cria bases novas;
/// aqui aplicamos alterações aditivas (colunas novas) de forma idempotente, com a
/// versão guardada em PRAGMA user_version. Nunca apaga dados.
/// </summary>
public static class SchemaUpgrader
{
    public const int CurrentVersion = 2;

    public static void Upgrade(AppDbContext db)
    {
        var version = Scalar(db, "PRAGMA user_version;");
        if (version >= CurrentVersion)
            return;

        // v1: BibleVersions.Copyright (crédito obrigatório de versões Creative Commons)
        if (version < 1)
            AddColumnIfMissing(db, "BibleVersions", "Copyright", "TEXT NULL");

        // v2: pesquisa ignora pontuação e "Ó/Oh/Óh" → recalcula as colunas normalizadas dos louvores
        if (version < 2)
            RecomputeSongSearchColumns(db);

        db.Database.ExecuteSqlRaw($"PRAGMA user_version = {CurrentVersion};");
    }

    private static void RecomputeSongSearchColumns(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        if (Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Songs';") == 0)
            return; // base sem louvores (ainda) — nada a recalcular

        using var tx = conn.BeginTransaction();

        var rows = new List<(long Id, string Title, string Lyrics)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Id, Title, Lyrics FROM Songs";
            using var r = read.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }

        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE Songs SET NormalizedTitle = $t, NormalizedLyrics = $l WHERE Id = $id";
            var pT = update.CreateParameter(); pT.ParameterName = "$t"; update.Parameters.Add(pT);
            var pL = update.CreateParameter(); pL.ParameterName = "$l"; update.Parameters.Add(pL);
            var pI = update.CreateParameter(); pI.ParameterName = "$id"; update.Parameters.Add(pI);
            foreach (var (id, title, lyrics) in rows)
            {
                pT.Value = TextNormalizer.NormalizeForSearch(title);
                pL.Value = TextNormalizer.NormalizeForSearch(lyrics);
                pI.Value = id;
                update.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static void AddColumnIfMissing(AppDbContext db, string table, string column, string definition)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
        {
            cmd.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
            cmd.ExecuteNonQuery();
        }
    }

    private static int Scalar(AppDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
