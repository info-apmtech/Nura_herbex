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
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<LeadSourceEvent> LeadSourceEvents => Set<LeadSourceEvent>();
    public DbSet<LeadStatusHistory> LeadStatusHistory => Set<LeadStatusHistory>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<JourneyEnrollment> JourneyEnrollments => Set<JourneyEnrollment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Designation> Designations => Set<Designation>();
    public DbSet<WorkTask> WorkTasks => Set<WorkTask>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<TaskChecklistItem> TaskChecklistItems => Set<TaskChecklistItem>();
    public DbSet<TaskActivity> TaskActivities => Set<TaskActivity>();
    public DbSet<LeadAssignmentHistory> LeadAssignmentHistory => Set<LeadAssignmentHistory>();

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
            e.Property(x => x.ParcelWeightKg).HasPrecision(9, 3);
            e.Property(x => x.ParcelLengthCm).HasPrecision(9, 2);
            e.Property(x => x.ParcelBreadthCm).HasPrecision(9, 2);
            e.Property(x => x.ParcelHeightCm).HasPrecision(9, 2);
            e.Property(x => x.ShipmentClientOrderId).HasMaxLength(100);
            e.HasIndex(x => x.ShipmentClientOrderId).IsUnique().HasFilter("[ShipmentClientOrderId] IS NOT NULL");
            e.HasIndex(x => new { x.Courier, x.ShiprocketAwb }).IsUnique().HasFilter("[Courier] = 'Shadowfax' AND [ShiprocketAwb] IS NOT NULL");
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
            e.Property(x => x.CustomerId).HasMaxLength(32);
            e.HasIndex(x => x.CustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<WhatsAppMessage>(e =>
        {
            e.ToTable("whatsapp_messages");
            e.Property(x => x.MediaId).HasMaxLength(200);
            e.Property(x => x.MediaMime).HasMaxLength(100);
            e.Property(x => x.MediaFile).HasMaxLength(200);
            e.Property(x => x.MediaName).HasMaxLength(260);
            e.Property(x => x.Reaction).HasMaxLength(16);
            e.Property(x => x.SentBy).HasMaxLength(200);
            e.HasKey(x => x.Id);
            e.Property(x => x.WaMessageId).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.HasIndex(x => x.WaMessageId);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.LeadId);
        });

        b.Entity<Lead>(e =>
        {
            e.ToTable("leads");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.SourceDetail).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.ConsentStatus).HasMaxLength(32);
            e.Property(x => x.ConsentSource).HasMaxLength(64);
            e.Property(x => x.CustomerId).HasMaxLength(64);
            e.HasIndex(x => x.Phone).IsUnique();
            e.HasIndex(x => new { x.Status, x.LastContactAt });
            e.HasIndex(x => x.AssignedToEmployeeId);
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LeadId).IsUnique();
        });

        b.Entity<LeadSourceEvent>(e =>
        {
            e.ToTable("lead_source_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasMaxLength(32);
            e.HasIndex(x => x.LeadId);
        });

        b.Entity<LeadStatusHistory>(e =>
        {
            e.ToTable("lead_status_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.FromStatus).HasMaxLength(32);
            e.Property(x => x.ToStatus).HasMaxLength(32);
            e.Property(x => x.ChangedBy).HasMaxLength(200);
            e.HasIndex(x => x.LeadId);
        });

        b.Entity<Employee>(e =>
        {
            e.ToTable("employees");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.PasswordHash).HasMaxLength(400);
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.DesignationId);
        });

        b.Entity<LeadAssignmentHistory>(e =>
        {
            e.ToTable("lead_assignment_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.ChangedBy).HasMaxLength(200);
            e.HasIndex(x => x.LeadId);
        });

        b.Entity<FollowUp>(e =>
        {
            e.ToTable("follow_ups");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderId).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Outcome).HasMaxLength(500);
            e.Property(x => x.CompletedBy).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.StepKey).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.AssignedToEmployeeId, x.Status, x.DueAt });
            e.HasIndex(x => x.LeadId);
            // One automatic task per order step, even if two servers run the worker at once.
            e.HasIndex(x => new { x.OrderId, x.StepKey }).IsUnique().HasFilter("[OrderId] IS NOT NULL AND [StepKey] IS NOT NULL");
        });

        b.Entity<JourneyEnrollment>(e =>
        {
            e.ToTable("journey_enrollments");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderId).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.StoppedReason).HasMaxLength(300);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => x.LeadId);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Link).HasMaxLength(200);
            e.HasIndex(x => new { x.EmployeeId, x.ReadAt, x.CreatedAt });
        });

        b.Entity<Department>(e =>
        {
            e.ToTable("departments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Designation>(e =>
        {
            e.ToTable("designations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.RightsJson).HasColumnType("nvarchar(max)");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<WorkTask>(e =>
        {
            e.ToTable("work_tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Priority).HasMaxLength(16);
            e.Property(x => x.CreatedByName).HasMaxLength(200);
            e.Property(x => x.OrderId).HasMaxLength(64);
            e.Property(x => x.Tags).HasMaxLength(300);
            e.Property(x => x.Repeat).HasMaxLength(16);
            e.HasIndex(x => new { x.AssignedToEmployeeId, x.Status, x.DueAt });
            e.HasIndex(x => x.LeadId);
            e.HasIndex(x => x.DepartmentId);
        });

        b.Entity<TaskComment>(e =>
        {
            e.ToTable("task_comments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Author).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(4000);
            e.HasIndex(x => x.TaskId);
        });

        b.Entity<TaskChecklistItem>(e =>
        {
            e.ToTable("task_checklist");
            e.HasKey(x => x.Id);
            e.Property(x => x.Text).HasMaxLength(300);
            e.HasIndex(x => x.TaskId);
        });

        b.Entity<TaskActivity>(e =>
        {
            e.ToTable("task_activity");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Text).HasMaxLength(500);
            e.HasIndex(x => x.TaskId);
        });
    }
}
