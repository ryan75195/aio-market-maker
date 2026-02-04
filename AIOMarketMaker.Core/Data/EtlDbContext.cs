using AIOMarketMaker.Core.Data.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Data;

public class EtlDbContext : DbContext
{
    private readonly string? _connectionString;

    public EtlDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public EtlDbContext(DbContextOptions<EtlDbContext> options) : base(options)
    {
    }

    public DbSet<ScrapeJob> ScrapeJobs { get; set; } = null!;
    public DbSet<Listing> Listings { get; set; } = null!;
    public DbSet<ListingStatusHistory> ListingStatusHistory { get; set; } = null!;
    public DbSet<ScrapeRun> ScrapeRuns { get; set; } = null!;
    public DbSet<ScrapeRunListing> ScrapeRunListings { get; set; } = null!;
    public DbSet<ScrapeRunIssue> ScrapeRunIssues { get; set; } = null!;
    public DbSet<ListingRelationship> ListingRelationships { get; set; } = null!;
    public DbSet<ListingPrediction> ListingPredictions { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite(_connectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Detect if using SQL Server to use correct default value syntax
        var isSqlServer = Database.IsSqlServer();
        var dateDefaultSql = isSqlServer ? "GETUTCDATE()" : "datetime('now')";

        modelBuilder.Entity<ScrapeJob>(entity =>
        {
            entity.ToTable("ScrapeJobs");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SearchTerm).IsRequired().HasMaxLength(255);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);

            entity.HasIndex(e => e.IsEnabled);
        });

        modelBuilder.Entity<Listing>(entity =>
        {
            entity.ToTable("Listings");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ListingId).IsRequired();
            entity.HasIndex(e => e.ListingId).IsUnique();
            entity.HasIndex(e => e.ScrapeJobId);
            entity.HasIndex(e => e.ListingStatus);

            entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);

            entity.HasOne(e => e.ScrapeJob)
                .WithMany()
                .HasForeignKey(e => e.ScrapeJobId);
        });

        modelBuilder.Entity<ListingStatusHistory>(entity =>
        {
            entity.ToTable("ListingStatusHistory");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ListingStatus).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Source).HasMaxLength(50);
            entity.Property(e => e.RecordedUtc).HasDefaultValueSql(dateDefaultSql);

            entity.HasIndex(e => e.ListingId);
            entity.HasIndex(e => e.RecordedUtc);

            entity.HasOne(e => e.Listing)
                .WithMany(l => l.StatusHistory)
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScrapeRun>(entity =>
        {
            entity.ToTable("ScrapeRuns");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.InstanceId).HasMaxLength(100);
            entity.Property(e => e.TriggerType).IsRequired().HasMaxLength(20).HasDefaultValue("Manual");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("Running");
            entity.Property(e => e.StartedUtc).HasDefaultValueSql(dateDefaultSql);

            entity.HasIndex(e => e.StartedUtc);
            entity.HasIndex(e => e.InstanceId);
        });

        modelBuilder.Entity<ScrapeRunListing>(entity =>
        {
            entity.ToTable("ScrapeRunListings");
            entity.HasKey(e => new { e.ScrapeRunId, e.ListingId });

            entity.Property(e => e.ListingId).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Pending");
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);

            entity.HasIndex(e => e.ListingId);
            entity.HasIndex(e => new { e.ScrapeRunId, e.Status });

            entity.HasOne(e => e.ScrapeRun)
                .WithMany()
                .HasForeignKey(e => e.ScrapeRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ScrapeJob)
                .WithMany()
                .HasForeignKey(e => e.ScrapeJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScrapeRunIssue>(entity =>
        {
            entity.ToTable("ScrapeRunIssues");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ListingId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.IssueType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);

            entity.HasIndex(e => e.ScrapeRunId);

            entity.HasOne(e => e.ScrapeRun)
                .WithMany()
                .HasForeignKey(e => e.ScrapeRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ListingRelationships
        modelBuilder.Entity<ListingRelationship>(entity =>
        {
            entity.ToTable("ListingRelationships");
            entity.HasIndex(e => new { e.ListingIdA, e.ListingIdB }).IsUnique();
            entity.HasIndex(e => e.ListingIdA);
            entity.HasIndex(e => e.ListingIdB);
            entity.Property(e => e.Explanation).HasMaxLength(500);

            entity.HasOne(e => e.ListingA)
                .WithMany()
                .HasForeignKey(e => e.ListingIdA)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.ListingB)
                .WithMany()
                .HasForeignKey(e => e.ListingIdB)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // ListingPredictions
        modelBuilder.Entity<ListingPrediction>(entity =>
        {
            entity.ToTable("ListingPredictions");
            entity.HasIndex(e => e.ListingId).IsUnique();
            entity.Property(e => e.AverageSoldPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.PotentialProfit).HasColumnType("decimal(18,2)");

            entity.HasOne(e => e.Listing)
                .WithMany()
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
