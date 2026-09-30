using Microsoft.EntityFrameworkCore;
using Projecao.Models;

namespace Projecao.Data;

/// <summary>
/// Contexto EF Core (SQLite). Em Blazor use sempre <c>IDbContextFactory&lt;AppDbContext&gt;</c>
/// e crie um contexto por operação — o DbContext não é thread-safe e os
/// componentes vivem muito tempo.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Song> Songs => Set<Song>();
    public DbSet<BibleVersion> BibleVersions => Set<BibleVersion>();
    public DbSet<BibleBook> BibleBooks => Set<BibleBook>();
    public DbSet<BibleVerse> BibleVerses => Set<BibleVerse>();
    public DbSet<Notice> Notices => Set<Notice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Song>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Author).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.MusicalKey).HasMaxLength(10);
            e.Property(x => x.Rhythm).HasMaxLength(50);
            e.Property(x => x.Collection).HasMaxLength(100);
            e.Property(x => x.BackgroundFile).HasMaxLength(500);
            e.Property(x => x.FirstSlideBackgroundFile).HasMaxLength(500);
            e.Property(x => x.Lyrics).IsRequired();
            e.Property(x => x.NormalizedTitle).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedLyrics).IsRequired();

            // Pesquisa por número é sempre dentro de uma coletânea.
            e.HasIndex(x => new { x.Collection, x.Number });
            e.HasIndex(x => x.AltNumber);
            e.HasIndex(x => x.NormalizedTitle);
            e.HasIndex(x => x.Category);
        });

        modelBuilder.Entity<BibleVersion>(e =>
        {
            e.Property(x => x.Abbreviation).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.Copyright).HasMaxLength(1000);
            e.HasIndex(x => x.Abbreviation).IsUnique();
        });

        modelBuilder.Entity<BibleBook>(e =>
        {
            // Id = ordem canónica fixa (1..66); nunca gerado pela base.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Abbreviation).HasMaxLength(10).IsRequired();
            e.Property(x => x.Testament).HasConversion<string>().HasMaxLength(5);
        });

        modelBuilder.Entity<BibleVerse>(e =>
        {
            e.Property(x => x.Text).IsRequired();

            // Índice único que também serve a consulta em cascata Livro → Capítulo → Versículo.
            e.HasIndex(x => new { x.VersionId, x.BookId, x.Chapter, x.Verse }).IsUnique();

            e.HasOne(x => x.Version)
                .WithMany(v => v.Verses)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Book)
                .WithMany()
                .HasForeignKey(x => x.BookId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Notice>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.FontFamily).HasMaxLength(100);
            e.Property(x => x.ForeColor).HasMaxLength(20);
            e.Property(x => x.BackColor).HasMaxLength(20);
            e.Property(x => x.TextAlign).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(x => x.IsTemplate);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyConventions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyConventions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Preenche datas de auditoria e colunas de pesquisa normalizadas.</summary>
    private void ApplyConventions()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Song>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.NormalizedTitle = TextNormalizer.NormalizeForSearch(entry.Entity.Title);
                entry.Entity.NormalizedLyrics = TextNormalizer.NormalizeForSearch(entry.Entity.Lyrics);
            }
        }
    }
}
