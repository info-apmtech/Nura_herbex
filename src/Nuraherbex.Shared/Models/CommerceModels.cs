using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nuraherbex.Shared.Models;

/// <summary>Standard JSON options shared by the API, web and mobile clients (camelCase, case-insensitive).</summary>
public static class NuraJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

// ---------------------------------------------------------------------------
// Generic envelope
// ---------------------------------------------------------------------------
public class ApiResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
}

public class ApiResult<T> : ApiResult
{
    public T? Data { get; set; }
}

// ---------------------------------------------------------------------------
// Catalog
// ---------------------------------------------------------------------------
public enum ProductStatus
{
    Active = 1,
    Draft = 2,
    Archived = 3,
}

public static class ProductStatuses
{
    public const string Active = "active";
    public const string Draft = "draft";
    public const string Archived = "archived";
    public static readonly string[] All = [Active, Draft, Archived];

    public static string ToCode(this ProductStatus status) => status switch
    {
        ProductStatus.Draft => Draft,
        ProductStatus.Archived => Archived,
        _ => Active,
    };

    public static bool TryParse(string? value, out ProductStatus status)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Active: status = ProductStatus.Active; return true;
            case Draft: status = ProductStatus.Draft; return true;
            case Archived: status = ProductStatus.Archived; return true;
            default: status = default; return false;
        }
    }
}

public class ProductDto
{
    public string Id { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public int StockQuantity { get; set; }
    public decimal WeightKg { get; set; }
    public decimal LengthCm { get; set; }
    public decimal BreadthCm { get; set; }
    public decimal HeightCm { get; set; }
    public string HsnCode { get; set; } = "21069099";
    public string ImageUrl { get; set; } = "";
    public string Status { get; set; } = "active";
    public string? Category { get; set; }
    public string? ShortName { get; set; }
    public string? Badge { get; set; }
    /// <summary>Key benefits, one per line.</summary>
    public string? Highlights { get; set; }
    public string? Ingredients { get; set; }
    public string? Directions { get; set; }
    public string? ServingInfo { get; set; }
    public IEnumerable<string> HighlightList => (Highlights ?? "").Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class ProductListResponse : ApiResult
{
    public List<ProductDto> Products { get; set; } = new();
}

public enum CouponDiscountType
{
    Percentage = 1,
    Fixed = 2,
}

public static class CouponDiscountTypes
{
    public const string Percentage = "PERCENTAGE";
    public const string Fixed = "FIXED";

    public static string ToCode(this CouponDiscountType type) => type switch
    {
        CouponDiscountType.Fixed => Fixed,
        _ => Percentage,
    };

    public static bool TryParse(string? value, out CouponDiscountType type)
    {
        switch (value)
        {
            case Percentage: type = CouponDiscountType.Percentage; return true;
            case Fixed: type = CouponDiscountType.Fixed; return true;
            default: type = default; return false;
        }
    }
}

public class CouponDto
{
    public string Code { get; set; } = "";
    public string DiscountType { get; set; } = CouponDiscountTypes.Percentage;
    public decimal DiscountValue { get; set; }
    public decimal MinOrderAmount { get; set; }
    public decimal MaxDiscountCap { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
}

// ---------------------------------------------------------------------------
// Cart / totals
// ---------------------------------------------------------------------------
public class CartLineDto
{
    public string Id { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

public class ValidateCartRequest
{
    public List<CartLineDto> Items { get; set; } = new();
    public string? CouponCode { get; set; }
}

public class OrderItemDto
{
    public string Id { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public decimal Total { get; set; }
    public string? Hsn { get; set; }
    public decimal WeightKg { get; set; }
    public string? Image { get; set; }
}

public class OrderTotalsDto : ApiResult
{
    public List<OrderItemDto> Items { get; set; } = new();
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? CouponCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal FinalTotal { get; set; }
}

// ---------------------------------------------------------------------------
// Orders
// ---------------------------------------------------------------------------
[JsonConverter(typeof(OrderPaymentMethodJsonConverter))]
public enum OrderPaymentMethod
{
    Cod = 1,
    Online = 2,
}

public static class OrderPaymentMethods
{
    public const string Cod = "COD";
    public const string Online = "ONLINE";
    public static readonly string[] All = [Cod, Online];

    public static string ToCode(this OrderPaymentMethod method) => method switch
    {
        OrderPaymentMethod.Online => Online,
        _ => Cod,
    };

    public static bool TryParse(string? value, out OrderPaymentMethod method)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case Cod: method = OrderPaymentMethod.Cod; return true;
            case Online: method = OrderPaymentMethod.Online; return true;
            default: method = default; return false;
        }
    }
}

public sealed class OrderPaymentMethodJsonConverter : JsonConverter<OrderPaymentMethod>
{
    public override OrderPaymentMethod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (OrderPaymentMethods.TryParse(s, out var parsed) || Enum.TryParse(s, ignoreCase: true, out parsed) && Enum.IsDefined(parsed))
                return parsed;
            throw new JsonException($"Unknown order payment method '{s}'.");
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var raw) && Enum.IsDefined(typeof(OrderPaymentMethod), raw))
            return (OrderPaymentMethod)raw;
        throw new JsonException($"Invalid token '{reader.TokenType}' for {nameof(OrderPaymentMethod)}.");
    }

    public override void Write(Utf8JsonWriter writer, OrderPaymentMethod value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToCode());
}

[JsonConverter(typeof(OrderPaymentStatusJsonConverter))]
public enum OrderPaymentStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Refunded = 4,
    CodPending = 5,
    CodCollected = 6,
    RefundPending = 7,
    Cancelled = 8,
}

public static class OrderPaymentStatuses
{
    public const string Pending = "PENDING";
    public const string Paid = "PAID";
    public const string Failed = "FAILED";
    public const string Refunded = "REFUNDED";
    public const string CodPending = "COD_PENDING";
    public const string CodCollected = "COD_COLLECTED";
    public const string RefundPending = "REFUND_PENDING";
    public const string Cancelled = "CANCELLED";
    public static readonly string[] All = [Pending, Paid, Failed, Refunded, CodPending, CodCollected, RefundPending, Cancelled];

    public static string ToCode(this OrderPaymentStatus status) => status switch
    {
        OrderPaymentStatus.Paid => Paid,
        OrderPaymentStatus.Failed => Failed,
        OrderPaymentStatus.Refunded => Refunded,
        OrderPaymentStatus.CodPending => CodPending,
        OrderPaymentStatus.CodCollected => CodCollected,
        OrderPaymentStatus.RefundPending => RefundPending,
        OrderPaymentStatus.Cancelled => Cancelled,
        _ => Pending,
    };

    public static bool TryParse(string? value, out OrderPaymentStatus status)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case Pending: status = OrderPaymentStatus.Pending; return true;
            case Paid: status = OrderPaymentStatus.Paid; return true;
            case Failed: status = OrderPaymentStatus.Failed; return true;
            case Refunded: status = OrderPaymentStatus.Refunded; return true;
            case CodPending:
            case "COD-PENDING": status = OrderPaymentStatus.CodPending; return true;
            case CodCollected:
            case "COD-COLLECTED": status = OrderPaymentStatus.CodCollected; return true;
            case RefundPending:
            case "REFUND-PENDING": status = OrderPaymentStatus.RefundPending; return true;
            case Cancelled:
            case "CANCELED": status = OrderPaymentStatus.Cancelled; return true;
            default: status = default; return false;
        }
    }
}

public sealed class OrderPaymentStatusJsonConverter : JsonConverter<OrderPaymentStatus>
{
    public override OrderPaymentStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (OrderPaymentStatuses.TryParse(s, out var parsed) || Enum.TryParse(s, ignoreCase: true, out parsed) && Enum.IsDefined(parsed))
                return parsed;
            throw new JsonException($"Unknown order payment status '{s}'.");
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var raw) && Enum.IsDefined(typeof(OrderPaymentStatus), raw))
            return (OrderPaymentStatus)raw;
        throw new JsonException($"Invalid token '{reader.TokenType}' for {nameof(OrderPaymentStatus)}.");
    }

    public override void Write(Utf8JsonWriter writer, OrderPaymentStatus value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToCode());
}

public class AddressDto
{
    public string AddressLine1 { get; set; } = "";
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Pincode { get; set; } = "";
    public string Country { get; set; } = "India";
}

public class TrackingEventDto
{
    public string? EventKey { get; set; }
    public string Status { get; set; } = "";
    public string? Location { get; set; }
    public string? Time { get; set; }
    public bool Done { get; set; } = true;
    public bool Active { get; set; }
}

public class OrderDto
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
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

    /// <summary>COD | ONLINE</summary>
    public string PaymentMethod { get; set; } = "COD";
    /// <summary>PENDING | PAID | FAILED | REFUNDED | COD_PENDING | COD_COLLECTED</summary>
    public string PaymentStatus { get; set; } = "PENDING";
    public string? PaymentRef { get; set; }
    public string? PayuTxnId { get; set; }

    /// <summary>PENDING | SHIPMENT_CREATED | AWB_ASSIGNED | PICKED_UP | IN_TRANSIT | OUT_FOR_DELIVERY | DELIVERED | RTO | CANCELLED</summary>
    public string FulfillmentStatus { get; set; } = "PENDING";
    /// <summary>Shiprocket | Shadowfax (null = Shiprocket). The Shiprocket* fields hold that courier platform's ids.</summary>
    public string? Courier { get; set; }
    public string? ShiprocketOrderId { get; set; }
    public string? ShiprocketShipmentId { get; set; }
    public string? ShiprocketAwb { get; set; }
    public string? ShiprocketCourier { get; set; }
    /// <summary>Admin-only printable label URL; customer DTO mappings deliberately leave this empty.</summary>
    public string? ShippingLabelUrl { get; set; }
    public string DeliveryStatus { get; set; } = "Order Placed";
    public List<TrackingEventDto> DeliveryTrackingEvents { get; set; } = new();
    public string? Notes { get; set; }

    // Enrichment (populated by /orders/track and /auth/my-orders)
    public string? ShiprocketTrackUrl { get; set; }
    public JsonElement? ShiprocketTracking { get; set; }
}

public class CheckoutCustomerDto
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>Optional. When 6+ chars a Vitality account is created/signed-in during checkout.</summary>
    public string? Password { get; set; }
    public string? Id { get; set; }
}

public class CreateOrderRequest
{
    public CheckoutCustomerDto Customer { get; set; } = new();
    public AddressDto Shipping { get; set; } = new();
    public List<CartLineDto> Items { get; set; } = new();
    public string? CouponCode { get; set; }
    /// <summary>COD | ONLINE</summary>
    public string PaymentMethod { get; set; } = "COD";
}

public class CreateOrderResponse : ApiResult
{
    public string OrderId { get; set; } = "";
    public OrderDto? Order { get; set; }
    public AuthResponse? CustomerAuth { get; set; }
}

public class OrderResponse : ApiResult
{
    public OrderDto? Order { get; set; }
}

public class PaymentAttemptDto
{
    public string OrderId { get; set; } = "";
    public string? TxnId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>CREATED | INITIATED | SUCCESS | FAILED | CANCELLED</summary>
    public string Status { get; set; } = "";
    public string? FailureReason { get; set; }
    public string? PayuPaymentId { get; set; }
    public string? PaymentMode { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PaymentAttemptListResponse : ApiResult
{
    public int Count { get; set; }
    public List<PaymentAttemptDto> Attempts { get; set; } = new();
}

public class OrderListResponse : ApiResult
{
    public int Count { get; set; }
    public List<OrderDto> Orders { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Payments (PayU)
// ---------------------------------------------------------------------------
public class PayUInitiateRequest
{
    public string OrderId { get; set; } = "";
}

public class PayUInitiateResponse : ApiResult
{
    public bool Simulated { get; set; }
    public string ActionUrl { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new();
    public string OrderId { get; set; } = "";
    public string Amount { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Customer accounts
// ---------------------------------------------------------------------------
public class CustomerDto
{
    public string Id { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? ProfileImageData { get; set; }
    public AddressDto ShippingAddress { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AdminCustomerDto : CustomerDto
{
    public int OrdersCount { get; set; }
    public decimal TotalSpent { get; set; }
    public DateTime? LastOrderDate { get; set; }
    public string? LastOrderId { get; set; }
    public List<OrderDto> Orders { get; set; } = new();
}

public class AdminCustomerListResponse : ApiResult
{
    public int Count { get; set; }
    public List<AdminCustomerDto> Customers { get; set; } = new();
}

public class RegisterRequest
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Password { get; set; } = "";
    public AddressDto? ShippingAddress { get; set; }
}

public class LoginRequest
{
    public string EmailOrPhone { get; set; } = "";
    public string Password { get; set; } = "";
}

public class SendOtpRequest
{
    public string Email { get; set; } = "";
}

public class VerifyOtpRequest
{
    public string Email { get; set; } = "";
    public string Otp { get; set; } = "";
}

public class UpdateProfileRequest
{
    public string? FullName { get; set; }
    /// <summary>Optional. When present and different it replaces the sign-in email and is carried over to the customer's existing orders.</summary>
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? ProfileImageData { get; set; }
    public AddressDto? ShippingAddress { get; set; }
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public class AuthResponse : ApiResult
{
    public CustomerDto? Customer { get; set; }
    public string? Token { get; set; }
    /// <summary>Echoed back by send-otp.</summary>
    public string? Email { get; set; }
}

public class CustomerResponse : ApiResult
{
    public CustomerDto? Customer { get; set; }
}

/// <summary>Admin "Add Customer" dialog: name and phone are required; email and shipping address are optional.</summary>
public class AdminCreateCustomerRequest
{
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public AddressDto? ShippingAddress { get; set; }
}

public class PincodeCheckResponse
{
    public bool Success { get; set; }
    /// <summary>null = unknown (courier check unavailable); never block checkout on null.</summary>
    public bool? Serviceable { get; set; }
}

public class CheckUserResponse
{
    public bool Exists { get; set; }
    public string FullName { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Admin auth
// ---------------------------------------------------------------------------
public class AdminLoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class AdminUserDto
{
    public string Email { get; set; } = "";
    public string Role { get; set; } = "admin";
    public string Name { get; set; } = "";
    /// <summary>Set for employee logins; null for the configured super administrator.</summary>
    public long? EmployeeId { get; set; }
    /// <summary>Page key to access level (see PageCatalog). The administrator has full access to every page.</summary>
    public Dictionary<string, int> Rights { get; set; } = new();
    public string? Department { get; set; }
    public string? Designation { get; set; }
}

public class AdminLoginResponse : ApiResult
{
    public string? Token { get; set; }
    public AdminUserDto? User { get; set; }
    public bool Valid { get; set; }
}

public class AdminEmailRequest
{
    /// <summary>confirmation | delivered | shipped | out-for-delivery | cancelled | returned</summary>
    public string Type { get; set; } = "confirmation";
}

/// <summary>Admin "Add Order" dialog: server prices the line from the catalog.</summary>
public class AdminCreateOrderRequest
{
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public AddressDto Address { get; set; } = new();
    public string ProductId { get; set; } = "stamix-single";
    public string? CouponCode { get; set; }
    public int Quantity { get; set; } = 1;
    /// <summary>COD | ONLINE</summary>
    public string PaymentMethod { get; set; } = "ONLINE";
    public bool MarkPaid { get; set; } = true;
}

public class ShipmentResultDto
{
    public bool Success { get; set; }
    public bool Simulated { get; set; }
    public string? Courier { get; set; }
    public string? ShiprocketOrderId { get; set; }
    public string? ShiprocketShipmentId { get; set; }
    public string? ShiprocketAwb { get; set; }
    public string? ShiprocketCourier { get; set; }
    public string? DeliveryStatus { get; set; }
    public string? ShippingLabelUrl { get; set; }
}

public class ShipmentResponse : ApiResult
{
    public ShipmentResultDto? Result { get; set; }
}

// ---------------------------------------------------------------------------
// Product quality and batch laboratory reports
// ---------------------------------------------------------------------------
public class QualityCheckDto
{
    public string Label { get; set; } = "";
    public string Status { get; set; } = "";
}

public class BatchDocumentDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Desc { get; set; }
    public string? ActionText { get; set; }
    public string? Tag { get; set; }
    public string? Details { get; set; }
    /// <summary>"document" (default), "product" (certification for this product) or "brand" (manufacturing / brand certification).</summary>
    public string Kind { get; set; } = "document";
    /// <summary>For certifications: until when it is valid, as written on the certificate (for example "Dec 2026").</summary>
    public string? ValidTill { get; set; }
    /// <summary>For certifications: link to the certificate file or page.</summary>
    public string? Url { get; set; }
}

public class TrustBatchDto
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TrustBatchListResponse : ApiResult
{
    public List<TrustBatchDto> Batches { get; set; } = new();
}

public class TrustBatchResponse : ApiResult
{
    public TrustBatchDto? Batch { get; set; }
}

// ---------------------------------------------------------------------------
// Formulation / site content (stored as JSON in site_settings)
// ---------------------------------------------------------------------------
public class FormulationIngredientDto
{
    public string Id { get; set; } = "";
    /// <summary>Product this ingredient belongs to. Empty on legacy rows, which belong to the first product (see <see cref="FormulationScope"/>).</summary>
    public string? ProductId { get; set; }
    public string Name { get; set; } = "";
    public string BotanicalName { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Category { get; set; } = "";
    public string Benefit { get; set; } = "";
    public string? ExtractRatio { get; set; }
    public string? Purpose { get; set; }
    public string? ActiveCompound { get; set; }
}

/// <summary>Resolves which product a formulation row belongs to; unassigned legacy rows fall to the default (first) product.</summary>
public static class FormulationScope
{
    public static bool Belongs(FormulationIngredientDto i, string productId, string? defaultProductId) =>
        string.IsNullOrEmpty(i.ProductId) ? productId == defaultProductId : i.ProductId == productId;
}

public class FormulationMetricDto
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public bool UseAutoCount { get; set; }
}

public class FormulationMetricsDto
{
    public FormulationMetricDto TotalIngredients { get; set; } = new() { Label = "Total Ingredients", Value = "17 Botanicals" };
    public FormulationMetricDto DailyServing { get; set; } = new() { Label = "Daily Serving Base", Value = "20,000 mg (20g)" };
    public FormulationMetricDto Bioavailability { get; set; } = new() { Label = "Bioavailability Catalyst", Value = "BioPerine® +2000%" };
    public FormulationMetricDto Transparency { get; set; } = new() { Label = "Public Transparency", Value = "100% Unmasked" };
}

// ---------------------------------------------------------------------------
// Reviews
// ---------------------------------------------------------------------------
public class ReviewDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public int Rating { get; set; } = 5;
    public string? Title { get; set; }
    public string Body { get; set; } = "";
    public bool Verified { get; set; }
    public bool Approved { get; set; }
    public string? ProfileImageData { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ReviewModerationRequest
{
    public bool Approved { get; set; }
}

public class ReviewListResponse : ApiResult
{
    public List<ReviewDto> Reviews { get; set; } = new();
}

// ---------------------------------------------------------------------------
// WhatsApp (admin view)
// ---------------------------------------------------------------------------
public class WhatsAppMessageDto
{
    public long Id { get; set; }
    /// <summary>inbound | outbound</summary>
    public string Direction { get; set; } = "inbound";
    public string WaMessageId { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? ProfileName { get; set; }
    public string Type { get; set; } = "text";
    public string? Body { get; set; }
    /// <summary>received | sent | delivered | read | failed</summary>
    public string Status { get; set; } = "received";
    public string? OrderId { get; set; }
    /// <summary>Employee name for messages sent by a person; empty for automatic ones.</summary>
    public string? SentBy { get; set; }
    /// <summary>Signed link (relative to the API) to the photo / sticker / video / audio / document. Null for text.</summary>
    public string? MediaUrl { get; set; }
    public string? MediaMime { get; set; }
    public string? MediaName { get; set; }
    /// <summary>Emoji reaction left on this message.</summary>
    public string? Reaction { get; set; }
    /// <summary>Why WhatsApp refused or could not deliver this message, in plain words. Only set when it failed.</summary>
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class WhatsAppMessageListResponse : ApiResult
{
    public List<WhatsAppMessageDto> Messages { get; set; } = new();
}

public class WhatsAppSendRequest
{
    public string Phone { get; set; } = "";
    public string Text { get; set; } = "";
    public string? OrderId { get; set; }
}

public class WhatsAppStatusDto : ApiResult
{
    public bool Configured { get; set; }
    public string? WebhookUrlHint { get; set; }
    public int InboundCount { get; set; }
    public int OutboundCount { get; set; }
}
