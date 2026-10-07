using JewelryManager.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Data;

/// <summary>
/// The single gateway to the PostgreSQL database.
/// EF Core creates one instance of this per HTTP request (scoped lifetime).
/// DbSet properties represent database tables — one per entity.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Settings> Settings => Set<Settings>();

    // More DbSets will be added here as features are built.

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ── Business ──────────────────────────────────────────────────────────
        b.Entity<Business>(e =>
        {
            e.Property(x => x.Branding).HasColumnType("jsonb");
            e.Property(x => x.ShopifySettings).HasColumnType("jsonb");
        });

        // Enums are stored as readable strings instead of integers.
        b.Entity<User>().Property(u => u.Role).HasConversion<string>();

        // ── Settings ──────────────────────────────────────────────────────────
        b.Entity<Settings>(e =>
        {
            e.ToTable("Settings");
            e.Property(s => s.Data).HasColumnType("jsonb");

            // One settings row per business.
            e.HasIndex(s => s.BusinessId).IsUnique();

            e.HasOne(s => s.Business)
             .WithMany()
             .HasForeignKey(s => s.BusinessId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── User ──────────────────────────────────────────────────────────────
        b.Entity<User>(e =>
        {
            // FirebaseUid must be unique across all users (replaces @@unique in Prisma).
            // IsUnique(false) is NOT what we want — just omit the false to make it unique.
            e.HasIndex(u => u.FirebaseUid).IsUnique();

            // Index on BusinessId speeds up queries that filter by tenant.
            e.HasIndex(u => u.BusinessId);

            // Defines the FK relationship: User.BusinessId → Business.Id
            e.HasOne(u => u.Business)
             .WithMany(b => b.Users)
             .HasForeignKey(u => u.BusinessId)
             .OnDelete(DeleteBehavior.SetNull); // if a business is deleted, don't delete the user
        });

        // ── Collection ────────────────────────────────────────────────────────
        b.Entity<Collection>(e =>
        {
            e.HasIndex(c => c.BusinessId);

            e.HasOne(c => c.Business)
             .WithMany(b => b.Collections)
             .HasForeignKey(c => c.BusinessId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(b);
    }
}
