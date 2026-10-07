using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Data;

public class NuraDbContext(DbContextOptions<NuraDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<TrustBatch> TrustBatches => Set<TrustBatch>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<WhatsAppMessage> WhatsAppMessages => Set<WhatsAppMessage>();

    private static JsonSerializerOptions J => NuraJson.Options;

    /// <summary>Stores a complex value as a JSON string column (works on SQL Server and the in-memory provider).</summary>
    private static void AsJson<T>(PropertyBuilder<T> p) where T : class, new()
    {
        p.HasConversion(
            v => JsonSerializer.Serialize(v, J),
            v => string.IsNullOrWhiteSpace(v) ? new T() : JsonSerializer.Deserialize<T>(v, J) ?? new T(),
            new ValueComparer<T>(
                (a, b) => JsonSerializer.Serialize(a, J) == JsonSerializer.Serialize(b, J),
                v => JsonSerializer.Serialize(v, J).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, J), J)!));
        p.HasColumnType("nvarchar(max)");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Sku).IsUnique();
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.CompareAtPrice).HasPrecision(18, 2);
            e.Property(x => x.CostPrice).HasPrecision(18, 2);
            e.Property(x => x.WeightKg).HasPrecision(9, 3);
            e.Property(x => x.LengthCm).HasPrecision(9, 2);
            e.Property(x => x.BreadthCm).HasPrecision(9, 2);
            e.Property(x => x.HeightCm).HasPrecision(9, 2);
            e.Property(x => x.Status).HasMaxLength(32);
        });

        b.Entity<Coupon>(e =>
        {
            e.ToTable("coupons");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.DiscountValue).HasPrecision(18, 2);
            e.Property(x => x.MinOrderAmount).HasPrecision(18, 2);
            e.Property(x => x.MaxDiscountCap).HasPrecision(18, 2);
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.Phone);
            AsJson(e.Property(x => x.ShippingAddress));
        });

        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.CustomerName).HasMaxLength(200);
            e.Property(x => x.CustomerEmail).HasMaxLength(256);
            e.Property(x => x.CustomerPhone).HasMaxLength(32);
            e.Property(x => x.CustomerId).HasMaxLength(32);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.DiscountAmount).HasPrecision(18, 2);
            e.Property(x => x.ShippingFee).HasPrecision(18, 2);
            e.Property(x => x.TaxAmount).HasPrecision(18, 2);
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.Property(x => x.PaymentMethod).HasMaxLength(16);
            e.Property(x => x.PaymentStatus).HasMaxLength(32);
            e.Property(x => x.FulfillmentStatus).HasMaxLength(32);
            e.Property(x => x.ShiprocketAwb).HasMaxLength(64);
            e.Property(x => x.Courier).HasMaxLength(16);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.CustomerPhone);
            e.HasIndex(x => x.ShiprocketAwb);
            AsJson(e.Property(x => x.ShippingAddress));
            AsJson(e.Property(x => x.Items));
            AsJson(e.Property(x => x.DeliveryTrackingEvents));
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderId).HasMaxLength(32);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.GatewayPaymentId);
        });

        b.Entity<PaymentAttempt>(e =>
        {
            e.ToTable("payment_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderId).HasMaxLength(32);
            e.Property(x => x.TxnId).HasMaxLength(128);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.GatewayStatus).HasMaxLength(64);
            e.Property(x => x.PayuPaymentId).HasMaxLength(64);
            e.Property(x => x.PaymentMode).HasMaxLength(32);
            e.Property(x => x.CustomerName).HasMaxLength(200);
            e.Property(x => x.CustomerEmail).HasMaxLength(256);
            e.Property(x => x.CustomerPhone).HasMaxLength(32);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.TxnId);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<TrustBatch>(e =>
        {
            e.ToTable("trust_batches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.BatchNo).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.BatchNo).IsUnique();
            AsJson(e.Property(x => x.QualityChecks));
            AsJson(e.Property(x => x.Documents));
        });

        b.Entity<SiteSetting>(e =>
        {
            e.ToTable("site_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasColumnType("nvarchar(max)");
        });

        b.Entity<Review>(e =>
        {
            e.ToTable("reviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
        });

        b.Entity<WhatsAppMessage>(e =>
        {
            e.ToTable("whatsapp_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.WaMessageId).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.HasIndex(x => x.WaMessageId);
            e.HasIndex(x => x.CreatedAt);
        });
    }
}
