using AIOMarketMaker.Core.Data.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AIOMarketMaker.Core.Data;

public class EtlDbContext : DbContext
{
    private readonly string? _connectionString;

    public EtlDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    [ActivatorUtilitiesConstructor]
    public EtlDbContext(DbContextOptions<EtlDbContext> options) : base(options)
    {
    }

    public DbSet<ScrapeJob> ScrapeJobs { get; set; } = null!;
    public DbSet<Listing> Listings { get; set; } = null!;
    public DbSet<ListingStatusHistory> ListingStatusHistory { get; set; } = null!;
    public DbSet<ScrapeRun> ScrapeRuns { get; set; } = null!;
    public DbSet<ScrapeRunIssue> ScrapeRunIssues { get; set; } = null!;
    public DbSet<ListingRelationship> ListingRelationships { get; set; } = null!;
    public DbSet<Category> Categories { get; set; } = null!;
    public DbSet<JobCategory> JobCategories { get; set; } = null!;
    public DbSet<ListingPrediction> ListingPredictions { get; set; } = null!;
    public DbSet<TaxonomyRun> TaxonomyRuns { get; set; } = null!;
    public DbSet<TaxonomyAxis> TaxonomyAxes { get; set; } = null!;
    public DbSet<TaxonomyAxisValue> TaxonomyAxisValues { get; set; } = null!;
    public DbSet<TaxonomyListingAssignment> TaxonomyListingAssignments { get; set; } = null!;
    public DbSet<TaxonomyOpportunity> TaxonomyOpportunities { get; set; } = null!;

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

        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("Categories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);
        });

        modelBuilder.Entity<JobCategory>(entity =>
        {
            entity.ToTable("JobCategories");
            entity.HasKey(e => new { e.JobId, e.CategoryId });

            entity.HasOne(e => e.Job)
                .WithMany(j => j.JobCategories)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Category)
                .WithMany(c => c.JobCategories)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.CategoryId);
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
            entity.HasIndex(e => e.BatchId)
                .HasFilter("[BatchId] IS NOT NULL");

            entity.Property(e => e.BatchPhase).HasMaxLength(20);
            entity.Property(e => e.CurrentPostStage).HasMaxLength(100);
        });

        modelBuilder.Entity<ScrapeRunIssue>(entity =>
        {
            entity.ToTable("ScrapeRunIssues");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ListingId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.IssueType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.Phase).HasMaxLength(50);
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

        modelBuilder.Entity<ListingPrediction>(entity =>
        {
            entity.ToTable("ListingPredictions");
            entity.HasKey(e => e.ListingId);

            entity.Property(e => e.AverageSoldPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.MedianSoldPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.PotentialProfit).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ComputedUtc).HasDefaultValueSql(dateDefaultSql);

            entity.HasOne(e => e.Listing)
                .WithOne()
                .HasForeignKey<ListingPrediction>(e => e.ListingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaxonomyRun>(entity =>
        {
            entity.ToTable("TaxonomyRuns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);
            entity.HasIndex(e => e.ScrapeJobId);
            entity.HasOne(e => e.ScrapeJob)
                .WithMany()
                .HasForeignKey(e => e.ScrapeJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaxonomyAxis>(entity =>
        {
            entity.ToTable("TaxonomyAxes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.TaxonomyRunId);
            entity.HasOne(e => e.TaxonomyRun)
                .WithMany(r => r.Axes)
                .HasForeignKey(e => e.TaxonomyRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaxonomyAxisValue>(entity =>
        {
            entity.ToTable("TaxonomyAxisValues");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.TaxonomyAxisId);
            entity.HasOne(e => e.TaxonomyAxis)
                .WithMany(a => a.Values)
                .HasForeignKey(e => e.TaxonomyAxisId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaxonomyListingAssignment>(entity =>
        {
            entity.ToTable("TaxonomyListingAssignments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CellJson).IsRequired().HasMaxLength(500);
            entity.HasIndex(e => e.TaxonomyRunId);
            entity.HasIndex(e => e.ListingId);
            entity.HasOne(e => e.TaxonomyRun)
                .WithMany(r => r.Assignments)
                .HasForeignKey(e => e.TaxonomyRunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Listing)
                .WithMany()
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<TaxonomyOpportunity>(entity =>
        {
            entity.ToTable("TaxonomyOpportunities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CellKey).IsRequired().HasMaxLength(500);
            entity.Property(e => e.AskPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.MedianSoldPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.EstimatedProfit).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ComputedUtc).HasDefaultValueSql(dateDefaultSql);
            entity.HasIndex(e => e.ScrapeJobId);
            entity.HasIndex(e => e.ListingId).IsUnique();
            entity.HasIndex(e => e.EstimatedProfit).IsDescending();
            entity.HasOne(e => e.ScrapeJob).WithMany().HasForeignKey(e => e.ScrapeJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Listing).WithMany().HasForeignKey(e => e.ListingId).OnDelete(DeleteBehavior.NoAction);
        });

    }
}
