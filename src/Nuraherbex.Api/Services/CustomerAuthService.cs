using System.Security.Cryptography;
using System.Text;
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
        var existing = await FindAsync(mail);

        // A previous code must not remain usable when a resend fails. Only activate
        // this code after the email provider has accepted the message.
        cache.Remove(OtpKey(mail));
        var sent = await email.SendOtpAsync(mail, otp, existing?.FullName ?? "Valued Patron");
        if (!sent)
            throw new InvalidOperationException("We couldn't send a login code to that email right now. Please try again shortly. If the problem continues, contact Nura Herbex support.");

        cache.Set(OtpKey(mail), otp, TimeSpan.FromMinutes(10));
        return new AuthResponse { Success = true, Message = $"A 6-digit login code was sent to {mail}. Check your inbox and spam folder.", Email = mail };
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
        if (r.FullName is not null)
        {
            if (string.IsNullOrWhiteSpace(r.FullName) || r.FullName.Trim().Length > 200)
                throw new InvalidOperationException("Full name is required (maximum 200 characters).");
            c.FullName = r.FullName.Trim();
        }
        if (!string.IsNullOrWhiteSpace(r.Phone))
        {
            var phone = Digits(r.Phone);
            if (phone.Length is < 10 or > 15) throw new InvalidOperationException("Enter a phone number with 10–15 digits.");
            if (await db.Customers.AnyAsync(other => other.Id != id && other.Phone == phone))
                throw new InvalidOperationException("This phone number is already used by another account.");
            c.Phone = phone;
        }
        if (r.ProfileImageData is not null)
            c.ProfileImageData = string.IsNullOrEmpty(r.ProfileImageData) ? null : ValidateProfileImageData(r.ProfileImageData);
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

    public async Task ChangePasswordAsync(string id, ChangePasswordRequest request)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword))
            throw new InvalidOperationException("Enter your current password.");
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 8)
            throw new InvalidOperationException("Your new password must contain at least 8 characters.");
        if (Encoding.UTF8.GetByteCount(request.NewPassword) > 72)
            throw new InvalidOperationException("Your new password is too long. Use no more than 72 UTF-8 bytes.");

        var customer = await GetAsync(id) ?? throw new InvalidOperationException("Customer account not found.");
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, customer.PasswordHash))
            throw new InvalidOperationException("Your current password is incorrect.");
        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, customer.PasswordHash))
            throw new InvalidOperationException("Choose a new password that differs from your current password.");

        customer.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        customer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static string ValidateProfileImageData(string dataUrl)
    {
        const int maxBytes = 512 * 1024;
        var separator = dataUrl.IndexOf(',');
        if (separator < 0)
            throw new InvalidOperationException("The profile photo format is invalid.");

        var mime = dataUrl[..separator].ToLowerInvariant() switch
        {
            "data:image/jpeg;base64" => "image/jpeg",
            "data:image/png;base64" => "image/png",
            "data:image/webp;base64" => "image/webp",
            _ => throw new InvalidOperationException("Upload a JPG, PNG, or WebP profile photo."),
        };
        var encoded = dataUrl[(separator + 1)..];
        if (encoded.Length > ((maxBytes + 2) / 3 * 4))
            throw new InvalidOperationException("The compressed profile photo must be 512 KB or smaller.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidOperationException("The profile photo data is invalid."); }
        if (bytes.Length == 0 || bytes.Length > maxBytes)
            throw new InvalidOperationException("The compressed profile photo must be 512 KB or smaller.");

        var valid = mime switch
        {
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/webp" => bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
            _ => false,
        };
        if (!valid) throw new InvalidOperationException("The uploaded file is not a valid supported image.");
        return dataUrl;
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
