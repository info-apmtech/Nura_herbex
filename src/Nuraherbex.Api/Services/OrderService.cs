using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public partial class OrderService(
    NuraDbContext db,
    ProductService products,
    ShippingGateway shipping,
    IServiceScopeFactory scopes,
    ILogger<OrderService> log)
{
    [GeneratedRegex(@"^[1-9][0-9]{5}$")]
    private static partial Regex PincodeRegex();

    public static string NewOrderId() => $"NH-{Random.Shared.Next(100000, 999999)}";

    /// <param name="holdUntilPaid">
    /// Public checkout: an ONLINE order is not saved to the orders table. It is held as a <see cref="PaymentAttempt"/> snapshot and only
    /// becomes a real order once PayU confirms payment (see <see cref="ConfirmHeldOrderAsync"/>).
    /// </param>
    public async Task<Order> CreateAsync(CreateOrderRequest req, string? customerId, bool holdUntilPaid = false)
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
            await products.DecrementStockAsync(order);

        if (!isCod && holdUntilPaid)
        {
            db.PaymentAttempts.Add(new PaymentAttempt
            {
                OrderId = order.Id, Amount = order.TotalAmount, Status = "CREATED",
                CustomerName = order.CustomerName, CustomerEmail = order.CustomerEmail, CustomerPhone = order.CustomerPhone,
                OrderSnapshotJson = System.Text.Json.JsonSerializer.Serialize(order),
            });
            await db.SaveChangesAsync();
            return order;
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        if (isCod)
        {
            order.Courier = shipping.ProviderFor(order);
            await TryDispatchAsync(order, calc.Parcel);
            await db.SaveChangesAsync();
            NotifyConfirmed(order);
        }
        return order;
    }

    /// <summary>Latest not-yet-paid checkout attempt for an order id, or null.</summary>
    public Task<PaymentAttempt?> FindHeldAttemptAsync(string orderId) =>
        db.PaymentAttempts.Where(a => a.OrderId == orderId && a.Status != "SUCCESS")
            .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync();

    /// <summary>PayU confirmed payment: persist settlement and inventory, then book the confirmed order with Shadowfax.</summary>
    public async Task<Order> ConfirmHeldOrderAsync(PaymentAttempt attempt, string? paymentRef, string? txnId, string rawGatewayJson)
    {
        var order = await GetAsync(attempt.OrderId);
        if (order is null)
        {
            order = System.Text.Json.JsonSerializer.Deserialize<Order>(attempt.OrderSnapshotJson)
                ?? throw new InvalidOperationException("Held order data is missing");
            db.Orders.Add(order);
            await db.SaveChangesAsync();
        }
        return await MarkPaidAsync(order.Id, paymentRef, txnId, rawGatewayJson);
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

    /// <summary>Marks an online order PAID (idempotent), persists settlement, then books it with Shadowfax.</summary>
    public async Task<Order> MarkPaidAsync(string orderId, string? paymentRef, string? txnId, string rawGatewayJson)
    {
        var order = await GetAsync(orderId) ?? throw new InvalidOperationException($"Order {orderId} not found");
        var parcel = new ParcelSpecs { WeightKg = order.Items.Sum(i => (i.WeightKg > 0 ? i.WeightKg : 0.38m) * i.Quantity) };
        if (order.PaymentStatus == "PAID")
        {
            var hasActiveAwb = !string.IsNullOrWhiteSpace(order.ShiprocketAwb)
                && !order.ShiprocketAwb.StartsWith("SR-PENDING-", StringComparison.OrdinalIgnoreCase);
            if (hasActiveAwb || order.FulfillmentStatus == "CANCELLED") return order;

            order.Courier = shipping.ProviderFor(order);
            await TryDispatchAsync(order, parcel);
            await db.SaveChangesAsync();
            return order;
        }

        order.PaymentStatus = "PAID";
        order.DeliveryStatus = "Order Confirmed";
        order.PaymentRef = paymentRef;
        order.PayuTxnId = txnId;
        order.UpdatedAt = DateTime.UtcNow;
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, new TrackingEventDto { Status = $"Payment Verified ({paymentRef})", Time = Mapping.TimelineStamp(), Done = true }];

        db.Payments.Add(new Payment
        {
            OrderId = order.Id, Provider = "PAYU", GatewayOrderId = txnId, GatewayPaymentId = paymentRef,
            Amount = order.TotalAmount, Status = "CAPTURED", GatewayResponse = rawGatewayJson,
        });
        await products.DecrementStockAsync(order);
        await db.SaveChangesAsync();

        order.Courier = shipping.ProviderFor(order);
        await TryDispatchAsync(order, parcel);
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

    /// <summary>Manual / retry push to the active courier platform (admin).</summary>
    public async Task<ShiprocketResult> RetryShipmentAsync(string orderId)
    {
        var order = await GetAsync(orderId) ?? throw new KeyNotFoundException("Order not found");
        if (order.PaymentStatus != "PAID" && order.PaymentMethod != "COD")
            throw new InvalidOperationException("Cannot ship an unpaid online order. Verify payment first.");
        if (!string.IsNullOrWhiteSpace(order.ShiprocketAwb) && !order.ShiprocketAwb.StartsWith("SR-PENDING-") && order.FulfillmentStatus != "CANCELLED")
            throw new InvalidOperationException("This order already has a shipment. Cancel it first to re-book.");

        var parcel = new ParcelSpecs { WeightKg = order.Items.Sum(i => (i.WeightKg > 0 ? i.WeightKg : 0.38m) * i.Quantity) };
        // Reuse the same idempotency key after a timeout, then advance after a confirmed cancellation.
        var clientOrderId = ShippingGateway.ShadowfaxClientOrderId(order);
        order.Courier = shipping.ProviderFor(order);
        await db.SaveChangesAsync();
        try
        {
            var (res, courier) = await shipping.CreateOrderAsync(order, parcel, clientOrderId);
            Apply(order, res, courier);
            await db.SaveChangesAsync();
            return res;
        }
        catch (Exception ex)
        {
            order.Notes = string.IsNullOrWhiteSpace(order.Notes)
                ? $"{order.Courier} retry failed: {ex.Message}"
                : $"{order.Notes} | {order.Courier} retry failed: {ex.Message}";
            order.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            throw;
        }
    }

    /// <summary>Creates and stores a Shadowfax PDF shipping label for an active, not-yet-picked shipment.</summary>
    public async Task<string> GenerateShipmentLabelAsync(string orderId)
    {
        var order = await GetAsync(orderId) ?? throw new KeyNotFoundException("Order not found");
        if (!ShippingGateway.IsShadowfax(order.Courier))
            throw new InvalidOperationException("Shipping labels are available here for Shadowfax shipments only.");
        if (string.IsNullOrWhiteSpace(order.ShiprocketAwb) || order.ShiprocketAwb.StartsWith("SR-PENDING-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This order does not have a Shadowfax AWB yet. Book the shipment first.");
        if (order.FulfillmentStatus is "CANCELLED" or "PICKED_UP" or "IN_TRANSIT" or "OUT_FOR_DELIVERY" or "DELIVERED" or "RTO")
            throw new InvalidOperationException("Shadowfax labels can only be generated before courier pickup and before cancellation.");

        order.ShippingLabelUrl = await shipping.GenerateLabelAsync(order.ShiprocketAwb);
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return order.ShippingLabelUrl;
    }

    /// <summary>Cancels the booked Shadowfax shipment (admin). Order stays; fulfillment returns to a re-shippable state once Shadowfax confirms.</summary>
    public async Task<string> CancelShipmentAsync(string orderId, string? reason)
    {
        var order = await GetAsync(orderId) ?? throw new KeyNotFoundException("Order not found");
        var msg = await shipping.CancelAsync(order, string.IsNullOrWhiteSpace(reason) ? "Cancelled by client" : reason.Trim());
        var queued = msg.Contains("queued", StringComparison.OrdinalIgnoreCase);
        if (!queued)
        {
            order.FulfillmentStatus = "CANCELLED";
            order.DeliveryStatus = "Shipment Cancelled";
            order.ShippingLabelUrl = null;
        }
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, new TrackingEventDto { Status = queued ? "Shipment cancellation queued" : "Shipment Cancelled", Time = Mapping.TimelineStamp(), Done = true, Active = true }];
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (!queued) NotifyStatus(order, "CANCELLED");
        return msg;
    }

    private async Task TryDispatchAsync(Order order, ParcelSpecs parcel)
    {
        order.Courier = shipping.ProviderFor(order);
        var courier = order.Courier;
        try
        {
            var (res, selectedCourier) = await shipping.CreateOrderAsync(order, parcel);
            if (res.Success) Apply(order, res, selectedCourier);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Courier}] Dispatch deferred for {Order}", courier, order.Id);
            var note = $"{courier} auto-dispatch deferred: {ex.Message}";
            order.Notes = string.IsNullOrWhiteSpace(order.Notes) ? note : $"{order.Notes} | {note}";
        }
    }

    private static void Apply(Order order, ShiprocketResult res, string courier)
    {
        order.Courier = courier;
        order.ShiprocketOrderId = res.ShiprocketOrderId;
        order.ShiprocketShipmentId = res.ShiprocketShipmentId;
        order.ShiprocketAwb = res.ShiprocketAwb;
        order.ShiprocketCourier = res.ShiprocketCourier;
        order.ShippingLabelUrl = null;
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

    /// <summary>Sends a customer status email (cancel/return/etc.) from its own DI scope. Email only — WhatsApp hooks come later.</summary>
    private void NotifyStatus(Order order, string status) => InBackground(async (email, _) =>
    {
        try { await email.SendStatusUpdateAsync(order, status); } catch (Exception ex) { log.LogWarning(ex, "{Status} email failed", status); }
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
