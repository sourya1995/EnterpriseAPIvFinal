using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;

namespace EnterpriseApi.Infrastructure.Data;

public class AppDbContext : DbContext
{
    private readonly ICurrentUserService? _currentUser;

    public AppDbContext(DbContextOptions<AppDbContext> options,
        ICurrentUserService? currentUser = null) : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Applies all IEntityTypeConfiguration<T> classes in this assembly automatically.
        // Avoids a 500-line OnModelCreating — each entity owns its configuration.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Override SaveChangesAsync to automatically generate audit log entries
    /// for every Add/Modify/Delete operation without any service layer changes.
    /// This is the open/closed principle applied to cross-cutting concerns.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var auditEntries = BuildAuditEntries();

        var result = await base.SaveChangesAsync(ct);

        // Add audit logs after save — we need the generated PK values for new entities
        if (auditEntries.Any())
        {
            foreach (var entry in auditEntries.Where(e => e.HasTemporaryProperties))
            {
                // Post-save: fill in auto-generated IDs for "Added" entities
                foreach (var prop in entry.TemporaryProperties)
                {
                    if (prop.Metadata.IsPrimaryKey())
                        entry.AuditLog.GetType().GetProperty("EntityId")!
                            .SetValue(entry.AuditLog, prop.CurrentValue?.ToString());
                }
            }

            AuditLogs.AddRange(auditEntries.Select(e => e.AuditLog));
            await base.SaveChangesAsync(ct);
        }

        return result;
    }

    private List<AuditEntry> BuildAuditEntries()
    {
        ChangeTracker.DetectChanges();

        var auditEntries = new List<AuditEntry>();
        var userId = _currentUser?.UserId;
        var ipAddress = _currentUser?.IpAddress;

        foreach (var entry in ChangeTracker.Entries())
        {
            // Skip audit log itself — would cause infinite recursion
            if (entry.Entity is AuditLog) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var auditEntry = new AuditEntry(entry)
            {
                AuditLog = AuditLog.Create(
                    entityName: entry.Entity.GetType().Name,
                    entityId: entry.Properties
                        .FirstOrDefault(p => p.Metadata.IsPrimaryKey())
                        ?.CurrentValue?.ToString() ?? "unknown",
                    action: entry.State.ToString(),
                    oldValues: entry.State == EntityState.Added
                        ? null
                        : JsonSerializer.Serialize(
                            entry.Properties
                                .Where(p => p.IsModified || entry.State == EntityState.Deleted)
                                .ToDictionary(p => p.Metadata.Name, p => p.OriginalValue?.ToString())),
                    newValues: entry.State == EntityState.Deleted
                        ? null
                        : JsonSerializer.Serialize(
                            entry.Properties
                                .Where(p => p.IsModified || entry.State == EntityState.Added)
                                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue?.ToString())),
                    userId: userId,
                    ipAddress: ipAddress
                )
            };

            auditEntries.Add(auditEntry);
        }

        return auditEntries;
    }

    // Helper class to track temporary properties (generated IDs) pre/post save
    private class AuditEntry
    {
        public EntityEntry Entry { get; }
        public AuditLog AuditLog { get; set; } = null!;
        public List<PropertyEntry> TemporaryProperties { get; } = new();
        public bool HasTemporaryProperties => TemporaryProperties.Any();

        public AuditEntry(EntityEntry entry)
        {
            Entry = entry;
            foreach (var prop in entry.Properties.Where(p => p.IsTemporary))
                TemporaryProperties.Add(prop);
        }
    }
}
