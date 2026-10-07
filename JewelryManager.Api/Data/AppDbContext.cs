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
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<FeeItem> FeeItems => Set<FeeItem>();
    public DbSet<PricingAdditionCategory> PricingAdditionCategories => Set<PricingAdditionCategory>();
    public DbSet<PricingAdditionItem> PricingAdditionItems => Set<PricingAdditionItem>();
    public DbSet<PreparationStage> PreparationStages => Set<PreparationStage>();
    public DbSet<ProductAdditionType> ProductAdditionTypes => Set<ProductAdditionType>();
    public DbSet<DiscountPreset> DiscountPresets => Set<DiscountPreset>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLineItem> OrderLineItems => Set<OrderLineItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductCollection> ProductCollections => Set<ProductCollection>();
    public DbSet<ProductAddition> ProductAdditions => Set<ProductAddition>();

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
            e.Property(s => s.LaborHourRate).HasPrecision(18, 4);
            e.Property(s => s.ProfitFloorPercent).HasPrecision(18, 4);
            e.Property(s => s.TestOrderPrefix).HasMaxLength(50).HasDefaultValue("בדיקה");

            // One settings row per business.
            e.HasIndex(s => s.BusinessId).IsUnique();

            e.HasOne(s => s.Business)
             .WithMany()
             .HasForeignKey(s => s.BusinessId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Settings lists ────────────────────────────────────────────────────
        // Names are unique per business: the UI identifies rows by name and the
        // pricing code looks materials up by it.
        b.Entity<Material>(e =>
        {
            e.HasIndex(m => new { m.BusinessId, m.Name }).IsUnique();
            e.Property(m => m.PricePerGram).HasPrecision(18, 4);
            e.Property(m => m.LaborHours).HasPrecision(18, 4);
            e.Property(m => m.ProfitMultiplier).HasPrecision(18, 4);
            e.HasOne<Business>().WithMany().HasForeignKey(m => m.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FeeItem>(e =>
        {
            e.HasIndex(f => new { f.BusinessId, f.Name }).IsUnique();

            // At most one fee per formula role in a business; extra fees have no key.
            e.HasIndex(f => new { f.BusinessId, f.Key }).IsUnique().HasFilter("\"Key\" IS NOT NULL");
            e.Property(f => f.Percent).HasPrecision(18, 4);
            e.HasOne<Business>().WithMany().HasForeignKey(f => f.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PricingAdditionCategory>(e =>
        {
            e.HasIndex(c => new { c.BusinessId, c.Name }).IsUnique();
            e.Property(c => c.BasePrice).HasPrecision(18, 4);
            e.HasOne<Business>().WithMany().HasForeignKey(c => c.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PricingAdditionItem>(e =>
        {
            e.HasIndex(i => new { i.CategoryId, i.Name }).IsUnique();
            e.HasIndex(i => i.BusinessId);
            e.Property(i => i.Price).HasPrecision(18, 4);
            e.HasOne(i => i.Category).WithMany(c => c.Items).HasForeignKey(i => i.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Business>().WithMany().HasForeignKey(i => i.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Orders ────────────────────────────────────────────────────────────
        b.Entity<Order>(e =>
        {
            e.HasIndex(o => new { o.BusinessId, o.Number }).IsUnique();
            e.HasIndex(o => new { o.BusinessId, o.Date });
            e.Property(o => o.Amount).HasPrecision(18, 2);
            e.Property(o => o.FinalAmount).HasPrecision(18, 2);
            e.Property(o => o.LaborHourRate).HasPrecision(18, 4);
            e.Property(o => o.Status).HasConversion<string>();
            e.Property(o => o.Source).HasConversion<string>();
            e.HasOne<Business>().WithMany().HasForeignKey(o => o.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderLineItem>(e =>
        {
            e.HasIndex(i => i.OrderId);
            e.HasIndex(i => i.BusinessId);
            e.Property(i => i.UnitPrice).HasPrecision(18, 2);
            e.Property(i => i.WorkHours).HasPrecision(18, 4);
            e.HasOne(i => i.Order).WithMany(o => o.Items).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);

            // Deleting a product must not rewrite history: the line keeps its snapshot, only the link goes.
            e.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Business>().WithMany().HasForeignKey(i => i.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DiscountPreset>(e =>
        {
            e.HasIndex(d => new { d.BusinessId, d.Percent }).IsUnique();
            e.Property(d => d.Percent).HasPrecision(5, 2);
            e.HasOne<Business>().WithMany().HasForeignKey(d => d.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductAdditionType>(e =>
        {
            e.HasIndex(t => new { t.BusinessId, t.Name }).IsUnique();
            e.HasOne<Business>().WithMany().HasForeignKey(t => t.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Products ──────────────────────────────────────────────────────────
        b.Entity<Product>(e =>
        {
            e.HasIndex(p => p.BusinessId);
            e.Property(p => p.Weight).HasPrecision(18, 4);
            e.Property(p => p.AdditionalWorkHours).HasPrecision(18, 4);
            e.Property(p => p.SitePrice).HasPrecision(18, 4);
            e.HasOne<Business>().WithMany().HasForeignKey(p => p.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductCollection>(e =>
        {
            e.HasKey(pc => new { pc.ProductId, pc.CollectionId });
            e.HasIndex(pc => pc.BusinessId);
            e.HasIndex(pc => pc.CollectionId);
            e.HasOne(pc => pc.Product).WithMany(p => p.Collections).HasForeignKey(pc => pc.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(pc => pc.Collection).WithMany(c => c.Products).HasForeignKey(pc => pc.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Business>().WithMany().HasForeignKey(pc => pc.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductAddition>(e =>
        {
            e.HasIndex(a => a.ProductId);
            e.HasIndex(a => a.BusinessId);
            e.Property(a => a.Price).HasPrecision(18, 4);
            e.HasOne(a => a.Product).WithMany(p => p.Additions).HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Business>().WithMany().HasForeignKey(a => a.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PreparationStage>(e =>
        {
            e.HasIndex(s => new { s.BusinessId, s.Name }).IsUnique();
            e.HasOne<Business>().WithMany().HasForeignKey(s => s.BusinessId).OnDelete(DeleteBehavior.Cascade);
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
