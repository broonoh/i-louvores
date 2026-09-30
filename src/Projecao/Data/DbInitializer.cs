using Microsoft.EntityFrameworkCore;
using Projecao.Models;

namespace Projecao.Data;

public static class DbInitializer
{
    /// <summary>
    /// Cria a base (se não existir), ativa WAL e insere dados iniciais.
    /// NOTA: EnsureCreated não evolui o schema. Antes da 1.ª versão em produção,
    /// substituir por Migrations (ver README) e chamar <c>Database.Migrate()</c>.
    /// </summary>
    public static void Initialize(AppDbContext db)
    {
        db.Database.EnsureCreated();

        // WAL: leituras não bloqueiam escritas e a base resiste melhor a quedas de energia.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        SchemaUpgrader.Upgrade(db);

        if (!db.BibleBooks.Any())
            db.BibleBooks.AddRange(BibleCanon.CreateBooks());

        if (!db.Songs.Any())
        {
            db.Songs.Add(new Song
            {
                Collection = "Avulsos",
                Number = 1,
                Title = "Louvor de Exemplo",
                Author = "Projeção",
                Category = "Exemplo",
                MusicalKey = "G",
                Rhythm = "4/4",
                Lyrics =
                    "Esta é a primeira estrofe\nde um louvor de exemplo\n\n" +
                    "Cada linha em branco\nseparada cria um novo slide\n\n" +
                    "Use as setas ou Próximo\npara avançar na projeção"
            });
        }

        db.SaveChanges();
    }
}
