using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Services;

public class ApiOptions
{
    /// <summary>Base address of Nuraherbex.Api, e.g. http://localhost:5118/</summary>
    public string BaseUrl { get; set; } = "http://localhost:5118/";
}

/// <summary>Holds the bearer tokens the ApiClient attaches. Persistence is handled by AuthState / AdminSession.</summary>
public class SessionTokens
{
    public string? CustomerToken { get; set; }
    public string? AdminToken { get; set; }
}

/// <summary>
/// Typed client for Nuraherbex.Api. Every method returns a result object — failures (HTTP errors, offline) are
/// reported as <c>Success=false</c> with a human-readable <c>Message</c>; methods do not throw.
/// </summary>
public class ApiClient(HttpClient http, SessionTokens tokens)
{
    private static readonly JsonSerializerOptions J = NuraJson.Options;

    private enum Who { None, Customer, Admin }

    // ------------------------------------------------------------------ plumbing
    private async Task<T> Call<T>(HttpMethod method, string path, object? body = null, Who who = Who.None) where T : ApiResult, new()
    {
        try
        {
            using var req = new HttpRequestMessage(method, path);
            var token = who == Who.Admin ? tokens.AdminToken : who == Who.Customer ? tokens.CustomerToken : null;
            if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), J), System.Text.Encoding.UTF8, "application/json");

            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();

            T? parsed = default;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { parsed = JsonSerializer.Deserialize<T>(text, J); } catch { /* non-JSON error page */ }
            }

            if (parsed is null)
                return new T { Success = res.IsSuccessStatusCode, Message = res.IsSuccessStatusCode ? null : Describe((int)res.StatusCode) };

            if (!res.IsSuccessStatusCode)
            {
                parsed.Success = false;
                parsed.Message ??= Describe((int)res.StatusCode);
            }
            else if (!HasSuccessFlag(text)) parsed.Success = true; // endpoints that return bare payloads (e.g. /orders/validate adds success anyway)
            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new T { Success = false, Message = "Unable to reach the server. Please check your connection and try again." };
        }
    }

    private static bool HasSuccessFlag(string json)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("success", out _); }
        catch { return false; }
    }

    private static string Describe(int status) => status switch
    {
        401 => "Your session has expired. Please sign in again.",
        403 => "You do not have permission to do that.",
        404 => "Not found.",
        429 => "Too many requests. Please try again in a minute.",
        >= 500 => "The server hit a problem. Please try again shortly.",
        _ => "Request failed.",
    };

    private async Task<T?> GetRaw<T>(string path) where T : class
    {
        try { return await http.GetFromJsonAsync<T>(path, J); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ catalog / cart / orders
    public Task<ProductListResponse> GetCatalogAsync() => Call<ProductListResponse>(HttpMethod.Get, "api/products");
    public async Task<List<ProductDto>> GetProductsAsync() => (await Call<ProductListResponse>(HttpMethod.Get, "api/products")).Products;

    public Task<OrderTotalsDto> ValidateCartAsync(ValidateCartRequest req) => Call<OrderTotalsDto>(HttpMethod.Post, "api/orders/validate", req);

    /// <summary>Creates an order. If a customer is signed in their token is sent so the order is linked to the account.</summary>
    public Task<CreateOrderResponse> CreateOrderAsync(CreateOrderRequest req) => Call<CreateOrderResponse>(HttpMethod.Post, "api/orders", req, Who.Customer);

    public Task<OrderResponse> GetOrderAsync(string id) => Call<OrderResponse>(HttpMethod.Get, $"api/orders/{Uri.EscapeDataString(id)}");

    /// <summary>Track by order id, phone number or AWB.</summary>
    public Task<OrderResponse> TrackOrderAsync(string query) => Call<OrderResponse>(HttpMethod.Get, $"api/orders/track?query={Uri.EscapeDataString(query)}");

    // ------------------------------------------------------------------ payments
    public Task<PayUInitiateResponse> PayUInitiateAsync(string orderId) => Call<PayUInitiateResponse>(HttpMethod.Post, "api/payments/payu-initiate", new PayUInitiateRequest { OrderId = orderId });

    /// <summary>Settles a PayU result (or the simulated one in dev). Body = PayU callback fields.</summary>
    public Task<OrderResponse> PayUVerifyAsync(Dictionary<string, string> fields) => Call<OrderResponse>(HttpMethod.Post, "api/payments/verify", fields);

    // ------------------------------------------------------------------ customer auth
    public Task<AuthResponse> LoginAsync(LoginRequest r) => Call<AuthResponse>(HttpMethod.Post, "api/auth/login", r);
    public Task<AuthResponse> RegisterAsync(RegisterRequest r) => Call<AuthResponse>(HttpMethod.Post, "api/auth/register", r);
    public Task<AuthResponse> SendOtpAsync(string email) => Call<AuthResponse>(HttpMethod.Post, "api/auth/send-otp", new SendOtpRequest { Email = email });
    public Task<AuthResponse> VerifyOtpAsync(string email, string otp) => Call<AuthResponse>(HttpMethod.Post, "api/auth/verify-otp", new VerifyOtpRequest { Email = email, Otp = otp });
    public async Task<PincodeCheckResponse> CheckPincodeAsync(string pincode) => await GetRaw<PincodeCheckResponse>($"api/orders/serviceability/{Uri.EscapeDataString(pincode)}") ?? new();
    public async Task<CheckUserResponse> CheckUserAsync(string query) => await GetRaw<CheckUserResponse>($"api/auth/check-user?query={Uri.EscapeDataString(query)}") ?? new();
    public Task<CustomerResponse> GetMeAsync() => Call<CustomerResponse>(HttpMethod.Get, "api/auth/me", null, Who.Customer);
    public Task<CustomerResponse> UpdateMeAsync(UpdateProfileRequest r) => Call<CustomerResponse>(HttpMethod.Put, "api/auth/me", r, Who.Customer);
    public Task<OrderListResponse> MyOrdersAsync() => Call<OrderListResponse>(HttpMethod.Get, "api/auth/my-orders", null, Who.Customer);

    // ------------------------------------------------------------------ public content
    /// <summary>Null when the API is unreachable (callers fall back to <see cref="SiteContent.Default"/>).</summary>
    public Task<SiteContent?> GetSiteContentAsync() => GetRaw<SiteContent>("api/content/site");
    public async Task<List<FormulationIngredientDto>?> GetIngredientsAsync() => await GetRaw<List<FormulationIngredientDto>>("api/content/formulation-ingredients");
    public Task<FormulationMetricsDto?> GetMetricsAsync() => GetRaw<FormulationMetricsDto>("api/content/formulation-metrics");
    public async Task<List<TrustBatchDto>> GetBatchesAsync() => (await GetRaw<TrustBatchListResponse>("api/trust-batches"))?.Batches ?? new();
    public Task<TrustBatchResponse> GetBatchAsync(string batchNo) => Call<TrustBatchResponse>(HttpMethod.Get, $"api/trust-batches/{Uri.EscapeDataString(batchNo)}");
    public async Task<List<ReviewDto>> GetReviewsAsync() => (await GetRaw<ReviewListResponse>("api/reviews"))?.Reviews ?? new();
    public Task<ApiResult> SubmitReviewAsync(ReviewDto r) => Call<ApiResult>(HttpMethod.Post, "api/reviews", r);

    // ------------------------------------------------------------------ admin
    public Task<AdminLoginResponse> AdminLoginAsync(string email, string password) => Call<AdminLoginResponse>(HttpMethod.Post, "api/admin/login", new AdminLoginRequest { Email = email, Password = password });
    public Task<AdminLoginResponse> AdminVerifyAsync() => Call<AdminLoginResponse>(HttpMethod.Get, "api/admin/verify", null, Who.Admin);

    public Task<OrderListResponse> AdminOrdersAsync() => Call<OrderListResponse>(HttpMethod.Get, "api/admin/orders", null, Who.Admin);
    public Task<OrderResponse> AdminCreateOrderAsync(AdminCreateOrderRequest r) => Call<OrderResponse>(HttpMethod.Post, "api/admin/orders", r, Who.Admin);
    public Task<ShipmentResponse> AdminRetryShiprocketAsync(string orderId) => Call<ShipmentResponse>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/retry-shiprocket", null, Who.Admin);
    public Task<ApiResult> AdminCancelShipmentAsync(string orderId) => Call<ApiResult>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/cancel-shipment", null, Who.Admin);
    public Task<OrderResponse> AdminMarkDeliveredAsync(string orderId) => Call<OrderResponse>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/deliver", null, Who.Admin);
    public Task<ApiResult> AdminSendEmailAsync(string orderId, string type) => Call<ApiResult>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/send-email", new AdminEmailRequest { Type = type }, Who.Admin);
    public Task<PaymentAttemptListResponse> AdminPaymentsAsync() => Call<PaymentAttemptListResponse>(HttpMethod.Get, "api/admin/payments", null, Who.Admin);
    public Task<AdminCustomerListResponse> AdminCustomersAsync() => Call<AdminCustomerListResponse>(HttpMethod.Get, "api/admin/customers", null, Who.Admin);

    public Task<TrustBatchListResponse> AdminBatchesAsync() => Call<TrustBatchListResponse>(HttpMethod.Get, "api/admin/batches", null, Who.Admin);
    public Task<TrustBatchResponse> AdminSaveBatchAsync(TrustBatchDto b) => Call<TrustBatchResponse>(HttpMethod.Put, "api/admin/batches", b, Who.Admin);
    public Task<ApiResult> AdminDeleteBatchAsync(string idOrBatchNo) => Call<ApiResult>(HttpMethod.Delete, $"api/admin/batches/{Uri.EscapeDataString(idOrBatchNo)}", null, Who.Admin);

    public Task<ApiResult> AdminSaveContentAsync(SiteContent c) => Call<ApiResult>(HttpMethod.Put, "api/admin/content", c, Who.Admin);
    public Task<ApiResult> AdminResetContentAsync() => Call<ApiResult>(HttpMethod.Delete, "api/admin/content", null, Who.Admin);
    public Task<ApiResult> AdminSaveIngredientsAsync(List<FormulationIngredientDto> list) => Call<ApiResult>(HttpMethod.Put, "api/admin/formulation/ingredients", list, Who.Admin);
    public Task<ApiResult> AdminSaveMetricsAsync(FormulationMetricsDto m) => Call<ApiResult>(HttpMethod.Put, "api/admin/formulation/metrics", m, Who.Admin);
    public Task<ProductListResponse> AdminProductsAsync() => Call<ProductListResponse>(HttpMethod.Get, "api/admin/products", null, Who.Admin);

    public Task<WhatsAppStatusDto> AdminWhatsAppStatusAsync() => Call<WhatsAppStatusDto>(HttpMethod.Get, "api/admin/whatsapp/status", null, Who.Admin);
    public Task<WhatsAppMessageListResponse> AdminWhatsAppMessagesAsync(int take = 100) => Call<WhatsAppMessageListResponse>(HttpMethod.Get, $"api/admin/whatsapp/messages?take={take}", null, Who.Admin);
    public Task<ApiResult> AdminWhatsAppSendAsync(WhatsAppSendRequest r) => Call<ApiResult>(HttpMethod.Post, "api/admin/whatsapp/send", r, Who.Admin);
    public Task<AdminProductListResponse> AdminCatalogAsync() => Call<AdminProductListResponse>(HttpMethod.Get, "api/admin/catalog/products", null, Who.Admin);
    public Task<AdminProductResponse> AdminSaveProductAsync(AdminProductDto p, bool create) => Call<AdminProductResponse>(create ? HttpMethod.Post : HttpMethod.Put, "api/admin/catalog/products" + (create ? "" : "/" + Uri.EscapeDataString(p.Id)), p, Who.Admin);
    public Task<ApiResult> AdminArchiveProductAsync(string id) => Call<ApiResult>(HttpMethod.Delete, "api/admin/catalog/products/" + Uri.EscapeDataString(id), null, Who.Admin);
    public Task<AdminCouponListResponse> AdminCouponsAsync() => Call<AdminCouponListResponse>(HttpMethod.Get, "api/admin/catalog/coupons", null, Who.Admin);
    public Task<AdminCouponResponse> AdminSaveCouponAsync(CouponDto c, bool create) => Call<AdminCouponResponse>(create ? HttpMethod.Post : HttpMethod.Put, "api/admin/catalog/coupons" + (create ? "" : "/" + Uri.EscapeDataString(c.Code)), c, Who.Admin);
    public Task<ApiResult> AdminDisableCouponAsync(string code) => Call<ApiResult>(HttpMethod.Delete, "api/admin/catalog/coupons/" + Uri.EscapeDataString(code), null, Who.Admin);
    public Task<CustomerResponse> AdminUpdateCustomerAsync(string id, UpdateProfileRequest r) => Call<CustomerResponse>(HttpMethod.Put, "api/admin/customers/" + Uri.EscapeDataString(id), r, Who.Admin);
}
