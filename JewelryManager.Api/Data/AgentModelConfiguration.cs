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

        b.Entity<AgentProactiveSent>(e =>
        {
            e.HasIndex(s => new { s.AgentUserId, s.Kind, s.Key }).IsUnique();
            e.Property(s => s.Kind).HasMaxLength(40);
            e.Property(s => s.Key).HasMaxLength(120);
            e.HasOne<AgentUser>().WithMany().HasForeignKey(s => s.AgentUserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
