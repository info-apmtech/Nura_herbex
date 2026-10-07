using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "admin")]
public class AdminController(
    NuraDbContext db,
    OrderService orders,
    CustomerAuthService customers,
    SettingsService settings,
    TokenService tokens,
    EmailService email,
    WhatsAppService whatsapp,
    IOptions<AdminOptions> admin,
    IOptions<WhatsAppOptions> waOptions,
    ILogger<AdminController> log) : ControllerBase
{
    private static bool SafeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    // ---- Auth ------------------------------------------------------------------------

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("admin-login")]
    public IActionResult Login(AdminLoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
            return BadRequest(new AdminLoginResponse { Success = false, Message = "Email and password are required" });

        var o = admin.Value;
        if (string.IsNullOrWhiteSpace(o.Email) || (string.IsNullOrEmpty(o.PasswordHash) && string.IsNullOrEmpty(o.Password)))
            return StatusCode(503, new AdminLoginResponse { Success = false, Message = "Admin credentials are not configured on the server." });

        var emailOk = SafeEquals(req.Email.Trim().ToLowerInvariant(), o.Email.Trim().ToLowerInvariant());
        var passOk = !string.IsNullOrEmpty(o.PasswordHash)
            ? AdminPassword.Verify(req.Password, o.PasswordHash)
            : SafeEquals(req.Password, o.Password); // Compatibility for existing environment-based credentials.
        if (!(emailOk & passOk))
        {
            log.LogWarning("[Security] Failed admin login attempt for {Email}", req.Email);
            return Unauthorized(new AdminLoginResponse { Success = false, Message = "Invalid administrator credentials. Access denied." });
        }

        var e = o.Email.Trim().ToLowerInvariant();
        return Ok(new AdminLoginResponse { Success = true, Valid = true, Message = "Authenticated successfully", Token = tokens.CreateAdminToken(e), User = new AdminUserDto { Email = e, Role = "admin" } });
    }

    [HttpGet("verify"), Authorize(Roles = "admin")]
    public IActionResult Verify() => Ok(new AdminLoginResponse
    {
        Success = true, Valid = true, User = new AdminUserDto { Email = User.FindFirstValue(ClaimTypes.Email) ?? "", Role = "admin" },
    });

    // ---- Orders ----------------------------------------------------------------------

    [HttpGet("orders"), Authorize(Roles = "admin")]
    public async Task<OrderListResponse> ListOrders()
    {
        var list = (await orders.ListAllAsync()).Select(o => o.ToDto()).ToList();
        return new OrderListResponse { Success = true, Count = list.Count, Orders = list };
    }

    [HttpPost("orders"), Authorize(Roles = "admin")]
    public async Task<IActionResult> CreateManualOrder(AdminCreateOrderRequest r)
    {
        try
        {
            var order = await orders.CreateAsync(new CreateOrderRequest
            {
                Customer = new CheckoutCustomerDto
                {
                    FullName = r.CustomerName, Phone = r.CustomerPhone,
                    Email = string.IsNullOrWhiteSpace(r.CustomerEmail) ? $"{r.CustomerName.ToLowerInvariant().Replace(" ", "")}@gmail.com" : r.CustomerEmail,
                },
                Shipping = r.Address,
                Items = [new CartLineDto { Id = r.ProductId, Quantity = Math.Max(1, r.Quantity) }],
                PaymentMethod = r.PaymentMethod,
            }, null);

            if (r.PaymentMethod != "COD" && r.MarkPaid)
                order = await orders.MarkPaidAsync(order.Id, $"ADMIN-{DateTime.UtcNow:yyyyMMddHHmmss}", null, "{\"source\":\"admin-manual\"}");

            return StatusCode(201, new OrderResponse { Success = true, Order = order.ToDto() });
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    [HttpPost("orders/{id}/retry-shiprocket"), Authorize(Roles = "admin")]
    public async Task<IActionResult> RetryShiprocket(string id)
    {
        try
        {
            var res = await orders.RetryShiprocketAsync(id);
            return Ok(new ShipmentResponse
            {
                Success = true,
                Result = new ShipmentResultDto
                {
                    Success = res.Success, Simulated = res.Simulated, Courier = (await orders.GetAsync(id))?.Courier, ShiprocketOrderId = res.ShiprocketOrderId, ShiprocketShipmentId = res.ShiprocketShipmentId,
                    ShiprocketAwb = res.ShiprocketAwb, ShiprocketCourier = res.ShiprocketCourier, DeliveryStatus = res.DeliveryStatus,
                },
            });
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResult { Success = false, Message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    [HttpPost("orders/{id}/cancel-shipment"), Authorize(Roles = "admin")]
    public async Task<IActionResult> CancelShipment(string id, [FromQuery] string? reason)
    {
        try { return Ok(new ApiResult { Success = true, Message = await orders.CancelShipmentAsync(id, reason) }); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResult { Success = false, Message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    [HttpPost("orders/{id}/deliver"), Authorize(Roles = "admin")]
    public async Task<IActionResult> Deliver(string id)
    {
        try
        {
            var o = await orders.MarkDeliveredAsync(id);
            return Ok(new OrderResponse { Success = true, Message = $"Order {id} marked as Delivered. Customer review email triggered.", Order = o.ToDto() });
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    [HttpPost("orders/{id}/send-email"), Authorize(Roles = "admin")]
    public async Task<IActionResult> SendEmail(string id, AdminEmailRequest req)
    {
        var o = await orders.GetAsync(id);
        if (o is null) return NotFound(new ApiResult { Success = false, Message = "Order not found" });
        var sent = req.Type == "delivered" ? await email.SendOrderDeliveredAsync(o) : await email.SendOrderConfirmationAsync(o);
        return Ok(new ApiResult { Success = sent, Message = sent ? $"Email sent to {o.CustomerEmail}" : "Email not sent (Resend not configured or rejected the request)." });
    }

    /// <summary>PayU attempts: SUCCESS rows have an order; FAILED / CANCELLED rows are the stored failure records.</summary>
    [HttpGet("payments"), Authorize(Roles = "admin")]
    public async Task<PaymentAttemptListResponse> ListPayments([FromQuery] string? status)
    {
        var q = db.PaymentAttempts.AsNoTracking().Where(a => a.Status != "CREATED");
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status.ToUpper());
        var rows = await q.OrderByDescending(a => a.CreatedAt).Take(500).ToListAsync();
        var list = rows.Select(a => new PaymentAttemptDto
        {
            OrderId = a.OrderId, TxnId = a.TxnId, Amount = a.Amount, Status = a.Status, FailureReason = a.FailureReason,
            PayuPaymentId = a.PayuPaymentId, PaymentMode = a.PaymentMode, CustomerName = a.CustomerName, CustomerEmail = a.CustomerEmail,
            CustomerPhone = a.CustomerPhone, CreatedAt = a.CreatedAt, UpdatedAt = a.UpdatedAt,
        }).ToList();
        return new PaymentAttemptListResponse { Success = true, Count = list.Count, Attempts = list };
    }

    [HttpGet("customers"), Authorize(Roles = "admin")]
    public async Task<AdminCustomerListResponse> ListCustomers()
    {
        var list = await customers.ListForAdminAsync();
        return new AdminCustomerListResponse { Success = true, Count = list.Count, Customers = list };
    }

    // ---- Trust Passport batches ------------------------------------------------------

    [HttpGet("batches"), Authorize(Roles = "admin")]
    public async Task<TrustBatchListResponse> ListBatches() => new()
    {
        Success = true,
        Batches = (await db.TrustBatches.AsNoTracking().OrderByDescending(b => b.CreatedAt).ToListAsync()).Select(b => b.ToDto()).ToList(),
    };

    [HttpPut("batches"), Authorize(Roles = "admin")]
    public async Task<IActionResult> UpsertBatch(TrustBatchDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.BatchNo)) return BadRequest(new ApiResult { Success = false, Message = "Batch number is required" });
        var no = dto.BatchNo.Trim().ToUpperInvariant();
        var id = string.IsNullOrWhiteSpace(dto.Id) ? no : dto.Id.Trim().ToUpperInvariant();

        var b = await db.TrustBatches.FirstOrDefaultAsync(x => x.Id == id || x.BatchNo == no);
        if (b is null) { b = new TrustBatch { Id = id, BatchNo = no }; db.TrustBatches.Add(b); }

        b.ProductName = string.IsNullOrWhiteSpace(dto.ProductName) ? b.ProductName : dto.ProductName;
        b.PackSize = string.IsNullOrWhiteSpace(dto.PackSize) ? b.PackSize : dto.PackSize;
        b.MfgDate = dto.MfgDate; b.ExpDate = dto.ExpDate; b.Status = string.IsNullOrWhiteSpace(dto.Status) ? "Active / Verified" : dto.Status;
        b.DocumentName = dto.DocumentName; b.DocumentUrl = dto.DocumentUrl; b.DocumentType = dto.DocumentType; b.FileSize = dto.FileSize;
        b.LabName = dto.LabName; b.Notes = dto.Notes;
        b.QualityChecks = dto.QualityChecks; b.Documents = dto.Documents; b.IsActive = dto.IsActive; b.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new TrustBatchResponse { Success = true, Batch = b.ToDto() });
    }

    [HttpDelete("batches/{idOrNo}"), Authorize(Roles = "admin")]
    public async Task<IActionResult> DeleteBatch(string idOrNo)
    {
        var key = idOrNo.Trim().ToUpperInvariant();
        var rows = await db.TrustBatches.Where(b => b.BatchNo.ToUpper() == key || b.Id.ToUpper() == key).ToListAsync();
        db.TrustBatches.RemoveRange(rows);
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = $"Batch {idOrNo} deleted successfully" });
    }

    // ---- Site content / formulation --------------------------------------------------

    [HttpPut("content"), Authorize(Roles = "admin")]
    public async Task<IActionResult> SaveContent(SiteContent content)
    {
        await settings.SetAsync(SettingsService.SiteContentKey, content);
        return Ok(new ApiResult { Success = true });
    }

    [HttpDelete("content"), Authorize(Roles = "admin")]
    public async Task<IActionResult> ResetContent()
    {
        await settings.DeleteAsync(SettingsService.SiteContentKey);
        return Ok(new ApiResult { Success = true });
    }

    [HttpPut("formulation/ingredients"), Authorize(Roles = "admin")]
    public async Task<IActionResult> SaveIngredients(List<FormulationIngredientDto> list)
    {
        await settings.SetAsync(SettingsService.IngredientsKey, list);
        return Ok(new ApiResult { Success = true });
    }

    [HttpPut("formulation/metrics"), Authorize(Roles = "admin")]
    public async Task<IActionResult> SaveMetrics(FormulationMetricsDto metrics)
    {
        await settings.SetAsync(SettingsService.MetricsKey, metrics);
        return Ok(new ApiResult { Success = true });
    }

    [HttpGet("products"), Authorize(Roles = "admin")]
    public async Task<ProductListResponse> AllProducts() => new()
    {
        Success = true,
        Products = (await db.Products.AsNoTracking().OrderBy(p => p.Price).ToListAsync()).Select(p => p.ToDto()).ToList(),
    };

    // ---- WhatsApp --------------------------------------------------------------------

    [HttpGet("whatsapp/status"), Authorize(Roles = "admin")]
    public async Task<WhatsAppStatusDto> WhatsAppStatus() => new()
    {
        Success = true,
        Configured = waOptions.Value.CanSend,
        WebhookUrlHint = $"{Request.Scheme}://{Request.Host}/api/webhooks/whatsapp",
        InboundCount = await db.WhatsAppMessages.CountAsync(m => m.Direction == "inbound"),
        OutboundCount = await db.WhatsAppMessages.CountAsync(m => m.Direction == "outbound"),
    };

    [HttpGet("whatsapp/messages"), Authorize(Roles = "admin")]
    public async Task<WhatsAppMessageListResponse> WhatsAppMessages([FromQuery] int take = 100) => new()
    {
        Success = true,
        Messages = (await db.WhatsAppMessages.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync()).Select(m => m.ToDto()).ToList(),
    };

    [HttpPost("whatsapp/send"), Authorize(Roles = "admin")]
    public async Task<IActionResult> WhatsAppSend(WhatsAppSendRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Phone) || string.IsNullOrWhiteSpace(r.Text))
            return BadRequest(new ApiResult { Success = false, Message = "Phone and text are required" });
        var ok = await whatsapp.SendTextAsync(r.Phone, r.Text, r.OrderId);
        return Ok(new ApiResult { Success = ok, Message = ok ? "Message sent" : "Message could not be sent. Check WhatsApp credentials, and that the customer messaged you in the last 24h (or use a template)." });
    }
}
