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
    public string Name { get; private set; } = "";
    /// <summary>admin | sales. Sales users only see (and reply to) the leads assigned to them.</summary>
    public string Role { get; private set; } = "admin";
    public bool IsAdmin => Role == "admin";
    /// <summary>Set for employee logins (null for the configured super administrator).</summary>
    public long? EmployeeId { get; private set; }
    public string? Department { get; private set; }
    public string? Designation { get; private set; }
    private Dictionary<string, int> _rights = new();
    /// <summary>Access level for a page: 0 none, 1 own records / view only, 2 everyone's records / view and change.</summary>
    public int Level(string page) => IsAdmin ? 2 : _rights.GetValueOrDefault(page);
    public bool Can(string page, int level = 1) => Level(page) >= level;
    /// <summary>Sees everyone's records on this page, not only their own.</summary>
    public bool CanAll(string page) => Level(page) >= 2;
    public bool HasAnyPage => IsAdmin || _rights.Values.Any(v => v > 0);
    public event Action? Changed;
    private bool _initialised;

    private void Adopt(AdminUserDto? u)
    {
        Email = u?.Email;
        Name = string.IsNullOrWhiteSpace(u?.Name) ? u?.Email ?? "" : u!.Name;
        Role = u?.Role == "sales" ? "sales" : "admin";
        EmployeeId = u?.EmployeeId;
        Department = u?.Department;
        Designation = u?.Designation;
        _rights = u?.Rights is { } r ? new Dictionary<string, int>(r) : new();
    }

    /// <summary>Restores and server-validates a stored admin token.</summary>
    public async Task InitAsync()
    {
        if (_initialised) return;
        _initialised = true;
        var token = await browser.GetSessionAsync(TokenKey);
        if (string.IsNullOrEmpty(token)) return;

        tokens.AdminToken = token;
        var v = await api.AdminVerifyAsync();
        if (v.Success && v.Valid) Adopt(v.User);
        else await SignOutAsync();
        Changed?.Invoke();
    }

    public async Task<AdminLoginResponse> LoginAsync(string email, string password)
    {
        var res = await api.AdminLoginAsync(email.Trim(), password);
        if (res.Success && !string.IsNullOrEmpty(res.Token))
        {
            tokens.AdminToken = res.Token;
            Adopt(res.User);
            await browser.SetSessionAsync(TokenKey, res.Token);
            Changed?.Invoke();
        }
        return res;
    }

    public async Task SignOutAsync()
    {
        if (tokens.AdminToken is not null) { try { await api.AdminGoOfflineAsync(); } catch { } } // stop auto-assigned work right away
        tokens.AdminToken = null;
        Adopt(null);
        Email = null;
        await browser.RemoveSessionAsync(TokenKey);
        Changed?.Invoke();
    }
}
