using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public static class Mapping
{
    public static ProductDto ToDto(this Product p) => new()
    {
        Id = p.Id, Sku = p.Sku, Name = p.Name, Subtitle = p.Subtitle, Description = p.Description,
        Price = p.Price, CompareAtPrice = p.CompareAtPrice, StockQuantity = p.StockQuantity,
        WeightKg = p.WeightKg, LengthCm = p.LengthCm, BreadthCm = p.BreadthCm, HeightCm = p.HeightCm,
        HsnCode = p.HsnCode, ImageUrl = p.ImageUrl, Status = p.Status,
    };

    public static OrderDto ToDto(this Order o) => new()
    {
        Id = o.Id, CreatedAt = o.CreatedAt, UpdatedAt = o.UpdatedAt,
        CustomerName = o.CustomerName, CustomerEmail = o.CustomerEmail, CustomerPhone = o.CustomerPhone, CustomerId = o.CustomerId,
        ShippingAddress = o.ShippingAddress, Items = o.Items,
        Subtotal = o.Subtotal, DiscountAmount = o.DiscountAmount, ShippingFee = o.ShippingFee, TaxAmount = o.TaxAmount, TotalAmount = o.TotalAmount,
        CouponCode = o.CouponCode, PaymentMethod = o.PaymentMethod, PaymentStatus = o.PaymentStatus, PaymentRef = o.PaymentRef, PayuTxnId = o.PayuTxnId,
        FulfillmentStatus = o.FulfillmentStatus, Courier = o.Courier, ShiprocketOrderId = o.ShiprocketOrderId, ShiprocketShipmentId = o.ShiprocketShipmentId,
        ShiprocketAwb = o.ShiprocketAwb, ShiprocketCourier = o.ShiprocketCourier, ShippingLabelUrl = o.ShippingLabelUrl,
        DeliveryStatus = o.DeliveryStatus, DeliveryTrackingEvents = o.DeliveryTrackingEvents, Notes = o.Notes,
    };

    public static CustomerDto ToDto(this Customer c) => new()
    {
        Id = c.Id, FullName = c.FullName, Email = c.Email, Phone = c.Phone,
        ShippingAddress = c.ShippingAddress, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt,
    };

    public static TrustBatchDto ToDto(this TrustBatch b) => new()
    {
        Id = b.Id, BatchNo = b.BatchNo, ProductName = b.ProductName, PackSize = b.PackSize, MfgDate = b.MfgDate, ExpDate = b.ExpDate,
        Status = b.Status, DocumentName = b.DocumentName, DocumentUrl = b.DocumentUrl, DocumentType = b.DocumentType, FileSize = b.FileSize,
        LabName = b.LabName, Notes = b.Notes, QualityChecks = b.QualityChecks, Documents = b.Documents, IsActive = b.IsActive,
        CreatedAt = b.CreatedAt, UpdatedAt = b.UpdatedAt,
    };

    public static ReviewDto ToDto(this Review r) => new()
    {
        Id = r.Id, Name = r.Name, City = r.City, Rating = r.Rating, Title = r.Title, Body = r.Body, Verified = r.Verified, Approved = r.Approved, CreatedAt = r.CreatedAt,
    };

    public static WhatsAppMessageDto ToDto(this WhatsAppMessage m) => new()
    {
        Id = m.Id, Direction = m.Direction, WaMessageId = m.WaMessageId, Phone = m.Phone, ProfileName = m.ProfileName,
        Type = m.Type, Body = m.Body, Status = m.Status, OrderId = m.OrderId, CreatedAt = m.CreatedAt,
    };

    /// <summary>Indian-style timestamp used in tracking timelines ("5/10/2026, 3:42:10 pm").</summary>
    public static string TimelineStamp() =>
        TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata"))
            .ToString("d/M/yyyy, h:mm:ss tt", System.Globalization.CultureInfo.GetCultureInfo("en-IN"));
}
