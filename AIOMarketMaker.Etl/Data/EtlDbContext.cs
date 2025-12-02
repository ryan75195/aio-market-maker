using AIOMarketMaker.Etl.Data.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Etl.Data;

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
    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<ListingStatusHistory> ListingStatusHistory { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite(_connectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScrapeJob>(entity =>
        {
            entity.ToTable("ScrapeJobs");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SearchTerm).IsRequired().HasMaxLength(255);
            entity.Property(e => e.BuyingFormat).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Condition).IsRequired().HasMaxLength(30);
            entity.Property(e => e.SearchType).IsRequired().HasMaxLength(10);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql("datetime('now')");
        });

        modelBuilder.Entity<Listing>(entity =>
        {
            entity.ToTable("Listings");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ListingId).IsRequired();
            entity.HasIndex(e => e.ListingId).IsUnique();
            entity.HasIndex(e => e.ScrapeJobId);
            entity.HasIndex(e => e.ListingStatus);

            entity.Property(e => e.CreatedUtc).HasDefaultValueSql("datetime('now')");

            entity.HasOne(e => e.ScrapeJob)
                .WithMany()
                .HasForeignKey(e => e.ScrapeJobId);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Category).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.ListingId).IsUnique();
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.Brand);
            entity.HasIndex(e => e.Model);

            entity.Property(e => e.ResolvedUtc).HasDefaultValueSql("datetime('now')");

            entity.HasOne(e => e.Listing)
                .WithOne(l => l.Product)
                .HasForeignKey<Product>(e => e.ListingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ListingStatusHistory>(entity =>
        {
            entity.ToTable("ListingStatusHistory");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ListingStatus).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Source).HasMaxLength(50);
            entity.Property(e => e.RecordedUtc).HasDefaultValueSql("datetime('now')");

            entity.HasIndex(e => e.ListingId);
            entity.HasIndex(e => e.RecordedUtc);

            entity.HasOne(e => e.Listing)
                .WithMany(l => l.StatusHistory)
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
