using System.Text.Json;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Services;

/// <summary>Port of AuthContext.jsx — the signed-in "Vitality Account" customer.</summary>
public class AuthState(ApiClient api, SessionTokens tokens, BrowserInterop browser)
{
    private const string TokenKey = "nura_vitality_customer_token";
    private const string UserKey = "nura_vitality_customer_data";

    public CustomerDto? Customer { get; private set; }
    public string? Token => tokens.CustomerToken;
    public bool IsAuthenticated => Customer is not null && !string.IsNullOrEmpty(tokens.CustomerToken);
    /// <summary>True until the stored session has been restored/validated on startup.</summary>
    public bool IsLoading { get; private set; } = true;

    public event Action? Changed;
    private bool _initialised;

    public async Task InitAsync()
    {
        if (_initialised) return;
        _initialised = true;

        var token = await browser.GetLocalAsync(TokenKey);
        if (string.IsNullOrEmpty(token)) { IsLoading = false; Changed?.Invoke(); return; }

        tokens.CustomerToken = token;
        var cached = await browser.GetLocalAsync(UserKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try { Customer = JsonSerializer.Deserialize<CustomerDto>(cached, NuraJson.Options); } catch { }
        }
        Changed?.Invoke();

        var me = await api.GetMeAsync();
        if (me.Success && me.Customer is not null) await SetSessionAsync(me.Customer, token);
        else if (me.Message?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true || Customer is null) await SignOutAsync();

        IsLoading = false;
        Changed?.Invoke();
    }

    public async Task SetSessionAsync(CustomerDto customer, string token)
    {
        Customer = customer;
        tokens.CustomerToken = token;
        await browser.SetLocalAsync(TokenKey, token);
        await browser.SetLocalAsync(UserKey, JsonSerializer.Serialize(customer, NuraJson.Options));
        Changed?.Invoke();
    }

    public async Task<AuthResponse> LoginAsync(string emailOrPhone, string password) =>
        await Adopt(await api.LoginAsync(new LoginRequest { EmailOrPhone = emailOrPhone, Password = password }));

    public async Task<AuthResponse> RegisterAsync(RegisterRequest r) => await Adopt(await api.RegisterAsync(r));

    public Task<AuthResponse> SendOtpAsync(string email) => api.SendOtpAsync(email);

    public async Task<AuthResponse> VerifyOtpAsync(string email, string otp) => await Adopt(await api.VerifyOtpAsync(email, otp));

    /// <summary>Adopts a session returned by checkout (account auto-created from the checkout form).</summary>
    public async Task AdoptAsync(AuthResponse? r) { if (r is not null) await Adopt(r); }

    public async Task<CustomerResponse> UpdateProfileAsync(UpdateProfileRequest r)
    {
        var res = await api.UpdateMeAsync(r);
        if (res.Success && res.Customer is not null && tokens.CustomerToken is { } t) await SetSessionAsync(res.Customer, t);
        return res;
    }

    public async Task SignOutAsync()
    {
        Customer = null;
        tokens.CustomerToken = null;
        await browser.RemoveLocalAsync(TokenKey);
        await browser.RemoveLocalAsync(UserKey);
        Changed?.Invoke();
    }

    private async Task<AuthResponse> Adopt(AuthResponse r)
    {
        if (r.Success && r.Customer is not null && !string.IsNullOrEmpty(r.Token)) await SetSessionAsync(r.Customer, r.Token);
        return r;
    }
}

/// <summary>Admin portal session (token lives in sessionStorage, like the React app: nh_admin_token).</summary>
public class AdminSession(ApiClient api, SessionTokens tokens, BrowserInterop browser)
{
    private const string TokenKey = "nh_admin_token";

    public bool IsAuthenticated => !string.IsNullOrEmpty(tokens.AdminToken);
    public string? Email { get; private set; }
    public event Action? Changed;
    private bool _initialised;

    /// <summary>Restores and server-validates a stored admin token.</summary>
    public async Task InitAsync()
    {
        if (_initialised) return;
        _initialised = true;
        var token = await browser.GetSessionAsync(TokenKey);
        if (string.IsNullOrEmpty(token)) return;

        tokens.AdminToken = token;
        var v = await api.AdminVerifyAsync();
        if (v.Success && v.Valid) Email = v.User?.Email;
        else await SignOutAsync();
        Changed?.Invoke();
    }

    public async Task<AdminLoginResponse> LoginAsync(string email, string password)
    {
        var res = await api.AdminLoginAsync(email.Trim(), password);
        if (res.Success && !string.IsNullOrEmpty(res.Token))
        {
            tokens.AdminToken = res.Token;
            Email = res.User?.Email;
            await browser.SetSessionAsync(TokenKey, res.Token);
            Changed?.Invoke();
        }
        return res;
    }

    public async Task SignOutAsync()
    {
        tokens.AdminToken = null;
        Email = null;
        await browser.RemoveSessionAsync(TokenKey);
        Changed?.Invoke();
    }
}
