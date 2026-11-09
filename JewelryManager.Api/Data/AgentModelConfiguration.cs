using JewelryManager.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Data;

/// <summary>The tables of the WhatsApp agent, kept apart so AppDbContext stays readable.</summary>
public static class AgentModelConfiguration
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<AgentUser>(e =>
        {
            e.HasIndex(u => u.Phone).IsUnique();
            e.Property(u => u.Phone).HasMaxLength(20);
            e.Property(u => u.Gender).HasMaxLength(20);
            e.HasOne<Business>().WithMany().HasForeignKey(u => u.BusinessId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentMessage>(e =>
        {
            e.HasIndex(m => new { m.AgentUserId, m.CreatedAt });
            e.Property(m => m.Role).HasMaxLength(20);
            e.Property(m => m.Text).HasMaxLength(4000);
            e.HasOne<AgentUser>().WithMany().HasForeignKey(m => m.AgentUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentPendingAction>(e =>
        {
            e.HasIndex(a => new { a.AgentUserId, a.CreatedAt });
            e.Property(a => a.ToolName).HasMaxLength(100);
            e.Property(a => a.Summary).HasMaxLength(500);
            e.Property(a => a.Resolution).HasMaxLength(20);
            e.HasOne<AgentUser>().WithMany().HasForeignKey(a => a.AgentUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentPreference>(e =>
        {
            e.HasIndex(p => new { p.BusinessId, p.AgentUserId, p.Key });
            e.Property(p => p.Key).HasMaxLength(60);
            e.Property(p => p.Instruction).HasMaxLength(200);
            e.HasOne<Business>().WithMany().HasForeignKey(p => p.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentAlias>(e =>
        {
            e.HasIndex(a => new { a.BusinessId, a.Nickname }).IsUnique();
            e.Property(a => a.Nickname).HasMaxLength(60);
            e.Property(a => a.FullName).HasMaxLength(120);
            e.HasOne<Business>().WithMany().HasForeignKey(a => a.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentUsage>(e =>
        {
            e.HasIndex(u => new { u.AgentUserId, u.CreatedAt });
            e.HasIndex(u => new { u.BusinessId, u.CreatedAt });
            e.Property(u => u.Model).HasMaxLength(60);
            e.HasOne<AgentUser>().WithMany().HasForeignKey(u => u.AgentUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentAuditEntry>(e =>
        {
            e.HasIndex(a => new { a.AgentUserId, a.CreatedAt });
            e.Property(a => a.Tool).HasMaxLength(100);
            e.Property(a => a.Note).HasMaxLength(300);
            e.HasOne<AgentUser>().WithMany().HasForeignKey(a => a.AgentUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentProcessedMessage>(e =>
        {
            e.HasKey(m => m.MessageId);
            e.Property(m => m.MessageId).HasMaxLength(200);
            e.HasIndex(m => m.ReceivedAt);
        });

        b.Entity<AgentUser>().Property(u => u.RestDays).HasMaxLength(20).HasDefaultValue("none");
        b.Entity<AgentMessage>().Property(m => m.Source).HasMaxLength(20).HasDefaultValue("chat");

        b.Entity<CoachItem>(e =>
        {
            e.HasIndex(i => new { i.BusinessId, i.Key }).IsUnique();
            e.HasIndex(i => new { i.BusinessId, i.State });
            e.Property(i => i.Key).HasMaxLength(160);
            e.Property(i => i.Kind).HasMaxLength(30);
            e.Property(i => i.Title).HasMaxLength(300);
            e.Property(i => i.State).HasMaxLength(20);
            e.HasOne<Business>().WithMany().HasForeignKey(i => i.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CoachFocus>(e =>
        {
            e.HasIndex(f => f.BusinessId).IsUnique();
            e.Property(f => f.Area).HasMaxLength(30);
            e.Property(f => f.Reason).HasMaxLength(300);
            e.Property(f => f.FirstStep).HasMaxLength(300);
            e.HasOne<Business>().WithMany().HasForeignKey(f => f.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CoachOccasion>(e =>
        {
            e.HasIndex(o => new { o.BusinessId, o.Code }).IsUnique();
            e.Property(o => o.Code).HasMaxLength(30);
            e.HasOne<Business>().WithMany().HasForeignKey(o => o.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecurringObligation>(e =>
        {
            e.HasIndex(o => o.BusinessId);
            e.Property(o => o.Title).HasMaxLength(200);
            e.Property(o => o.LastCreatedMonth).HasMaxLength(7);
            e.HasOne<Business>().WithMany().HasForeignKey(o => o.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MetalMapping>(e =>
        {
            e.HasIndex(m => new { m.BusinessId, m.MaterialName }).IsUnique();
            e.Property(m => m.MaterialName).HasMaxLength(100);
            e.Property(m => m.Metal).HasMaxLength(10);
            e.Property(m => m.Purity).HasPrecision(6, 4);
            e.Property(m => m.LastSettingsPrice).HasPrecision(18, 4);
            e.Property(m => m.BaselineSpotIlsPerGram).HasPrecision(18, 4);
            e.Property(m => m.ImpliedPremium).HasPrecision(10, 4);
            e.HasOne<Business>().WithMany().HasForeignKey(m => m.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MetalPriceReading>(e =>
        {
            e.HasIndex(r => new { r.Metal, r.At });
            e.Property(r => r.Metal).HasMaxLength(10);
            e.Property(r => r.Source).HasMaxLength(60);
            e.Property(r => r.Note).HasMaxLength(200);
            e.Property(r => r.UsdPerOunce).HasPrecision(18, 4);
            e.Property(r => r.UsdIls).HasPrecision(10, 4);
            e.Property(r => r.IlsPerGram).HasPrecision(18, 4);
        });

        b.Entity<TaskChange>(e =>
        {
            e.HasIndex(c => new { c.BusinessId, c.TaskId, c.At });
            e.Property(c => c.Field).HasMaxLength(30);
            e.Property(c => c.OldValue).HasMaxLength(300);
            e.Property(c => c.NewValue).HasMaxLength(300);
            e.Property(c => c.Source).HasMaxLength(20);
            e.HasOne<Business>().WithMany().HasForeignKey(c => c.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        // A small step belongs to its big task; deleting the big one frees the steps instead of deleting them.
        b.Entity<TaskItem>().HasOne<TaskItem>().WithMany().HasForeignKey(t => t.ParentTaskId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<AgentProactiveSent>(e =>
        {
            e.HasIndex(s => new { s.AgentUserId, s.Kind, s.Key }).IsUnique();
            e.Property(s => s.Kind).HasMaxLength(40);
            e.Property(s => s.Key).HasMaxLength(120);
            e.HasOne<AgentUser>().WithMany().HasForeignKey(s => s.AgentUserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
