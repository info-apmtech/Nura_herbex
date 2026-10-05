using System.Security.Claims;
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
[AllowAnonymous]
[Route("api")]
public class HealthController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Get() => Ok(new
    {
        status = "healthy", timestamp = DateTime.UtcNow, service = "Nura Herbex Stamix Ecommerce API", version = "1.0.0",
    });
}

[ApiController]
[AllowAnonymous]
[Route("api/products")]
public class ProductsController(ProductService products) : ControllerBase
{
    [HttpGet]
    public async Task<ProductListResponse> List() =>
        new() { Success = true, Products = (await products.ListActiveAsync()).Select(p => p.ToDto()).ToList() };
}

[ApiController]
[AllowAnonymous]
[Route("api/orders")]
[EnableRateLimiting("orders")]
public class OrdersController(OrderService orders, ProductService products, CustomerAuthService auth, ShiprocketService shiprocket) : ControllerBase
{
    [HttpPost("validate")]
    public async Task<IActionResult> Validate(ValidateCartRequest req)
    {
        try { return Ok((await products.CalculateAsync(req.Items, req.CouponCode)).Totals); }
        catch (Exception ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderRequest req)
    {
        try
        {
            AuthResponse? customerAuth = null;
            string? customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Optional: create / sign into a Vitality account straight from checkout.
            if (!string.IsNullOrEmpty(req.Customer.Password) && req.Customer.Password.Length >= 6)
            {
                try
                {
                    var existing = await auth.FindAsync(req.Customer.Email);
                    if (existing is null)
                    {
                        customerAuth = await auth.RegisterAsync(new RegisterRequest
                        {
                            FullName = req.Customer.FullName, Email = req.Customer.Email, Phone = req.Customer.Phone,
                            Password = req.Customer.Password, ShippingAddress = req.Shipping,
                        });
                        customerId = customerAuth.Customer!.Id;
                    }
                    else
                    {
                        try
                        {
                            customerAuth = await auth.LoginAsync(new LoginRequest { EmailOrPhone = req.Customer.Email, Password = req.Customer.Password });
                            customerId = customerAuth.Customer!.Id;
                        }
                        catch { customerId = existing.Id; }
                    }
                }
                catch { /* account creation must never block the order */ }
            }

            var order = await orders.CreateAsync(req, customerId, holdUntilPaid: true);
            return StatusCode(201, new CreateOrderResponse { Success = true, OrderId = order.Id, Order = order.ToDto(), CustomerAuth = customerAuth });
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            return BadRequest(new ApiResult { Success = false, Message = ex.Message });
        }
    }

    [HttpGet("track")]
    public async Task<IActionResult> Track([FromQuery] string? query, [FromQuery] string? id)
    {
        var q = query ?? id;
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new ApiResult { Success = false, Message = "Search query is required" });

        var order = await orders.FindByQueryAsync(q);
        if (order is null) return NotFound(new ApiResult { Success = false, Message = "No matching order found" });

        var dto = order.ToDto();
        await EnrichTrackingAsync(dto);
        return Ok(new OrderResponse { Success = true, Order = dto });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var order = await orders.GetAsync(id);
        return order is null ? NotFound(new ApiResult { Success = false, Message = "Order not found" }) : Ok(new OrderResponse { Success = true, Order = order.ToDto() });
    }

    private async Task EnrichTrackingAsync(OrderDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.ShiprocketAwb) && !dto.ShiprocketAwb.StartsWith("SR-PENDING-"))
        {
            dto.ShiprocketTrackUrl = $"https://shiprocket.co/tracking/{dto.ShiprocketAwb}";
            var live = await shiprocket.FetchLiveTrackingAsync(dto.ShiprocketAwb);
            if (live is { } td)
            {
                dto.ShiprocketTracking = td;
                if (td.TryGetProperty("track_url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String) dto.ShiprocketTrackUrl = u.GetString();
            }
        }
        else if (!string.IsNullOrWhiteSpace(dto.ShiprocketOrderId))
            dto.ShiprocketTrackUrl = $"https://shiprocket.co/tracking?order_id={dto.ShiprocketOrderId}";
    }
}

[ApiController]
[AllowAnonymous]
[Route("api/payments")]
public class PaymentsController(PayUService payu, IOptions<StoreOptions> store, ILogger<PaymentsController> log) : ControllerBase
{
    [HttpPost("payu-initiate")]
    public async Task<IActionResult> Initiate(PayUInitiateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.OrderId)) return BadRequest(new ApiResult { Success = false, Message = "orderId is required" });
        try
        {
            var publicBase = string.IsNullOrWhiteSpace(store.Value.ApiPublicUrl) ? $"{Request.Scheme}://{Request.Host}" : store.Value.ApiPublicUrl.TrimEnd('/');
            return Ok(await payu.InitiateAsync(req.OrderId, publicBase));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    /// <summary>PayU form-POSTs here (surl/furl + webhook). We verify, settle, then send the browser to the web app.</summary>
    [HttpPost("payu-response")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> PayUResponse([FromForm] Dictionary<string, string> form)
    {
        var front = store.Value.FrontendUrl.TrimEnd('/');
        try
        {
            var order = await payu.ProcessResponseAsync(form);
            return Redirect($"{front}/checkout?status=confirmed&orderId={Uri.EscapeDataString(order.Id)}");
        }
        catch (Exception ex)
        {
            log.LogWarning("[PayU] Callback failed: {Msg}", ex.Message);
            return Redirect($"{front}/checkout?status=failed&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify(Dictionary<string, string> body)
    {
        try
        {
            var order = await payu.ProcessResponseAsync(body);
            return Ok(new OrderResponse { Success = true, Message = "Payment verified successfully via PayU", Order = order.ToDto() });
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }
}

[ApiController]
[Route("api/auth")]
public class AuthController(CustomerAuthService auth, OrderService orders, ShiprocketService shiprocket) : ControllerBase
{
    private string? CurrentId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task<IActionResult> Run(Func<Task<AuthResponse>> action, int failStatus = 400)
    {
        try { return Ok(await action()); }
        catch (InvalidOperationException ex) { return StatusCode(failStatus, new AuthResponse { Success = false, Message = ex.Message }); }
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest r)
    {
        try { return StatusCode(201, await auth.RegisterAsync(r)); }
        catch (InvalidOperationException ex) { return BadRequest(new AuthResponse { Success = false, Message = ex.Message }); }
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public Task<IActionResult> Login(LoginRequest r) => Run(() => auth.LoginAsync(r), 401);

    [AllowAnonymous]
    [HttpPost("send-otp")]
    public Task<IActionResult> SendOtp(SendOtpRequest r) => Run(() => auth.SendOtpAsync(r.Email));

    [AllowAnonymous]
    [HttpPost("verify-otp")]
    public Task<IActionResult> VerifyOtp(VerifyOtpRequest r) => Run(() => auth.VerifyOtpAsync(r.Email, r.Otp), 401);

    [AllowAnonymous]
    [HttpGet("check-user")]
    public async Task<CheckUserResponse> CheckUser([FromQuery] string? query, [FromQuery] string? email, [FromQuery] string? phone)
    {
        var found = await auth.FindAsync(query ?? email ?? phone);
        return new CheckUserResponse { Exists = found is not null, FullName = found?.FullName ?? "" };
    }

    [HttpGet("me"), Authorize(Roles = "customer")]
    public async Task<IActionResult> Me()
    {
        var c = await auth.GetAsync(CurrentId ?? "");
        return c is null ? NotFound(new ApiResult { Success = false, Message = "Customer account not found" }) : Ok(new CustomerResponse { Success = true, Customer = c.ToDto() });
    }

    [HttpPut("me"), Authorize(Roles = "customer")]
    public async Task<IActionResult> UpdateMe(UpdateProfileRequest r)
    {
        try { return Ok(new CustomerResponse { Success = true, Message = "Profile updated successfully", Customer = await auth.UpdateProfileAsync(CurrentId ?? "", r) }); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    [HttpGet("my-orders"), Authorize(Roles = "customer")]
    public async Task<IActionResult> MyOrders()
    {
        var c = await auth.GetAsync(CurrentId ?? "");
        if (c is null) return NotFound(new ApiResult { Success = false, Message = "Customer not found" });

        var list = (await orders.ListForCustomerAsync(c.Email, c.Phone)).Select(o => o.ToDto()).ToList();
        foreach (var dto in list)
        {
            if (!string.IsNullOrWhiteSpace(dto.ShiprocketAwb) && !dto.ShiprocketAwb.StartsWith("SR-PENDING-"))
            {
                dto.ShiprocketTrackUrl = $"https://shiprocket.co/tracking/{dto.ShiprocketAwb}";
                dto.ShiprocketTracking = await shiprocket.FetchLiveTrackingAsync(dto.ShiprocketAwb);
            }
            else if (!string.IsNullOrWhiteSpace(dto.ShiprocketOrderId))
                dto.ShiprocketTrackUrl = $"https://shiprocket.co/tracking?order_id={dto.ShiprocketOrderId}";
        }
        return Ok(new OrderListResponse { Success = true, Count = list.Count, Orders = list });
    }
}

// ---------------------------------------------------------------------------
// Public content: Trust Passport batches, site copy, formulation, reviews
// ---------------------------------------------------------------------------
[ApiController]
[AllowAnonymous]
[Route("api/trust-batches")]
public class TrustBatchesController(NuraDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<TrustBatchListResponse> List() => new()
    {
        Success = true,
        Batches = (await db.TrustBatches.AsNoTracking().Where(b => b.IsActive).OrderByDescending(b => b.CreatedAt).ToListAsync()).Select(b => b.ToDto()).ToList(),
    };

    [HttpGet("{batchNo}")]
    public async Task<IActionResult> Get(string batchNo)
    {
        var key = batchNo.Trim().ToUpperInvariant();
        var b = await db.TrustBatches.AsNoTracking().FirstOrDefaultAsync(x => x.IsActive && (x.BatchNo.ToUpper() == key || x.Id.ToUpper() == key));
        return b is null ? NotFound(new ApiResult { Success = false, Message = "Batch not found. Please check the batch number printed on your canister." })
                         : Ok(new TrustBatchResponse { Success = true, Batch = b.ToDto() });
    }
}

[ApiController]
[AllowAnonymous]
[Route("api/content")]
public class ContentController(SettingsService settings) : ControllerBase
{
    [HttpGet("site")]
    public async Task<SiteContent> Site() => await settings.GetAsync<SiteContent>(SettingsService.SiteContentKey) ?? SiteContent.Default();

    [HttpGet("formulation-ingredients")]
    public async Task<List<FormulationIngredientDto>> Ingredients()
    {
        var saved = await settings.GetAsync<List<FormulationIngredientDto>>(SettingsService.IngredientsKey);
        return saved is { Count: > 0 } ? saved : SettingsService.DefaultIngredients();
    }

    [HttpGet("formulation-metrics")]
    public async Task<FormulationMetricsDto> Metrics() => await settings.GetAsync<FormulationMetricsDto>(SettingsService.MetricsKey) ?? new FormulationMetricsDto();
}

[ApiController]
[AllowAnonymous]
[Route("api/reviews")]
public class ReviewsController(NuraDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ReviewListResponse> List() => new()
    {
        Success = true,
        Reviews = (await db.Reviews.AsNoTracking().Where(r => r.Approved).OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync()).Select(r => r.ToDto()).ToList(),
    };

    [HttpPost]
    [EnableRateLimiting("orders")]
    public async Task<IActionResult> Create(ReviewDto r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Body))
            return BadRequest(new ApiResult { Success = false, Message = "Name and review text are required" });
        db.Reviews.Add(new Review
        {
            Name = r.Name.Trim(), City = r.City?.Trim(), Rating = Math.Clamp(r.Rating, 1, 5), Title = r.Title?.Trim(), Body = r.Body.Trim(),
            Approved = false, // moderated before publishing
        });
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = "Thank you! Your review has been submitted for verification." });
    }
}
