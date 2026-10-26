using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Services;

public class ApiOptions
{
    /// <summary>Base address of Nuraherbex.Api, e.g. https://nuraapi.apmiot.com/</summary>
    public string BaseUrl { get; set; } = "https://nuraapi.apmiot.com/";
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

    /// <summary>Full address of an API path (used for signed media links that are loaded straight by the browser).</summary>
    public string Url(string relative) => http.BaseAddress is { } b ? new Uri(b, relative).ToString() : "/" + relative;

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
    public Task<ApiResult> ChangePasswordAsync(ChangePasswordRequest r) => Call<ApiResult>(HttpMethod.Put, "api/auth/me/password", r, Who.Customer);
    public Task<OrderListResponse> MyOrdersAsync() => Call<OrderListResponse>(HttpMethod.Get, "api/auth/my-orders", null, Who.Customer);

    // ------------------------------------------------------------------ public content
    /// <summary>Null when the API is unreachable (callers fall back to <see cref="SiteContent.Default"/>).</summary>
    public Task<SiteContent?> GetSiteContentAsync() => GetRaw<SiteContent>("api/content/site");
    public async Task<List<FormulationIngredientDto>?> GetIngredientsAsync() => await GetRaw<List<FormulationIngredientDto>>("api/content/formulation-ingredients");
    public Task<FormulationMetricsDto?> GetMetricsAsync() => GetRaw<FormulationMetricsDto>("api/content/formulation-metrics");
    public async Task<List<TrustBatchDto>> GetBatchesAsync() => (await GetRaw<TrustBatchListResponse>("api/trust-batches"))?.Batches ?? new();
    public Task<TrustBatchResponse> GetBatchAsync(string batchNo) => Call<TrustBatchResponse>(HttpMethod.Get, $"api/trust-batches/{Uri.EscapeDataString(batchNo)}");
    public async Task<List<ReviewDto>> GetReviewsAsync() => (await GetRaw<ReviewListResponse>("api/reviews"))?.Reviews ?? new();
    public Task<ApiResult> SubmitReviewAsync(ReviewDto r) => Call<ApiResult>(HttpMethod.Post, "api/reviews", r, Who.Customer);

    // ------------------------------------------------------------------ admin
    public Task<AdminLoginResponse> AdminLoginAsync(string email, string password) => Call<AdminLoginResponse>(HttpMethod.Post, "api/admin/login", new AdminLoginRequest { Email = email, Password = password });
    public Task<AdminLoginResponse> AdminVerifyAsync() => Call<AdminLoginResponse>(HttpMethod.Get, "api/admin/verify", null, Who.Admin);

    public Task<OrderListResponse> AdminOrdersAsync() => Call<OrderListResponse>(HttpMethod.Get, "api/admin/orders", null, Who.Admin);
    public Task<OrderResponse> AdminCreateOrderAsync(AdminCreateOrderRequest r) => Call<OrderResponse>(HttpMethod.Post, "api/admin/orders", r, Who.Admin);
    public Task<OrderResponse> AdminMarkReadyAsync(string orderId, decimal weightKg, decimal lengthCm, decimal breadthCm, decimal heightCm) => Call<OrderResponse>(HttpMethod.Post, $"api/shipping/{Uri.EscapeDataString(orderId)}/ready", new { weightKg, lengthCm, breadthCm, heightCm }, Who.Admin);
    public Task<ShipmentResponse> AdminRetryShipmentAsync(string orderId) => Call<ShipmentResponse>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/retry-shipment", null, Who.Admin);
    public Task<ShipmentResponse> AdminGenerateShipmentLabelAsync(string orderId) => Call<ShipmentResponse>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/shipping-label", null, Who.Admin);
    public Task<ApiResult> AdminCancelShipmentAsync(string orderId) => Call<ApiResult>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/cancel-shipment", null, Who.Admin);
    public Task<OrderResponse> AdminMarkDeliveredAsync(string orderId) => Call<OrderResponse>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/deliver", null, Who.Admin);
    public Task<ApiResult> AdminSendEmailAsync(string orderId, string type) => Call<ApiResult>(HttpMethod.Post, $"api/admin/orders/{Uri.EscapeDataString(orderId)}/send-email", new AdminEmailRequest { Type = type }, Who.Admin);
    public Task<PaymentAttemptListResponse> AdminPaymentsAsync() => Call<PaymentAttemptListResponse>(HttpMethod.Get, "api/admin/payments", null, Who.Admin);
    public Task<AdminCustomerListResponse> AdminCustomersAsync() => Call<AdminCustomerListResponse>(HttpMethod.Get, "api/admin/customers", null, Who.Admin);
    public Task<ReviewListResponse> AdminReviewsAsync() => Call<ReviewListResponse>(HttpMethod.Get, "api/admin/reviews", null, Who.Admin);
    public Task<ApiResult> AdminModerateReviewAsync(string id, bool approved) => Call<ApiResult>(HttpMethod.Put, $"api/admin/reviews/{Uri.EscapeDataString(id)}", new ReviewModerationRequest { Approved = approved }, Who.Admin);

    public Task<TrustBatchListResponse> AdminBatchesAsync() => Call<TrustBatchListResponse>(HttpMethod.Get, "api/admin/batches", null, Who.Admin);
    public Task<TrustBatchResponse> AdminSaveBatchAsync(TrustBatchDto b) => Call<TrustBatchResponse>(HttpMethod.Put, "api/admin/batches", b, Who.Admin);
    public Task<ApiResult> AdminDeleteBatchAsync(string idOrBatchNo) => Call<ApiResult>(HttpMethod.Delete, $"api/admin/batches/{Uri.EscapeDataString(idOrBatchNo)}", null, Who.Admin);

    public Task<ApiResult> AdminSaveContentAsync(SiteContent c) => Call<ApiResult>(HttpMethod.Put, "api/admin/content", c, Who.Admin);
    public async Task<PageContentDto> GetPageContentAsync() => await GetRaw<PageContentDto>("api/content/page") ?? new();
    public Task<ApiResult> AdminSavePageContentAsync(PageContentDto d) => Call<ApiResult>(HttpMethod.Put, "api/admin/content/page", d, Who.Admin);
    public Task<ApiResult> AdminResetContentAsync() => Call<ApiResult>(HttpMethod.Delete, "api/admin/content", null, Who.Admin);
    public Task<ApiResult> AdminSaveIngredientsAsync(List<FormulationIngredientDto> list) => Call<ApiResult>(HttpMethod.Put, "api/admin/formulation/ingredients", list, Who.Admin);
    public Task<ApiResult> AdminSaveMetricsAsync(FormulationMetricsDto m) => Call<ApiResult>(HttpMethod.Put, "api/admin/formulation/metrics", m, Who.Admin);
    public Task<ProductListResponse> AdminProductsAsync() => Call<ProductListResponse>(HttpMethod.Get, "api/admin/products", null, Who.Admin);

    public Task<WhatsAppStatusDto> AdminWhatsAppStatusAsync() => Call<WhatsAppStatusDto>(HttpMethod.Get, "api/admin/whatsapp/status", null, Who.Admin);
    public Task<WhatsAppMessageListResponse> AdminWhatsAppMessagesAsync(int take = 100) => Call<WhatsAppMessageListResponse>(HttpMethod.Get, $"api/admin/whatsapp/messages?take={take}", null, Who.Admin);
    public Task<ApiResult> AdminWhatsAppSendAsync(WhatsAppSendRequest r) => Call<ApiResult>(HttpMethod.Post, "api/admin/whatsapp/send", r, Who.Admin);

    public Task<LeadListResponse> AdminLeadsAsync(string? status = null, string? source = null, string? q = null, string? assignee = null, int take = 100) =>
        Call<LeadListResponse>(HttpMethod.Get, $"api/admin/leads?take={take}&status={Uri.EscapeDataString(status ?? "")}&source={Uri.EscapeDataString(source ?? "")}&q={Uri.EscapeDataString(q ?? "")}&assignee={Uri.EscapeDataString(assignee ?? "")}", null, Who.Admin);
    public Task<ApiResult> AdminLeadAssignAsync(long id, LeadAssignRequest r) => Call<ApiResult>(HttpMethod.Put, $"api/admin/leads/{id}/assign", r, Who.Admin);
    public Task<ApiResult> AdminLeadReplyAsync(long id, string text) => Call<ApiResult>(HttpMethod.Post, $"api/admin/leads/{id}/reply", new LeadReplyRequest { Text = text }, Who.Admin);
    public Task<EmployeeListResponse> AdminTeamAsync(string? forPage = null) => Call<EmployeeListResponse>(HttpMethod.Get, forPage is null ? "api/admin/team" : $"api/admin/team?for={forPage}", null, Who.Admin);
    public Task<DepartmentListResponse> AdminTeamDepartmentsAsync() => Call<DepartmentListResponse>(HttpMethod.Get, "api/admin/team/departments", null, Who.Admin);
    public Task<EmployeeListResponse> AdminEmployeesAsync() => Call<EmployeeListResponse>(HttpMethod.Get, "api/admin/employees", null, Who.Admin);
    public Task<RightsCatalogResponse> AdminRightsCatalogAsync() => Call<RightsCatalogResponse>(HttpMethod.Get, "api/admin/rights/catalog", null, Who.Admin);
    public Task<DepartmentListResponse> AdminDepartmentsAsync() => Call<DepartmentListResponse>(HttpMethod.Get, "api/admin/departments", null, Who.Admin);
    public Task<ApiResult> AdminSaveDepartmentAsync(long? id, DepartmentSaveRequest r) => Call<ApiResult>(id is null ? HttpMethod.Post : HttpMethod.Put, id is null ? "api/admin/departments" : $"api/admin/departments/{id}", r, Who.Admin);
    public Task<ApiResult> AdminDeleteDepartmentAsync(long id) => Call<ApiResult>(HttpMethod.Delete, $"api/admin/departments/{id}", null, Who.Admin);
    public Task<DesignationListResponse> AdminDesignationsAsync() => Call<DesignationListResponse>(HttpMethod.Get, "api/admin/designations", null, Who.Admin);
    public Task<ApiResult> AdminSaveDesignationAsync(long? id, DesignationSaveRequest r) => Call<ApiResult>(id is null ? HttpMethod.Post : HttpMethod.Put, id is null ? "api/admin/designations" : $"api/admin/designations/{id}", r, Who.Admin);
    public Task<ApiResult> AdminDeleteDesignationAsync(long id) => Call<ApiResult>(HttpMethod.Delete, $"api/admin/designations/{id}", null, Who.Admin);

    public Task<TaskListResponse> AdminTasksAsync(string? scope = null, string? assignee = null, long? department = null, string? priority = null, string? q = null, long? leadId = null, bool createdByMe = false) =>
        Call<TaskListResponse>(HttpMethod.Get, $"api/admin/tasks?scope={Uri.EscapeDataString(scope ?? "")}&assignee={Uri.EscapeDataString(assignee ?? "")}&priority={Uri.EscapeDataString(priority ?? "")}&q={Uri.EscapeDataString(q ?? "")}{(department is { } d ? $"&department={d}" : "")}{(leadId is { } l ? $"&leadId={l}" : "")}{(createdByMe ? "&createdByMe=true" : "")}", null, Who.Admin);
    public Task<TaskDetailResponse> AdminTaskAsync(long id) => Call<TaskDetailResponse>(HttpMethod.Get, $"api/admin/tasks/{id}", null, Who.Admin);
    public Task<TaskDetailResponse> AdminCreateTaskAsync(TaskSaveRequest r) => Call<TaskDetailResponse>(HttpMethod.Post, "api/admin/tasks", r, Who.Admin);
    public Task<ApiResult> AdminUpdateTaskAsync(long id, TaskSaveRequest r) => Call<ApiResult>(HttpMethod.Put, $"api/admin/tasks/{id}", r, Who.Admin);
    public Task<ApiResult> AdminTaskStatusAsync(long id, string status, string? note = null) => Call<ApiResult>(HttpMethod.Put, $"api/admin/tasks/{id}/status", new TaskStatusRequest { Status = status, Note = note }, Who.Admin);
    public Task<ApiResult> AdminDeleteTaskAsync(long id) => Call<ApiResult>(HttpMethod.Delete, $"api/admin/tasks/{id}", null, Who.Admin);
    public Task<ApiResult> AdminTaskCommentAsync(long id, string body) => Call<ApiResult>(HttpMethod.Post, $"api/admin/tasks/{id}/comments", new TaskCommentRequest { Body = body }, Who.Admin);
    public Task<ApiResult> AdminTaskAddItemAsync(long id, string text) => Call<ApiResult>(HttpMethod.Post, $"api/admin/tasks/{id}/checklist", new TaskChecklistRequest { Text = text }, Who.Admin);
    public Task<ApiResult> AdminTaskTickItemAsync(long id, long itemId, bool done) => Call<ApiResult>(HttpMethod.Put, $"api/admin/tasks/{id}/checklist/{itemId}", new TaskChecklistRequest { Done = done }, Who.Admin);
    public Task<ApiResult> AdminTaskRemoveItemAsync(long id, long itemId) => Call<ApiResult>(HttpMethod.Delete, $"api/admin/tasks/{id}/checklist/{itemId}", null, Who.Admin);

    public Task<EmployeeResponse> AdminCreateEmployeeAsync(EmployeeSaveRequest r) => Call<EmployeeResponse>(HttpMethod.Post, "api/admin/employees", r, Who.Admin);
    public Task<EmployeeResponse> AdminUpdateEmployeeAsync(long id, EmployeeSaveRequest r) => Call<EmployeeResponse>(HttpMethod.Put, $"api/admin/employees/{id}", r, Who.Admin);
    public Task<ApiResult> AdminSetEmployeeWhatsAppAsync(long id, bool active) => Call<ApiResult>(HttpMethod.Put, $"api/admin/employees/{id}/whatsapp", new WhatsAppActiveRequest { Active = active }, Who.Admin);
    public Task<ApiResult> AdminRemoveEmployeeAsync(long id, bool spreadLeads) => Call<ApiResult>(HttpMethod.Delete, $"api/admin/employees/{id}?reassign={(spreadLeads ? "auto" : "unassigned")}", null, Who.Admin);
    public Task<ApiResult> AdminLeadSettingsAsync(bool autoAssign) => Call<ApiResult>(HttpMethod.Put, "api/admin/leads/settings", new LeadSettingsRequest { AutoAssign = autoAssign }, Who.Admin);
    public Task<LeadOrderResponse> AdminLeadOrderAsync(long id, LeadOrderRequest r) => Call<LeadOrderResponse>(HttpMethod.Post, $"api/admin/leads/{id}/order", r, Who.Admin);
    public Task<CrmDashboardResponse> AdminCrmDashboardAsync(int days) => Call<CrmDashboardResponse>(HttpMethod.Get, $"api/admin/crm/dashboard?days={days}", null, Who.Admin);
    public Task<ReplySuggestionResponse> AdminSuggestReplyAsync(long id, ReplySuggestRequest r) => Call<ReplySuggestionResponse>(HttpMethod.Post, $"api/admin/leads/{id}/suggest-reply", r, Who.Admin);
    public Task<ReplyAssistantSettingsDto> AdminReplySettingsAsync() => Call<ReplyAssistantSettingsDto>(HttpMethod.Get, "api/admin/ai/reply-settings", null, Who.Admin);
    public Task<ApiResult> AdminSaveReplySettingsAsync(ReplyAssistantSettingsRequest r) => Call<ApiResult>(HttpMethod.Put, "api/admin/ai/reply-settings", r, Who.Admin);
    public Task<ApiResult> AdminLeadConsentAsync(long id, string status) => Call<ApiResult>(HttpMethod.Put, $"api/admin/leads/{id}/consent", new LeadConsentRequest { Status = status }, Who.Admin);

    public Task<FollowUpListResponse> AdminFollowUpsAsync(string? scope = null, string? assignee = null, string? type = null, long? leadId = null) =>
        Call<FollowUpListResponse>(HttpMethod.Get, $"api/admin/followups?scope={Uri.EscapeDataString(scope ?? "")}&assignee={Uri.EscapeDataString(assignee ?? "")}&type={Uri.EscapeDataString(type ?? "")}{(leadId is { } l ? $"&leadId={l}" : "")}", null, Who.Admin);
    public Task<ApiResult> AdminCreateFollowUpAsync(FollowUpCreateRequest r) => Call<ApiResult>(HttpMethod.Post, "api/admin/followups", r, Who.Admin);
    public Task<ApiResult> AdminCompleteFollowUpAsync(long id, FollowUpCompleteRequest r) => Call<ApiResult>(HttpMethod.Put, $"api/admin/followups/{id}/complete", r, Who.Admin);
    public Task<ApiResult> AdminRescheduleFollowUpAsync(long id, FollowUpRescheduleRequest r) => Call<ApiResult>(HttpMethod.Put, $"api/admin/followups/{id}/reschedule", r, Who.Admin);
    public Task<ApiResult> AdminSkipFollowUpAsync(long id) => Call<ApiResult>(HttpMethod.Put, $"api/admin/followups/{id}/skip", new { }, Who.Admin);
    public Task<ApiResult> AdminSendFollowUpAsync(long id) => Call<ApiResult>(HttpMethod.Post, $"api/admin/followups/{id}/send", new { }, Who.Admin);
    public Task<ApiResult> AdminSendDueFollowUpsAsync() => Call<ApiResult>(HttpMethod.Post, "api/admin/followups/send-due", new { }, Who.Admin);
    public Task<WhatsAppLimitDto> AdminFollowUpWhatsAppUsageAsync() => Call<WhatsAppLimitDto>(HttpMethod.Get, "api/admin/followups/whatsapp-usage", null, Who.Admin);
    public Task<ReportListResponse> AdminReportsAsync() => Call<ReportListResponse>(HttpMethod.Get, "api/admin/reports", null, Who.Admin);
    public Task<ReportResponse> AdminReportAsync(string key, string from, string to) => Call<ReportResponse>(HttpMethod.Get, $"api/admin/reports/{Uri.EscapeDataString(key)}?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}", null, Who.Admin);
    public Task<WhatsAppLimitDto> AdminWhatsAppLimitAsync() => Call<WhatsAppLimitDto>(HttpMethod.Get, "api/admin/whatsapp/limit", null, Who.Admin);
    public Task<WhatsAppLimitDto> AdminSaveWhatsAppLimitAsync(WhatsAppLimitRequest r) => Call<WhatsAppLimitDto>(HttpMethod.Put, "api/admin/whatsapp/limit", r, Who.Admin);
    public Task<FollowUpSettingsDto> AdminFollowUpSettingsAsync() => Call<FollowUpSettingsDto>(HttpMethod.Get, "api/admin/followups/settings", null, Who.Admin);
    public Task<ApiResult> AdminSaveFollowUpSettingsAsync(FollowUpSettingsRequest r) => Call<ApiResult>(HttpMethod.Put, "api/admin/followups/settings", r, Who.Admin);
    public Task<FollowUpReportResponse> AdminFollowUpReportAsync(int days) => Call<FollowUpReportResponse>(HttpMethod.Get, $"api/admin/followups/report?days={days}", null, Who.Admin);
    public Task<ApiResult> AdminGoOfflineAsync() => Call<ApiResult>(HttpMethod.Post, "api/admin/presence/offline", null, Who.Admin);
    public Task<WhatsAppActiveResponse> AdminGetWhatsAppActiveAsync() => Call<WhatsAppActiveResponse>(HttpMethod.Get, "api/admin/presence/whatsapp", null, Who.Admin);
    public Task<WhatsAppActiveResponse> AdminSetWhatsAppActiveAsync(bool active) => Call<WhatsAppActiveResponse>(HttpMethod.Put, "api/admin/presence/whatsapp", new WhatsAppActiveRequest { Active = active }, Who.Admin);
    public Task<NotificationListResponse> AdminNotificationsAsync() => Call<NotificationListResponse>(HttpMethod.Get, "api/admin/notifications", null, Who.Admin);
    public Task<ApiResult> AdminReadAllNotificationsAsync() => Call<ApiResult>(HttpMethod.Put, "api/admin/notifications/read-all", new { }, Who.Admin);
    public Task<ApiResult> AdminReadNotificationAsync(long id) => Call<ApiResult>(HttpMethod.Put, $"api/admin/notifications/{id}/read", new { }, Who.Admin);
    public Task<LeadDetailResponse> AdminLeadDetailAsync(long id) => Call<LeadDetailResponse>(HttpMethod.Get, $"api/admin/leads/{id}", null, Who.Admin);
    public Task<ApiResult> AdminLeadStatusAsync(long id, LeadStatusRequest r) => Call<ApiResult>(HttpMethod.Put, $"api/admin/leads/{id}/status", r, Who.Admin);
    public Task<ApiResult> AdminLeadNotesAsync(long id, LeadNotesRequest r) => Call<ApiResult>(HttpMethod.Put, $"api/admin/leads/{id}/notes", r, Who.Admin);
    public Task<AdminProductListResponse> AdminCatalogAsync() => Call<AdminProductListResponse>(HttpMethod.Get, "api/admin/catalog/products", null, Who.Admin);
    public Task<AdminProductResponse> AdminSaveProductAsync(AdminProductDto p, bool create) => Call<AdminProductResponse>(create ? HttpMethod.Post : HttpMethod.Put, "api/admin/catalog/products" + (create ? "" : "/" + Uri.EscapeDataString(p.Id)), p, Who.Admin);
    public Task<ApiResult> AdminArchiveProductAsync(string id) => Call<ApiResult>(HttpMethod.Delete, "api/admin/catalog/products/" + Uri.EscapeDataString(id), null, Who.Admin);
    public Task<AdminCouponListResponse> AdminCouponsAsync() => Call<AdminCouponListResponse>(HttpMethod.Get, "api/admin/catalog/coupons", null, Who.Admin);
    public Task<AdminCouponResponse> AdminSaveCouponAsync(CouponDto c, bool create) => Call<AdminCouponResponse>(create ? HttpMethod.Post : HttpMethod.Put, "api/admin/catalog/coupons" + (create ? "" : "/" + Uri.EscapeDataString(c.Code)), c, Who.Admin);
    public Task<ApiResult> AdminDisableCouponAsync(string code) => Call<ApiResult>(HttpMethod.Delete, "api/admin/catalog/coupons/" + Uri.EscapeDataString(code), null, Who.Admin);
    public Task<CustomerResponse> AdminUpdateCustomerAsync(string id, UpdateProfileRequest r) => Call<CustomerResponse>(HttpMethod.Put, "api/admin/customers/" + Uri.EscapeDataString(id), r, Who.Admin);
}
