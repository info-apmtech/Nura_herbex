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
    public string PasswordHash { get; set; } = "";
    public AddressDto ShippingAddress { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Order
{
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
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public int Rating { get; set; } = 5;
    public string? Title { get; set; }
    public string Body { get; set; } = "";
    public bool Verified { get; set; }
    public bool Approved { get; set; } = true;
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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
