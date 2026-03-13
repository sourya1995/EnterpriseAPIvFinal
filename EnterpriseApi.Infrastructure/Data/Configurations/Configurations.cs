using EnterpriseApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseApi.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).IsRequired().HasMaxLength(255);
        builder.HasIndex(u => u.Email).IsUnique();
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(512);
        builder.Property(u => u.FirstName).IsRequired().HasMaxLength(100);
        builder.Property(u => u.LastName).IsRequired().HasMaxLength(100);
        builder.Property(u => u.Role).IsRequired().HasMaxLength(50);

        // Optimistic concurrency — EF adds WHERE RowVersion = @original on every UPDATE.
        // SQLite uses xmin equivalent; SQL Server uses rowversion type.
        builder.UseXminAsConcurrencyToken();

        builder.HasMany(u => u.Orders).WithOne(o => o.User)
            .HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(u => u.Notifications).WithOne(n => n.User)
            .HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.Invoices).WithOne(i => i.User)
            .HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.SKU).IsRequired().HasMaxLength(50);
        builder.HasIndex(p => p.SKU).IsUnique();
        builder.Property(p => p.Category).IsRequired().HasMaxLength(100);
        builder.Property(p => p.BasePrice).HasPrecision(18, 2);
        builder.Property(p => p.DiscountedPrice).HasPrecision(18, 2);
        builder.HasIndex(p => p.Category);
        builder.HasIndex(p => p.IsActive);
        builder.UseXminAsConcurrencyToken();

        // One-to-one with inventory — each product has exactly one inventory record
        builder.HasOne(p => p.Inventory).WithOne(i => i.Product)
            .HasForeignKey<Inventory>(i => i.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class InventoryConfiguration : IEntityTypeConfiguration<Inventory>
{
    public void Configure(EntityTypeBuilder<Inventory> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.ProductId).IsRequired();
        builder.HasIndex(i => i.ProductId).IsUnique();

        // Critical for concurrent inventory updates — see OrderService.CreateOrderAsync
        builder.UseXminAsConcurrencyToken();
    }
}

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.UnitPrice).HasPrecision(18, 2);
        builder.Property(o => o.TotalAmount).HasPrecision(18, 2);

        // Store as string for readability in DB — "Pending" vs 0
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(o => o.CancellationReason).HasMaxLength(500);

        builder.HasIndex(o => o.UserId);
        builder.HasIndex(o => o.ProductId);
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => o.CreatedAt);
        builder.UseXminAsConcurrencyToken();

        builder.HasOne(o => o.Product).WithMany(p => p.Orders)
            .HasForeignKey(o => o.ProductId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.Invoice).WithOne(i => i.Order)
            .HasForeignKey<Invoice>(i => i.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.InvoiceNumber).IsRequired().HasMaxLength(50);
        builder.HasIndex(i => i.InvoiceNumber).IsUnique();
        builder.Property(i => i.Subtotal).HasPrecision(18, 2);
        builder.Property(i => i.TaxAmount).HasPrecision(18, 2);
        builder.Property(i => i.TotalAmount).HasPrecision(18, 2);
        builder.Property(i => i.TaxRate).HasPrecision(5, 4);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.PaymentReference).HasMaxLength(100);
        builder.HasIndex(i => i.UserId);
        builder.HasIndex(i => i.Status);
        builder.UseXminAsConcurrencyToken();
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Channel).IsRequired().HasMaxLength(20);
        builder.Property(n => n.Subject).IsRequired().HasMaxLength(200);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(4000);
        builder.Property(n => n.Status).IsRequired().HasMaxLength(20);
        builder.Property(n => n.FailureReason).HasMaxLength(500);
        builder.HasIndex(n => n.UserId);
        builder.HasIndex(n => n.Status);
        builder.HasIndex(n => n.CreatedAt);
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.EntityName).IsRequired().HasMaxLength(100);
        builder.Property(a => a.EntityId).IsRequired().HasMaxLength(100);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(20);
        builder.Property(a => a.IpAddress).HasMaxLength(45); // IPv6 max = 39 chars

        // Audit logs are append-only — no navigation properties intentionally
        // to avoid accidental joins that could cause N+1 queries
        builder.HasIndex(a => new { a.EntityName, a.EntityId });
        builder.HasIndex(a => a.Timestamp);
        builder.HasIndex(a => a.UserId);
    }
}
