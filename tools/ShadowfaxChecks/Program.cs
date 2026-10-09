using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Controllers;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> work, string label) { try { await work(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception(label); }
var fake = new CourierHandler();
var config = new ShadowfaxOptions { StagingToken = "test-only-token", WebhookToken = new string('w', 40), Pickup = new() { Name = "Store", Contact = "9999999999", AddressLine1 = "Test street", City = "Chennai", Pincode = "600002", UniqueCode = "TEST" } };
using var http = new HttpClient(fake);
var sf = new ShadowfaxService(http, Options.Create(config), NullLogger<ShadowfaxService>.Instance);
Order NewOrder(string id = "NH-TEST") => new() { Id = id, CustomerName = "Customer", CustomerPhone = "9999999999", PaymentMethod = "ONLINE", PaymentStatus = "PAID", Subtotal = 1200, TotalAmount = 1200,
    ShippingAddress = new() { AddressLine1 = "Delivery street", City = "Chennai", State = "Tamil Nadu", Pincode = "600001" },
    ParcelWeightKg = .4m, ParcelLengthCm = 10, ParcelBreadthCm = 10, ParcelHeightCm = 14,
    Items = [new() { Id = "p", Sku = "SKU", Name = "Product", Price = 1200, Quantity = 1, WeightKg = .4m }] };
var order = NewOrder();
await sf.CreateOrderAsync(order, new());
Check(fake.Booking!["return_details"] is not null && fake.Booking["rts_details"] is null, "Documented return_details payload");
Check(fake.Booking["order_details"]!["cod_amount"]!.GetValue<decimal>() == 0, "Prepaid COD amount is zero");
order.PaymentMethod = "COD";
await sf.CreateOrderAsync(order, new());
Check(fake.Booking["order_details"]!["cod_amount"]!.GetValue<decimal>() == 1200, "COD amount comes from stored order total");
fake.RejectBooking = true;
await Reject(() => sf.CreateOrderAsync(order, new()), "HTTP 200 business rejection is not a booking");
fake.RejectBooking = false; fake.Serviceable = false;
var before = fake.Bookings;
await Reject(() => sf.CreateOrderAsync(order, new()), "Unserviceable route blocks booking");
Check(fake.Bookings == before, "Unserviceable request never reaches create endpoint");
fake.Serviceable = true; fake.CancelBody = "{}";
await Reject(() => sf.CancelAsync("SFTEST", "test"), "Malformed cancellation never becomes success");
fake.CancelBody = "{\"responseCode\":304,\"responseMsg\":\"Accepted\"}";
Check((await sf.CancelAsync("SFTEST", "test")).Contains("queued"), "Queued cancellation is identified by response code");
fake.Label = "http://unsafe.test/label";
await Reject(() => sf.GenerateLabelAsync("SFTEST"), "Insecure label URL rejected");
fake.Label = "https://example.test/label.pdf";
Check((await sf.GenerateLabelAsync("SFTEST")).StartsWith("https://"), "HTTPS label accepted");
using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
await using var db = new NuraDbContext(new DbContextOptionsBuilder<NuraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
var gate = new OrderOperationLock(db);
var shipping = new ShippingGateway(new ShiprocketService(http, Options.Create(new ShiprocketOptions()), NullLogger<ShiprocketService>.Instance), sf);
var orders = new OrderService(db, new ProductService(db, Options.Create(new StoreOptions())), shipping, gate, scopeFactory, NullLogger<OrderService>.Instance);
db.Orders.Add(NewOrder()); await db.SaveChangesAsync();
await Reject(() => orders.RetryShipmentAsync("NH-TEST"), "Packing gate blocks paid but unpacked order");
await orders.MarkReadyAsync("NH-TEST");
before = fake.Bookings;
var booked = await orders.RetryShipmentAsync("NH-TEST");
await orders.RetryShipmentAsync("NH-TEST");
Check(fake.Bookings == before + 1 && booked.ShiprocketAwb == "SFTEST", "Repeated booking reuses stored AWB");
Check((await db.Orders.SingleAsync()).ShipmentClientOrderId == "NH-TEST", "Booking reference is persisted");
var processor = new ShadowfaxTrackingProcessor(db, gate, scopeFactory, NullLogger<ShadowfaxTrackingProcessor>.Instance);
JsonElement Event(string code, string time, string awb = "SFTEST", string reference = "NH-TEST") => JsonSerializer.SerializeToElement(new { awb_number = awb, client_order_id = reference, status_id = code, last_updated = time });
await processor.ProcessAsync(Event("ofd", "2026-10-09T12:00:00Z"));
var events = (await db.Orders.SingleAsync()).DeliveryTrackingEvents.Count;
await processor.ProcessAsync(Event("ofd", "2026-10-09T12:00:00Z"));
Check((await db.Orders.SingleAsync()).DeliveryTrackingEvents.Count == events, "Duplicate callback does not append another event");
await processor.ProcessAsync(Event("picked", "2026-10-09T11:00:00Z"));
Check((await db.Orders.SingleAsync()).FulfillmentStatus == "OUT_FOR_DELIVERY", "Old scan cannot regress fulfillment");
await processor.ProcessAsync(Event("delivered", "2026-10-09T13:00:00Z", reference: "wrong"));
Check((await db.Orders.SingleAsync()).FulfillmentStatus == "OUT_FOR_DELIVERY", "Mismatched client reference is ignored");
await processor.ProcessAsync(Event("delivered", "2026-10-09T13:00:00Z", awb: "WRONG"));
Check((await db.Orders.SingleAsync()).FulfillmentStatus == "OUT_FOR_DELIVERY", "Mismatched AWB is ignored");
await processor.ProcessAsync(Event("delivered", "2026-10-09T13:00:00Z"));
await processor.ProcessAsync(Event("picked", "2026-10-09T14:00:00Z"));
Check((await db.Orders.SingleAsync()).DeliveryStatus == "Delivered to Recipient", "Late scan cannot overwrite delivered display status");
Check(ShadowfaxTrackingProcessor.ParseTime("2026-10-09 18:30:00") == new DateTime(2026,10,9,13,0,0,DateTimeKind.Utc), "Callback timestamps without offset are interpreted as IST");
var controller = new ShadowfaxWebhookController(Options.Create(config), processor) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
Check(await controller.Receive(Event("picked", "")) is UnauthorizedResult, "Missing callback credential rejected");
config.WebhookToken = "";
Check(await controller.Receive(Event("picked", "")) is StatusCodeResult { StatusCode: 503 }, "Unconfigured webhook fails closed");
await Reject(() => orders.RetryShipmentAsync("NH-TEST"), "Delivered order cannot be rebooked");
Check(Enumerable.Range(0, 10000).Select(_ => OrderService.NewOrderId()).Distinct().Count() == 10000, "New order references have high entropy and fit the schema");
var retryOrder = NewOrder("NH-RETRY");
retryOrder.FulfillmentReadyAt = DateTime.UtcNow;
db.Orders.Add(retryOrder); await db.SaveChangesAsync();
fake.TimeoutNext = true;
await Reject(() => orders.RetryShipmentAsync("NH-RETRY"), "Ambiguous booking timeout is reported for retry");
var retryReference = (await db.Orders.SingleAsync(o => o.Id == "NH-RETRY")).ShipmentClientOrderId;
await orders.RetryShipmentAsync("NH-RETRY");
Check(fake.Booking!["order_details"]!["client_order_id"]!.ToString() == retryReference, "Timeout retry preserves original courier idempotency key");

db.Products.Add(new Product { Id = "p", Sku = "SKU", Name = "Product", Price = 1200, StockQuantity = 10, WeightKg = .4m, LengthCm = 10, BreadthCm = 10, HeightCm = 14 });
await db.SaveChangesAsync();
var pending = await orders.CreateAsync(new CreateOrderRequest { PaymentMethod = "ONLINE", Customer = new() { FullName = "Buyer", Email = "buyer@example.test", Phone = "9999999999" }, Shipping = NewOrder().ShippingAddress, Items = [new() { Id = "p", Quantity = 1 }] }, null, holdUntilPaid: true);
Check(await db.Orders.AnyAsync(o => o.Id == pending.Id && o.PaymentStatus == "PENDING"), "Online checkout saves pending order before payment");
await Reject(() => orders.MarkReadyAsync(pending.Id), "Unpaid online order cannot be marked packed");
var payOptions = new PayUOptions { MerchantKey = "test-merchant", MerchantSalt = "test-salt" };
var payu = new PayUService(db, orders, gate, Options.Create(payOptions), Options.Create(new StoreOptions()), NullLogger<PayUService>.Instance);
var initiated = await payu.InitiateAsync(pending.Id, "https://example.test");
var payment = new Dictionary<string,string>(initiated.Fields) { ["status"] = "success", ["mihpayid"] = "TEST-PAYMENT" };
void Sign()
{
    string G(string name) => payment.GetValueOrDefault(name) ?? "";
    var sequence = $"test-salt|{G("status")}||||||{G("udf5")}|{G("udf4")}|{G("udf3")}|{G("udf2")}|{G("udf1")}|{G("email")}|{G("firstname")}|{G("productinfo")}|{G("amount")}|{G("txnid")}|{G("key")}";
    payment["hash"] = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(sequence))).ToLowerInvariant();
}
payment["hash"] = "forged";
await Reject(() => payu.ProcessResponseAsync(payment), "Forged payment cannot confirm an order");
var originalAmount = payment["amount"];
payment["amount"] = "1199.50"; Sign();
await Reject(() => payu.ProcessResponseAsync(payment), "Underpayment is rejected even within one rupee");
payment["amount"] = originalAmount; Sign();
before = fake.Bookings;
await Task.WhenAll(payu.ProcessResponseAsync(payment), payu.ProcessResponseAsync(payment));
Check(await db.Payments.CountAsync(p => p.OrderId == pending.Id) == 1, "Concurrent payment callbacks settle once");
Check((await db.Products.SingleAsync(p => p.Id == "p")).StockQuantity == 9, "Concurrent payment callbacks decrement stock once");
Check(fake.Bookings == before, "Payment confirmation never triggers pickup before packing");
Console.WriteLine($"{checks} Shadowfax checks passed. No external services contacted.");

sealed class CourierHandler : HttpMessageHandler
{
    public JsonNode? Booking; public int Bookings; public bool RejectBooking; public bool Serviceable = true; public bool TimeoutNext;
    public string CancelBody = "{\"responseCode\":200,\"responseMsg\":\"Cancelled\"}";
    public string Label = "https://example.test/label.pdf";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization?.ToString() != "Token test-only-token") throw new Exception("Incorrect server-side authentication");
        var uri = request.RequestUri!; string response;
        if (uri.AbsolutePath.EndsWith("serviceability/"))
        {
            var pin = uri.Query.Contains("600001") ? 600001 : 600002;
            response = Serviceable ? JsonSerializer.Serialize(new[] { new { code = pin, services = new[] { "Regular", "Marketplace", "RTS", "dc_pickup", "RTO" } } }) : "[]";
        }
        else if (uri.AbsolutePath.EndsWith("orders/"))
        {
            Bookings++; Booking = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (TimeoutNext) { TimeoutNext = false; throw new TaskCanceledException("Simulated response lost after booking submission"); }
            response = RejectBooking ? "{\"message\":\"Failure\",\"errors\":\"Rejected\"}" : "{\"message\":\"Success\",\"data\":{\"id\":123,\"awb_number\":\"SFTEST\"}}";
        }
        else if (uri.AbsolutePath.EndsWith("cancel/")) response = CancelBody;
        else if (uri.AbsolutePath.EndsWith("generate_label/")) response = JsonSerializer.Serialize(new { message = "Success", data = new { label_url = Label } });
        else throw new Exception("Unexpected URL " + uri);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
    }
}