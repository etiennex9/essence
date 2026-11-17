using Essence.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Essence.Data;

public sealed class EssenceDbContext(DbContextOptions<EssenceDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Name of the GiST index on <c>ll_to_earth(latitude, longitude)</c>, created with raw SQL in the initial migration.
    /// The API no longer queries by distance in SQL (its snapshot of the stations has their corrected points, which the
    /// table doesn't), so nothing reads it now; it's kept for queries by hand.
    /// </summary>
    public const string EarthIndexName = "ix_stations_earth";

    public DbSet<FuelType> FuelTypes => Set<FuelType>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<StationChange> StationChanges => Set<StationChange>();
    public DbSet<PriceHistory> PriceHistory => Set<PriceHistory>();
    public DbSet<CurrentPrice> CurrentPrices => Set<CurrentPrice>();
    public DbSet<Import> Imports => Set<Import>();
    public DbSet<StationHoursEntry> StationHours => Set<StationHoursEntry>();
    public DbSet<HoursImport> HoursImports => Set<HoursImport>();
    public DbSet<PositionCandidate> PositionCandidates => Set<PositionCandidate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("cube");
        modelBuilder.HasPostgresExtension("earthdistance");

        modelBuilder.Entity<FuelType>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Station>(e =>
        {
            e.HasIndex(x => x.NaturalKey).IsUnique();
            e.HasIndex(x => x.Region);
            e.HasIndex(x => x.Brand);
        });

        modelBuilder.Entity<StationChange>(e =>
        {
            e.HasOne(x => x.Station).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Import).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StationId, x.ChangedAt });
        });

        modelBuilder.Entity<PriceHistory>(e =>
        {
            e.Property(x => x.PriceCents).HasPrecision(5, 1);
            e.HasOne(x => x.Station).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FuelType).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Import).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StationId, x.FuelTypeId, x.ValidFrom }).IsDescending(false, false, true);
            // At most one open row per station and fuel: guards the import's close-then-insert logic.
            e.HasIndex(x => new { x.StationId, x.FuelTypeId })
                .IsUnique()
                .HasFilter("valid_to IS NULL")
                .HasDatabaseName("ux_price_history_open");
        });

        modelBuilder.Entity<CurrentPrice>(e =>
        {
            e.HasKey(x => new { x.StationId, x.FuelTypeId });
            e.Property(x => x.PriceCents).HasPrecision(5, 1);
            e.HasOne(x => x.Station).WithMany(s => s.CurrentPrices).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FuelType).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PriceHistory).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.FuelTypeId, x.PriceCents });
        });

        modelBuilder.Entity<Import>(e =>
        {
            e.Property(x => x.Status).HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<ImportStatus>(v, true));
            e.Property(x => x.Source).HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<ImportSource>(v, true));
            // Failed attempts may repeat a generated_at; only one successful import per version.
            e.HasIndex(x => x.GeneratedAt).IsUnique().HasFilter("status = 'succeeded'");
            e.HasIndex(x => x.Sha256);
        });

        modelBuilder.Entity<StationHoursEntry>(e =>
        {
            e.Property(x => x.TemporarilyClosed).HasDefaultValue(false);
            e.HasOne(x => x.Station).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.HoursImport).WithMany().OnDelete(DeleteBehavior.Restrict);
            // One row per station and source; a source's run reads and replaces its own rows.
            e.HasIndex(x => new { x.StationId, x.Source }).IsUnique();
            e.HasIndex(x => x.Source);
        });

        modelBuilder.Entity<HoursImport>(e =>
        {
            e.Property(x => x.Status).HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<HoursImportStatus>(v, true));
            // Each source's latest runs: its last success, and the failures since.
            e.HasIndex(x => new { x.Source, x.FinishedAt });
        });

        modelBuilder.Entity<PositionCandidate>(e =>
        {
            // At most one per station; the ingest replaces them all at each run.
            e.HasKey(x => x.StationId);
            e.Property(x => x.StationId).ValueGeneratedNever();
            e.Property(x => x.Evidence).HasColumnType("jsonb");
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
