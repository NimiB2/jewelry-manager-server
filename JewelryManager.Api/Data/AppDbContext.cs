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

    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
    public DbSet<Income> Incomes => Set<Income>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<ExpenseType> ExpenseTypes => Set<ExpenseType>();
    public DbSet<ExpenseSupplier> ExpenseSuppliers => Set<ExpenseSupplier>();

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
            e.Property(i => i.Note).HasMaxLength(500);
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

        // ── Finances ──────────────────────────────────────────────────────────
        b.Entity<RecurringExpense>(e =>
        {
            e.HasIndex(r => new { r.BusinessId, r.IsActive });
            e.Property(r => r.Amount).HasPrecision(18, 2);
            e.Property(r => r.Category).HasConversion<string>();
            e.Property(r => r.Description).HasMaxLength(300);
            e.Property(r => r.TypeName).HasMaxLength(100);
            e.Property(r => r.Supplier).HasMaxLength(100);
            e.HasOne<Business>().WithMany().HasForeignKey(r => r.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Expense>(e =>
        {
            e.HasIndex(x => new { x.BusinessId, x.Date });
            e.HasIndex(x => x.SeriesId);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Category).HasConversion<string>();
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.TypeName).HasMaxLength(100);
            e.Property(x => x.Supplier).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.InvoiceStoredName).HasMaxLength(100);
            e.Property(x => x.InvoiceFileName).HasMaxLength(260);
            e.Property(x => x.InvoiceContentType).HasMaxLength(100);
            e.HasIndex(x => x.OrderId);
            e.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Cascade);

            // An order is only soft-deleted, but if one is ever removed the expense stays.
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.SetNull);

            // Stopping a series must not erase the expenses it already produced.
            e.HasOne<RecurringExpense>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Income>(e =>
        {
            e.HasIndex(x => new { x.BusinessId, x.Date });

            // One income per order: completing an order twice can never double the books.
            e.HasIndex(x => x.OrderId).IsUnique().HasFilter("\"OrderId\" IS NOT NULL");
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.WorkHours).HasPrecision(18, 4);
            e.Property(x => x.LaborHourRate).HasPrecision(18, 4);
            e.Property(x => x.Category).HasConversion<string>();
            e.Property(x => x.Description).HasMaxLength(300);
            e.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ExpenseType>(e =>
        {
            e.HasIndex(t => new { t.BusinessId, t.Name }).IsUnique();
            e.HasOne<Business>().WithMany().HasForeignKey(t => t.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ExpenseSupplier>(e =>
        {
            e.HasIndex(s => new { s.BusinessId, s.Name }).IsUnique();
            e.Property(s => s.Name).HasMaxLength(100);
            e.HasOne<Business>().WithMany().HasForeignKey(s => s.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Tasks ─────────────────────────────────────────────────────────────
        b.Entity<TaskItem>(e =>
        {
            e.ToTable("Tasks");
            e.HasIndex(t => new { t.BusinessId, t.Status });
            e.HasIndex(t => t.OrderId);
            e.Property(t => t.Title).HasMaxLength(200);
            e.Property(t => t.Content).HasMaxLength(4000);
            e.Property(t => t.Status).HasConversion<string>();
            e.HasOne<Business>().WithMany().HasForeignKey(t => t.BusinessId).OnDelete(DeleteBehavior.Cascade);

            // Orders are only soft-deleted, but if one is ever removed the task stays.
            e.HasOne<Order>().WithMany().HasForeignKey(t => t.OrderId).OnDelete(DeleteBehavior.SetNull);
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
