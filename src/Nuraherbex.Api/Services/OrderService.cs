using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public partial class OrderService(
    NuraDbContext db,
    ProductService products,
    ShiprocketService shiprocket,
    IServiceScopeFactory scopes,
    ILogger<OrderService> log)
{
    [GeneratedRegex(@"^[1-9][0-9]{5}$")]
    private static partial Regex PincodeRegex();

    public static string NewOrderId() => $"NH-{Random.Shared.Next(100000, 999999)}";

    public async Task<Order> CreateAsync(CreateOrderRequest req, string? customerId)
    {
        var c = req.Customer;
        var s = req.Shipping;
        if (string.IsNullOrWhiteSpace(c.FullName) || string.IsNullOrWhiteSpace(c.Phone) || string.IsNullOrWhiteSpace(c.Email))
            throw new InvalidOperationException("Customer full name, phone number, and email are required");
        if (string.IsNullOrWhiteSpace(s.AddressLine1) || string.IsNullOrWhiteSpace(s.City) || string.IsNullOrWhiteSpace(s.State) || string.IsNullOrWhiteSpace(s.Pincode))
            throw new InvalidOperationException("Complete shipping address (address line 1, city, state, pincode) is required");

        var pincode = s.Pincode.Trim();
        if (!PincodeRegex().IsMatch(pincode)) throw new InvalidOperationException("Please enter a valid 6-digit Indian Pincode");

        var phone = new string(c.Phone.Where(char.IsDigit).ToArray());
        if (phone.Length < 10) throw new InvalidOperationException("Please enter a valid 10-digit Phone number");

        var calc = await products.CalculateAsync(req.Items, req.CouponCode);
        var t = calc.Totals;
        var isCod = string.Equals(req.PaymentMethod, "COD", StringComparison.OrdinalIgnoreCase);

        string id;
        do { id = NewOrderId(); } while (await db.Orders.AnyAsync(o => o.Id == id));

        var order = new Order
        {
            Id = id, CustomerName = c.FullName.Trim(), CustomerEmail = c.Email.Trim().ToLowerInvariant(), CustomerPhone = phone,
            CustomerId = customerId ?? c.Id,
            ShippingAddress = new AddressDto
            {
                AddressLine1 = s.AddressLine1.Trim(), AddressLine2 = (s.AddressLine2 ?? "").Trim(), City = s.City.Trim(),
                State = s.State.Trim(), Pincode = pincode, Country = string.IsNullOrWhiteSpace(s.Country) ? "India" : s.Country,
            },
            Items = t.Items, Subtotal = t.Subtotal, DiscountAmount = t.DiscountAmount, CouponCode = t.CouponCode,
            ShippingFee = t.ShippingFee, TaxAmount = t.TaxAmount, TotalAmount = t.FinalTotal,
            PaymentMethod = isCod ? "COD" : "ONLINE", PaymentStatus = isCod ? "COD_PENDING" : "PENDING",
            FulfillmentStatus = "PENDING",
            Notes = isCod ? "Cash on Delivery order" : "Pending online payment settlement",
            DeliveryStatus = isCod ? "Order Confirmed" : "Payment Pending",
            DeliveryTrackingEvents =
            [
                new() { Status = isCod ? "Order Confirmed (Cash on Delivery)" : "Order Created (Awaiting Payment)", Time = Mapping.TimelineStamp(), Done = true, Active = true },
            ],
        };

        if (isCod)
        {
            await TryDispatchAsync(order, calc.Parcel);
            await products.DecrementStockAsync(order);
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        if (isCod) NotifyConfirmed(order);
        return order;
    }

    public Task<Order?> GetAsync(string id) => db.Orders.FirstOrDefaultAsync(o => o.Id == id);

    /// <summary>Track by order id (partial match), customer phone, or AWB.</summary>
    public async Task<Order?> FindByQueryAsync(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return null;
        var digits = new string(q.Where(char.IsDigit).ToArray());
        var upper = q.ToUpperInvariant();
        return await db.Orders.AsNoTracking()
            .Where(o => o.Id.Contains(upper) || o.ShiprocketAwb == q || (digits.Length >= 10 && o.CustomerPhone.EndsWith(digits.Substring(digits.Length - 10))))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<List<Order>> ListAllAsync() =>
        await db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAt).ToListAsync();

    public async Task<List<Order>> ListForCustomerAsync(string email, string phone)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        var p = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return await db.Orders.AsNoTracking()
            .Where(o => (e != "" && o.CustomerEmail == e) || (p != "" && o.CustomerPhone == p))
            .OrderByDescending(o => o.CreatedAt).ToListAsync();
    }

    /// <summary>Marks an online order PAID (idempotent), decrements stock, dispatches to Shiprocket, notifies the customer.</summary>
    public async Task<Order> MarkPaidAsync(string orderId, string? paymentRef, string? txnId, string rawGatewayJson)
    {
        var order = await GetAsync(orderId) ?? throw new InvalidOperationException($"Order {orderId} not found");
        if (order.PaymentStatus == "PAID") return order;

        order.PaymentStatus = "PAID";
        order.DeliveryStatus = "Order Confirmed";
        order.PaymentRef = paymentRef;
        order.PayuTxnId = txnId;
        order.UpdatedAt = DateTime.UtcNow;
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, new TrackingEventDto { Status = $"Payment Verified ({paymentRef})", Time = Mapping.TimelineStamp(), Done = true }];

        var parcel = new ParcelSpecs { WeightKg = order.Items.Sum(i => (i.WeightKg > 0 ? i.WeightKg : 0.38m) * i.Quantity) };
        await TryDispatchAsync(order, parcel);

        db.Payments.Add(new Payment
        {
            OrderId = order.Id, Provider = "PAYU", GatewayOrderId = txnId, GatewayPaymentId = paymentRef,
            Amount = order.TotalAmount, Status = "CAPTURED", GatewayResponse = rawGatewayJson,
        });
        await products.DecrementStockAsync(order);
        await db.SaveChangesAsync();

        NotifyConfirmed(order);
        return order;
    }

    public async Task<Order> MarkDeliveredAsync(string orderId)
    {
        var order = await GetAsync(orderId) ?? throw new InvalidOperationException($"Order {orderId} not found");
        order.FulfillmentStatus = "DELIVERED";
        order.DeliveryStatus = "Delivered";
        if (order.PaymentMethod == "COD") order.PaymentStatus = "COD_COLLECTED";
        order.UpdatedAt = DateTime.UtcNow;
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, new TrackingEventDto { Status = "Delivered to Recipient", Time = Mapping.TimelineStamp(), Done = true, Active = true }];
        await db.SaveChangesAsync();

        InBackground(async (email, whatsapp) =>
        {
            try { await email.SendOrderDeliveredAsync(order); } catch (Exception ex) { log.LogWarning(ex, "Delivered email failed"); }
            try { await whatsapp.NotifyDeliveredAsync(order); } catch (Exception ex) { log.LogWarning(ex, "Delivered WhatsApp failed"); }
        });
        return order;
    }

    /// <summary>Manual / retry push to Shiprocket (admin).</summary>
    public async Task<ShiprocketResult> RetryShiprocketAsync(string orderId)
    {
        var order = await GetAsync(orderId) ?? throw new KeyNotFoundException("Order not found");
        if (order.PaymentStatus != "PAID" && order.PaymentMethod != "COD")
            throw new InvalidOperationException("Cannot ship an unpaid online order. Verify payment first.");

        var res = await shiprocket.CreateOrderAsync(order, new ParcelSpecs());
        Apply(order, res);
        await db.SaveChangesAsync();
        return res;
    }

    private async Task TryDispatchAsync(Order order, ParcelSpecs parcel)
    {
        try
        {
            var res = await shiprocket.CreateOrderAsync(order, parcel);
            if (res.Success) Apply(order, res);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Shiprocket] Dispatch deferred for {Order}", order.Id);
            order.Notes += $" | Shiprocket auto-dispatch deferred: {ex.Message}";
        }
    }

    private static void Apply(Order order, ShiprocketResult res)
    {
        order.ShiprocketOrderId = res.ShiprocketOrderId;
        order.ShiprocketShipmentId = res.ShiprocketShipmentId;
        order.ShiprocketAwb = res.ShiprocketAwb;
        order.ShiprocketCourier = res.ShiprocketCourier;
        order.FulfillmentStatus = res.ShiprocketAwb.StartsWith("SR-PENDING-") ? "SHIPMENT_CREATED" : "AWB_ASSIGNED";
        order.DeliveryStatus = res.DeliveryStatus;
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, .. res.TrackingEvents];
        order.UpdatedAt = DateTime.UtcNow;
    }

    private void NotifyConfirmed(Order order) => InBackground(async (email, whatsapp) =>
    {
        try { await email.SendOrderConfirmationAsync(order); } catch (Exception ex) { log.LogWarning(ex, "Confirmation email failed"); }
        try { await whatsapp.NotifyOrderConfirmedAsync(order); } catch (Exception ex) { log.LogWarning(ex, "Confirmation WhatsApp failed"); }
    });

    /// <summary>Runs notification work in its own DI scope so it outlives the HTTP request's DbContext.</summary>
    private void InBackground(Func<EmailService, WhatsAppService, Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            await work(scope.ServiceProvider.GetRequiredService<EmailService>(), scope.ServiceProvider.GetRequiredService<WhatsAppService>());
        }
        catch (Exception ex) { log.LogWarning(ex, "Background notification failed"); }
    });
}
