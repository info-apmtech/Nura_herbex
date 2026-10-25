using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Data;

public class Product
{
    public string Id { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public decimal CostPrice { get; set; }
    public int StockQuantity { get; set; } = 100;
    public decimal WeightKg { get; set; }
    public decimal LengthCm { get; set; }
    public decimal BreadthCm { get; set; }
    public decimal HeightCm { get; set; }
    public string HsnCode { get; set; } = "21069099";
    public string? FssaiLicense { get; set; } = "11223999000181";
    public string? Category { get; set; }
    /// <summary>Short brand name used in headings (e.g. "GUT 365").</summary>
    public string? ShortName { get; set; }
    /// <summary>Small label on cards, e.g. "Bestseller".</summary>
    public string? Badge { get; set; }
    /// <summary>Key benefits, one per line.</summary>
    public string? Highlights { get; set; }
    public string? Ingredients { get; set; }
    public string? Directions { get; set; }
    /// <summary>Pack / serving line, e.g. "Daily powder · 30-day supply".</summary>
    public string? ServingInfo { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Coupon
{
    public string Code { get; set; } = "";
    public string DiscountType { get; set; } = "PERCENTAGE";
    public decimal DiscountValue { get; set; }
    public decimal MinOrderAmount { get; set; }
    public decimal MaxDiscountCap { get; set; } = 1000;
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Customer
{
    public string Id { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? ProfileImageData { get; set; }
    public string PasswordHash { get; set; } = "";
    public AddressDto ShippingAddress { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Order
{
    public decimal ParcelWeightKg { get; set; }
    public decimal ParcelLengthCm { get; set; }
    public decimal ParcelBreadthCm { get; set; }
    public decimal ParcelHeightCm { get; set; }
    public DateTime? FulfillmentReadyAt { get; set; }
    public string? ShipmentClientOrderId { get; set; }
    public DateTime? ShipmentBookedAt { get; set; }
    public DateTime? PickedUpAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? LastShippingEventAt { get; set; }
    public DateTime? LastTrackingCheckedAt { get; set; }
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string? CustomerId { get; set; }
    public AddressDto ShippingAddress { get; set; } = new();
    public List<OrderItemDto> Items { get; set; } = new();
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string? CouponCode { get; set; }
    public string PaymentMethod { get; set; } = "COD";
    public string PaymentStatus { get; set; } = "PENDING";
    public string? PaymentRef { get; set; }
    public string? PayuTxnId { get; set; }
    public string FulfillmentStatus { get; set; } = "PENDING";
    /// <summary>Courier platform that booked this shipment: Shiprocket | Shadowfax (null = Shiprocket, pre-dates multi-courier). The Shiprocket* fields hold that platform's ids.</summary>
    public string? Courier { get; set; }
    public string? ShiprocketOrderId { get; set; }
    public string? ShiprocketShipmentId { get; set; }
    public string? ShiprocketAwb { get; set; }
    public string? ShiprocketCourier { get; set; }
    public string? ShippingLabelUrl { get; set; }
    public string DeliveryStatus { get; set; } = "Order Placed";
    public List<TrackingEventDto> DeliveryTrackingEvents { get; set; } = new();
    public string? Notes { get; set; }
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderId { get; set; } = "";
    public string Provider { get; set; } = "PAYU";
    public string? GatewayOrderId { get; set; }
    public string? GatewayPaymentId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string Status { get; set; } = "CAPTURED";
    public string? GatewayResponse { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One PayU checkout attempt. Online orders are NOT written to <see cref="Order"/> until PayU confirms payment —
/// the order is held here as a snapshot and every failed / cancelled attempt is kept as a failure record.
/// Status: CREATED (order held, not yet sent to PayU) | INITIATED | SUCCESS | FAILED | CANCELLED
/// </summary>
public class PaymentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderId { get; set; } = "";
    public string? TxnId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "CREATED";
    public string? FailureReason { get; set; }
    public string? GatewayStatus { get; set; }
    public string? PayuPaymentId { get; set; }
    public string? PaymentMode { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string OrderSnapshotJson { get; set; } = "";
    public string? GatewayResponse { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class TrustBatch
{
    public string Id { get; set; } = "";
    public string BatchNo { get; set; } = "";
    public string ProductName { get; set; } = "STAMIX Vitality Mix 300G";
    public string PackSize { get; set; } = "300 g | 30 Servings";
    public string MfgDate { get; set; } = "";
    public string ExpDate { get; set; } = "";
    public string Status { get; set; } = "Active / Verified";
    public string? DocumentName { get; set; }
    public string? DocumentUrl { get; set; }
    public string? DocumentType { get; set; }
    public string? FileSize { get; set; }
    public string? LabName { get; set; }
    public string? Notes { get; set; }
    public List<QualityCheckDto> QualityChecks { get; set; } = new();
    public List<BatchDocumentDto> Documents { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SiteSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Review
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? CustomerId { get; set; }
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public int Rating { get; set; } = 5;
    public string? Title { get; set; }
    public string Body { get; set; } = "";
    public bool Verified { get; set; }
    public bool Approved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class WhatsAppMessage
{
    public long Id { get; set; }
    public string Direction { get; set; } = "inbound";
    public string WaMessageId { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? ProfileName { get; set; }
    public string Type { get; set; } = "text";
    public string? Body { get; set; }
    public string Status { get; set; } = "received";
    public string? OrderId { get; set; }
    public string? RawPayload { get; set; }
    public long? LeadId { get; set; }
    /// <summary>Employee who sent an outbound message; null/"system" for automatic ones (auto-reply, order updates, journey).</summary>
    public string? SentBy { get; set; }
    /// <summary>Meta media id of a photo / sticker / video / voice note / document the customer sent (expires on Meta after about 30 days).</summary>
    public string? MediaId { get; set; }
    public string? MediaMime { get; set; }
    /// <summary>File name inside the media folder once downloaded.</summary>
    public string? MediaFile { get; set; }
    /// <summary>Original file name of a document.</summary>
    public string? MediaName { get; set; }
    /// <summary>Emoji the other side reacted with, if any.</summary>
    public string? Reaction { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A sales enquiry captured from WhatsApp (and, in later phases, Instagram / Facebook / website). One lead per normalised phone number.</summary>
public class Lead
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>E.164 digits without '+', e.g. 919876543210. Unique.</summary>
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>whatsapp | instagram | facebook | website | manual</summary>
    public string Source { get; set; } = "whatsapp";
    /// <summary>Campaign / ad headline / form name that brought the lead in.</summary>
    public string? SourceDetail { get; set; }
    /// <summary>new | interested | follow-up | converted | not-interested</summary>
    public string Status { get; set; } = "new";
    /// <summary>unknown | opted_in | opted_out</summary>
    public string ConsentStatus { get; set; } = "unknown";
    public DateTime? ConsentAt { get; set; }
    public string? ConsentSource { get; set; }
    public string? FirstMessage { get; set; }
    public string? Notes { get; set; }
    public string? CustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastContactAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConvertedAt { get; set; }
    /// <summary>Owning sales employee (null = unassigned).</summary>
    public long? AssignedToEmployeeId { get; set; }
    public DateTime? AssignedAt { get; set; }
}

/// <summary>Chat state for a lead: unread count and the 24h WhatsApp customer-service window.</summary>
public class Conversation
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public DateTime? LastInboundAt { get; set; }
    public DateTime? LastOutboundAt { get; set; }
    public int UnreadCount { get; set; }
    /// <summary>Last inbound + 24h: free-form replies are allowed only until then; afterwards approved templates only.</summary>
    public DateTime? ServiceWindowExpiresAt { get; set; }
}

/// <summary>Every time a known lead reaches out again through a source (keeps attribution without duplicating the lead).</summary>
public class LeadSourceEvent
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public string Source { get; set; } = "whatsapp";
    public string? Detail { get; set; }
    public string? ReferralJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class LeadStatusHistory
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string? Note { get; set; }
    public string ChangedBy { get; set; } = "system";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A CRM user with their own login. Role "admin" sees everything, "sales" sees only the leads assigned to them.</summary>
public class Employee
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lower-case, unique; used to sign in.</summary>
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>pbkdf2-sha512:iterations:salt:hash</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>admin | sales</summary>
    public string Role { get; set; } = "sales";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Last time the portal checked in; recent means online. Cleared on sign-out.</summary>
    public DateTime? LastSeenAt { get; set; }
    /// <summary>Round-robin cursor: the employee who was assigned a lead longest ago gets the next one.</summary>
    public DateTime? LastAssignedAt { get; set; }
    public long? DepartmentId { get; set; }
    /// <summary>Gives the page rights. Administrators (Role admin) ignore it and can open every page.</summary>
    public long? DesignationId { get; set; }
}

public class LeadAssignmentHistory
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public long? FromEmployeeId { get; set; }
    public long? ToEmployeeId { get; set; }
    public string? Reason { get; set; }
    /// <summary>auto | admin name/email of whoever made the change.</summary>
    public string ChangedBy { get; set; } = "auto";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A task to contact a lead/customer: manual, from the 15-day journey, or raised by order progress checks.</summary>
public class FollowUp
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public string? OrderId { get; set; }
    public long? AssignedToEmployeeId { get; set; }
    /// <summary>call | whatsapp | review | reorder | order | other</summary>
    public string Type { get; set; } = "call";
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    /// <summary>Ready-to-send WhatsApp text (placeholders already filled) for message-type tasks.</summary>
    public string? SuggestedMessage { get; set; }
    public DateTime DueAt { get; set; }
    /// <summary>When the owner gets an advance heads-up (normally 2 days before DueAt).</summary>
    public DateTime? RemindAt { get; set; }
    public DateTime? ReminderSentAt { get; set; }
    public DateTime? DueNotifiedAt { get; set; }
    public DateTime? EscalatedAt { get; set; }
    /// <summary>pending | done | skipped</summary>
    public string Status { get; set; } = "pending";
    public string? Outcome { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    /// <summary>manual | journey | system</summary>
    public string Source { get; set; } = "manual";
    public long? EnrollmentId { get; set; }
    /// <summary>Journey step or order-check key, e.g. day3-usage. With OrderId it is unique, so a step is created only once.</summary>
    public string? StepKey { get; set; }
    public string CreatedBy { get; set; } = "system";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A customer's 15-day journey, started when their order is delivered.</summary>
public class JourneyEnrollment
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public string OrderId { get; set; } = "";
    /// <summary>Delivery time; every step is scheduled relative to it.</summary>
    public DateTime AnchorAt { get; set; }
    /// <summary>active | completed | stopped</summary>
    public string Status { get; set; } = "active";
    public string? StoppedReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>In-app notification. EmployeeId null = every administrator.</summary>
public class Notification
{
    public long Id { get; set; }
    public long? EmployeeId { get; set; }
    /// <summary>reminder | due | overdue | order | lead | info</summary>
    public string Kind { get; set; } = "info";
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    /// <summary>Admin route to open, e.g. /admin/followups.</summary>
    public string? Link { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public class Department
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? HeadEmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A job title that carries page rights, e.g. Sales Executive.</summary>
public class Designation
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? DepartmentId { get; set; }
    public int Level { get; set; } = 3;
    public bool IsActive { get; set; } = true;
    /// <summary>JSON object: page key to access level 0-2 (see PageCatalog).</summary>
    public string RightsJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A piece of team work (not tied to a customer follow-up). Named WorkTask to avoid clashing with System.Threading.Tasks.Task.</summary>
public class WorkTask
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>todo | in_progress | blocked | done</summary>
    public string Status { get; set; } = "todo";
    /// <summary>low | normal | high | urgent</summary>
    public string Priority { get; set; } = "normal";
    public DateTime? DueAt { get; set; }
    public long? AssignedToEmployeeId { get; set; }
    public long? CreatedByEmployeeId { get; set; }
    public string CreatedByName { get; set; } = "";
    public long? DepartmentId { get; set; }
    public long? LeadId { get; set; }
    public string? OrderId { get; set; }
    public string? Tags { get; set; }
    /// <summary>none | daily | weekly | monthly: finishing the task creates the next one.</summary>
    public string Repeat { get; set; } = "none";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? ReminderSentAt { get; set; }
    public DateTime? OverdueNotifiedAt { get; set; }
}

public class TaskComment
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public long? AuthorEmployeeId { get; set; }
    public string Author { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TaskChecklistItem
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Position { get; set; }
}

public class TaskActivity
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string Actor { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
