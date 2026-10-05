using Hawker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hawker.Core.Data;

public class HawkerDbContext(DbContextOptions<HawkerDbContext> options) : DbContext(options)
{
    public DbSet<Feed> Feeds => Set<Feed>();
    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<EditionSchedule> EditionSchedules => Set<EditionSchedule>();
    public DbSet<EditionRun> EditionRuns => Set<EditionRun>();
    public DbSet<SeenArticle> SeenArticles => Set<SeenArticle>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare or order DateTimeOffset stored as text, so store it as a sortable integer.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Feed>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(200);
            e.Property(f => f.Url).HasMaxLength(2000);
            e.Property(f => f.ScoreSource).HasConversion<string>();
        });

        modelBuilder.Entity<Edition>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.SortMode).HasConversion<string>();
            e.Property(x => x.PaperSize).HasConversion<string>();
            e.HasMany(x => x.Feeds).WithMany(f => f.Editions);
            e.HasMany(x => x.Schedules).WithOne(s => s.Edition).HasForeignKey(s => s.EditionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Runs).WithOne(r => r.Edition).HasForeignKey(r => r.EditionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditionSchedule>(e =>
        {
            e.Property(s => s.Kind).HasConversion<string>();
            e.Property(s => s.CronExpression).HasMaxLength(100);
        });

        modelBuilder.Entity<EditionRun>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>();
            e.Property(r => r.Trigger).HasConversion<string>();
            e.HasIndex(r => new { r.EditionId, r.StartedAt });
        });

        modelBuilder.Entity<SeenArticle>(e =>
        {
            e.HasKey(s => new { s.EditionId, s.UrlHash });
            e.Property(s => s.UrlHash).HasMaxLength(64);
            e.HasOne<Edition>().WithMany().HasForeignKey(s => s.EditionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSettings>(e =>
        {
            e.HasData(new AppSettings());
        });
    }
}
