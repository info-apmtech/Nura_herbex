using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public class CustomerAuthService(NuraDbContext db, TokenService tokens, EmailService email, IMemoryCache cache, OrderService orders)
{
    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    public async Task<Customer?> FindAsync(string? query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return null;
        var d = Digits(q);
        return await db.Customers.FirstOrDefaultAsync(c => c.Email == q || (d.Length >= 10 && c.Phone == d));
    }

    public Task<Customer?> GetAsync(string id) => db.Customers.FirstOrDefaultAsync(c => c.Id == id);

    public async Task<AuthResponse> RegisterAsync(RegisterRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.FullName)) throw new InvalidOperationException("Full name is required");
        if (string.IsNullOrWhiteSpace(r.Email)) throw new InvalidOperationException("Email address is required");
        if (string.IsNullOrWhiteSpace(r.Phone)) throw new InvalidOperationException("Phone number is required");
        if (string.IsNullOrEmpty(r.Password) || r.Password.Length < 6) throw new InvalidOperationException("Password must be at least 6 characters");

        var mail = r.Email.Trim().ToLowerInvariant();
        var phone = Digits(r.Phone);
        if (await db.Customers.AnyAsync(c => c.Email == mail))
            throw new InvalidOperationException("An account with this email address already exists. Please sign in instead.");
        if (await db.Customers.AnyAsync(c => c.Phone == phone))
            throw new InvalidOperationException("An account with this phone number already exists. Please sign in instead.");

        var customer = new Customer
        {
            Id = $"CUST-{Random.Shared.Next(100000, 999999)}", FullName = r.FullName.Trim(), Email = mail, Phone = phone,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Password), ShippingAddress = r.ShippingAddress ?? new(),
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return new AuthResponse { Success = true, Message = "Welcome to Nura Herbex! Your account has been created.", Customer = customer.ToDto(), Token = tokens.CreateCustomerToken(customer) };
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.EmailOrPhone)) throw new InvalidOperationException("Email or phone number is required");
        if (string.IsNullOrEmpty(r.Password)) throw new InvalidOperationException("Password is required");

        var c = await FindAsync(r.EmailOrPhone) ?? throw new InvalidOperationException("No account found with this email or phone number. Please create an account.");
        if (!BCrypt.Net.BCrypt.Verify(r.Password, c.PasswordHash)) throw new InvalidOperationException("Incorrect password. Please verify and try again.");
        return new AuthResponse { Success = true, Message = "Signed in successfully", Customer = c.ToDto(), Token = tokens.CreateCustomerToken(c) };
    }

    // ---- Email OTP -----------------------------------------------------------------

    private static string OtpKey(string email) => $"otp:{email}";

    public async Task<AuthResponse> SendOtpAsync(string? emailAddress)
    {
        var mail = (emailAddress ?? "").Trim().ToLowerInvariant();
        if (mail.Length == 0) throw new InvalidOperationException("Email address is required");
        if (!System.Text.RegularExpressions.Regex.IsMatch(mail, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
            throw new InvalidOperationException("Please enter a valid email address");

        var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        cache.Set(OtpKey(mail), otp, TimeSpan.FromMinutes(10));

        var existing = await FindAsync(mail);
        await email.SendOtpAsync(mail, otp, existing?.FullName ?? "Valued Patron");
        return new AuthResponse { Success = true, Message = $"A 6-digit login code has been sent to {mail}", Email = mail };
    }

    public async Task<AuthResponse> VerifyOtpAsync(string? emailAddress, string? otp)
    {
        if (string.IsNullOrWhiteSpace(emailAddress) || string.IsNullOrWhiteSpace(otp)) throw new InvalidOperationException("Email and verification code are required");
        var mail = emailAddress.Trim().ToLowerInvariant();

        if (!cache.TryGetValue(OtpKey(mail), out string? expected))
            throw new InvalidOperationException("No active verification code found for this email (or it has expired). Please click Resend Code.");
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected ?? ""), System.Text.Encoding.UTF8.GetBytes(otp.Trim())))
            throw new InvalidOperationException("Invalid verification code. Please check and try again.");
        cache.Remove(OtpKey(mail));

        var c = await FindAsync(mail);
        if (c is null)
        {
            var prefix = mail.Split('@')[0].Replace('.', ' ').Replace('_', ' ');
            c = new Customer
            {
                Id = $"CUST-{Random.Shared.Next(100000, 999999)}", FullName = char.ToUpper(prefix[0]) + prefix[1..], Email = mail, Phone = "",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))),
            };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
        }
        return new AuthResponse { Success = true, Message = "Email verified. Signed in successfully!", Customer = c.ToDto(), Token = tokens.CreateCustomerToken(c) };
    }

    public async Task<CustomerDto> UpdateProfileAsync(string id, UpdateProfileRequest r)
    {
        var c = await GetAsync(id) ?? throw new InvalidOperationException("Customer not found");
        if (!string.IsNullOrWhiteSpace(r.FullName)) c.FullName = r.FullName.Trim();
        if (!string.IsNullOrWhiteSpace(r.Phone)) c.Phone = Digits(r.Phone);
        if (r.ShippingAddress is { } a)
        {
            var cur = c.ShippingAddress;
            c.ShippingAddress = new AddressDto
            {
                AddressLine1 = string.IsNullOrWhiteSpace(a.AddressLine1) ? cur.AddressLine1 : a.AddressLine1,
                AddressLine2 = a.AddressLine2 ?? cur.AddressLine2,
                City = string.IsNullOrWhiteSpace(a.City) ? cur.City : a.City,
                State = string.IsNullOrWhiteSpace(a.State) ? cur.State : a.State,
                Pincode = string.IsNullOrWhiteSpace(a.Pincode) ? cur.Pincode : a.Pincode,
                Country = string.IsNullOrWhiteSpace(a.Country) ? cur.Country : a.Country,
            };
        }
        c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return c.ToDto();
    }

    public async Task<List<AdminCustomerDto>> ListForAdminAsync()
    {
        var customers = await db.Customers.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync();
        var all = await orders.ListAllAsync();
        return customers.Select(c =>
        {
            var mine = all.Where(o => (c.Email != "" && o.CustomerEmail == c.Email) || (c.Phone != "" && o.CustomerPhone == c.Phone)).ToList();
            var dto = c.ToDto();
            return new AdminCustomerDto
            {
                Id = dto.Id, FullName = dto.FullName, Email = dto.Email, Phone = dto.Phone, ShippingAddress = dto.ShippingAddress,
                CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt,
                OrdersCount = mine.Count, TotalSpent = mine.Sum(o => o.TotalAmount),
                LastOrderDate = mine.FirstOrDefault()?.CreatedAt, LastOrderId = mine.FirstOrDefault()?.Id,
                Orders = mine.Select(o => o.ToDto()).ToList(),
            };
        }).ToList();
    }
}
