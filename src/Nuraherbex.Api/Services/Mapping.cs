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
        Category = p.Category, ShortName = p.ShortName, Badge = p.Badge, Highlights = p.Highlights, Ingredients = p.Ingredients, Directions = p.Directions, ServingInfo = p.ServingInfo,
    };

    public static OrderDto ToDto(this Order o) => new()
    {
        Id = o.Id, CreatedAt = o.CreatedAt, UpdatedAt = o.UpdatedAt,
        CustomerName = o.CustomerName, CustomerEmail = o.CustomerEmail, CustomerPhone = o.CustomerPhone, CustomerId = o.CustomerId,
        ShippingAddress = o.ShippingAddress, Items = o.Items,
        Subtotal = o.Subtotal, DiscountAmount = o.DiscountAmount, ShippingFee = o.ShippingFee, TaxAmount = o.TaxAmount, TotalAmount = o.TotalAmount,
        CouponCode = o.CouponCode, PaymentMethod = o.PaymentMethod, PaymentStatus = o.PaymentStatus, PaymentRef = o.PaymentRef, PayuTxnId = o.PayuTxnId,
        ParcelWeightKg = o.ParcelWeightKg, ParcelLengthCm = o.ParcelLengthCm, ParcelBreadthCm = o.ParcelBreadthCm, ParcelHeightCm = o.ParcelHeightCm,
        FulfillmentReadyAt = o.FulfillmentReadyAt, ShipmentBookedAt = o.ShipmentBookedAt, PickedUpAt = o.PickedUpAt, DeliveredAt = o.DeliveredAt,
        LastShippingEventAt = o.LastShippingEventAt, FulfillmentStatus = o.FulfillmentStatus, Courier = o.Courier, ShiprocketOrderId = o.ShiprocketOrderId, ShiprocketShipmentId = o.ShiprocketShipmentId,
        ShiprocketAwb = o.ShiprocketAwb, ShiprocketCourier = o.ShiprocketCourier,
        DeliveryStatus = o.DeliveryStatus, DeliveryTrackingEvents = o.DeliveryTrackingEvents, Notes = o.Notes,
    };

    public static CustomerDto ToDto(this Customer c) => new()
    {
        Id = c.Id, FullName = c.FullName, Email = c.Email, Phone = c.Phone, ProfileImageData = c.ProfileImageData,
        ShippingAddress = c.ShippingAddress, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt,
    };

    public static TrustBatchDto ToDto(this TrustBatch b) => new()
    {
        Id = b.Id, BatchNo = b.BatchNo, ProductName = b.ProductName, PackSize = b.PackSize, MfgDate = b.MfgDate, ExpDate = b.ExpDate,
        Status = b.Status, DocumentName = b.DocumentName, DocumentUrl = b.DocumentUrl, DocumentType = b.DocumentType, FileSize = b.FileSize,
        LabName = b.LabName, Notes = b.Notes, QualityChecks = b.QualityChecks, Documents = b.Documents, IsActive = b.IsActive,
        CreatedAt = b.CreatedAt, UpdatedAt = b.UpdatedAt,
    };

    public static ReviewDto ToDto(this Review r, Customer? currentCustomer = null) => new()
    {
        Id = r.Id,
        Name = string.IsNullOrWhiteSpace(currentCustomer?.FullName) ? r.Name : currentCustomer.FullName,
        City = r.City,
        Rating = r.Rating,
        Title = r.Title,
        Body = r.Body,
        Verified = r.Verified,
        Approved = r.Approved,
        ProfileImageData = currentCustomer?.ProfileImageData,
        CreatedAt = r.CreatedAt,
    };

    public static readonly string[] MediaTypes = ["image", "video", "audio", "document", "sticker"];

    public static WhatsAppMessageDto ToDto(this WhatsAppMessage m, MediaSigner? signer = null) => new()
    {
        Id = m.Id, Direction = m.Direction, WaMessageId = m.WaMessageId, Phone = m.Phone, ProfileName = m.ProfileName,
        Type = m.Type, Body = m.Body, Status = m.Status, OrderId = m.OrderId, CreatedAt = m.CreatedAt,
        SentBy = m.SentBy is null or "system" or "auto" ? null : m.SentBy,
        MediaUrl = signer is not null && (m.MediaId is not null || m.MediaFile is not null) ? signer.Url(m.Id) : null,
        MediaMime = m.MediaMime, MediaName = m.MediaName, Reaction = m.Reaction,
        Error = m.Direction == "outbound" && m.Status == "failed" ? WhatsAppErrors.Friendly(m.RawPayload) : null,
    };

    /// <summary>Short text for lists and AI context when a message has no text of its own.</summary>
    public static string MessageLabel(string type) => type switch
    {
        "image" => "Photo", "sticker" => "Sticker", "video" => "Video", "audio" => "Voice message", "document" => "Document",
        "location" => "Location", "contacts" => "Contact", "unsupported" => "Unsupported message", _ => $"[{type}]",
    };

    public static FollowUpDto ToDto(this FollowUp f, string leadName, string leadPhone, string? ownerName) => new()
    {
        Id = f.Id, LeadId = f.LeadId, LeadName = leadName, LeadPhone = leadPhone, OrderId = f.OrderId,
        AssignedToEmployeeId = f.AssignedToEmployeeId, AssignedToName = ownerName, Type = f.Type, Title = f.Title, Notes = f.Notes,
        SuggestedMessage = f.SuggestedMessage, DueAt = f.DueAt, Status = f.Status, Outcome = f.Outcome, CompletedAt = f.CompletedAt,
        CompletedBy = f.CompletedBy, Source = f.Source, StepKey = f.StepKey, RemindAt = f.RemindAt, CreatedAt = f.CreatedAt,
        IsOverdue = f.Status == "pending" && f.DueAt < FollowUpService.TodayStartUtc(),
        CanMessage = f.Status == "pending" && !string.IsNullOrWhiteSpace(f.SuggestedMessage),
    };

    public static EmployeeDto ToDto(this Employee e) => new()
    {
        Id = e.Id, Name = e.Name, Email = e.Email, Phone = e.Phone, Role = e.Role, IsActive = e.IsActive,
        CreatedAt = e.CreatedAt, LastLoginAt = e.LastLoginAt, IsOnline = StaffAvailability.IsOnline(e), WhatsAppActive = e.WhatsAppActive, DepartmentId = e.DepartmentId, DesignationId = e.DesignationId,
    };

    public static LeadDto ToDto(this Lead l, Conversation? c = null) => new()
    {
        Id = l.Id, Name = l.Name, Phone = l.Phone, Email = l.Email, Source = l.Source, SourceDetail = l.SourceDetail,
        Status = l.Status, ConsentStatus = l.ConsentStatus, FirstMessage = l.FirstMessage, Notes = l.Notes, CustomerId = l.CustomerId,
        UnreadCount = c?.UnreadCount ?? 0, ServiceWindowExpiresAt = c?.ServiceWindowExpiresAt,
        CreatedAt = l.CreatedAt, LastContactAt = l.LastContactAt, ConvertedAt = l.ConvertedAt,
        AssignedToEmployeeId = l.AssignedToEmployeeId, AssignedAt = l.AssignedAt,
    };

    /// <summary>Indian-style timestamp used in tracking timelines ("5/10/2026, 3:42:10 pm").</summary>
    public static string TimelineStamp() =>
        TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata"))
            .ToString("d/M/yyyy, h:mm:ss tt", System.Globalization.CultureInfo.GetCultureInfo("en-IN"));
}
