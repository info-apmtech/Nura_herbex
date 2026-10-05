using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>Transactional email through Resend. Silently logs when no API key is configured.</summary>
public class EmailService(HttpClient http, IOptions<EmailOptions> email, IOptions<StoreOptions> store, ILogger<EmailService> log)
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    public async Task<bool> SendOrderConfirmationAsync(Order o)
    {
        var rows = new StringBuilder();
        foreach (var it in o.Items)
            rows.Append($"<tr><td style=\"padding:8px 0;color:#e9e3f5\">{H(it.Name)} × {it.Quantity}</td><td align=\"right\" style=\"padding:8px 0;color:#fff3bf\">₹{it.Total:N0}</td></tr>");

        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:22px;margin:0 0 8px">Order confirmed, {H(o.CustomerName.Split(' ')[0])}.</h1>
            <p style="color:#c9bfe0;margin:0 0 20px">Thank you for choosing Stamix™. Your order <b style="color:#fff3bf">{H(o.Id)}</b> has been received.</p>
            <table width="100%" cellpadding="0" cellspacing="0" style="border-top:1px solid #2b1f44;border-bottom:1px solid #2b1f44">{rows}
              <tr><td style="padding:8px 0;color:#9d90bd">Shipping</td><td align="right" style="color:#9d90bd">{(o.ShippingFee == 0 ? "FREE" : $"₹{o.ShippingFee:N0}")}</td></tr>
              <tr><td style="padding:8px 0;color:#fff;font-weight:bold">Total ({H(o.PaymentMethod == "COD" ? "Cash on Delivery" : "Paid online")})</td><td align="right" style="color:#fff3bf;font-weight:bold">₹{o.TotalAmount:N0}</td></tr>
            </table>
            <p style="color:#c9bfe0;margin:20px 0 4px">Delivering to:</p>
            <p style="color:#fff;margin:0">{H(o.ShippingAddress.AddressLine1)} {H(o.ShippingAddress.AddressLine2)}, {H(o.ShippingAddress.City)}, {H(o.ShippingAddress.State)} – {H(o.ShippingAddress.Pincode)}</p>
            <p style="margin:24px 0 0"><a href="{store.Value.FrontendUrl}/track" style="background:#9929ea;color:#fff;text-decoration:none;padding:12px 22px;border-radius:999px;font-weight:bold">Track your order</a></p>
            """;
        return await SendAsync(o.CustomerEmail, $"Order Confirmed: {o.Id} | STAMIX™ Botanical Vitality Formula", Wrap(body));
    }

    public async Task<bool> SendOrderDeliveredAsync(Order o)
    {
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:22px;margin:0 0 8px">Your Stamix™ has arrived.</h1>
            <p style="color:#c9bfe0;margin:0 0 16px">Order <b style="color:#fff3bf">{H(o.Id)}</b> was delivered. Consistency compounds — two 10 g scoops a day, every day.</p>
            <p style="color:#c9bfe0;margin:0 0 20px">How was your experience? Your honest review helps other men decide.</p>
            <p style="margin:0"><a href="{store.Value.FrontendUrl}/#reviews" style="background:#9929ea;color:#fff;text-decoration:none;padding:12px 22px;border-radius:999px;font-weight:bold">Leave a review</a></p>
            """;
        return await SendAsync(o.CustomerEmail, "Your STAMIX™ has been delivered! How was your experience? ⭐⭐⭐⭐⭐", Wrap(body));
    }

    public async Task<bool> SendOtpAsync(string to, string otp, string name)
    {
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:20px;margin:0 0 8px">Hello {H(name)},</h1>
            <p style="color:#c9bfe0;margin:0 0 18px">Use this code to sign in to your Vitality Account. It expires in 10 minutes.</p>
            <div style="font-size:34px;letter-spacing:10px;font-weight:bold;color:#fff3bf;background:#140f22;border:1px solid #2b1f44;border-radius:14px;padding:16px;text-align:center">{H(otp)}</div>
            <p style="color:#9d90bd;margin:18px 0 0;font-size:13px">If you didn't request this, you can safely ignore this email.</p>
            """;
        return await SendAsync(to, $"{otp} is your Nura Herbex Vitality Login Code", Wrap(body));
    }

    private string Wrap(string inner) => $"""
        <!doctype html><html><body style="margin:0;background:#07050b;padding:24px;font-family:Georgia,'EB Garamond',serif">
        <table width="100%" cellpadding="0" cellspacing="0"><tr><td align="center">
        <table width="560" cellpadding="0" cellspacing="0" style="max-width:560px;background:#0d0a14;border:1px solid #2b1f44;border-radius:20px">
        <tr><td style="padding:28px 32px 0;font-family:Arial,sans-serif;letter-spacing:4px;color:#cc66da;font-size:12px;font-weight:bold">NURA HERBEX</td></tr>
        <tr><td style="padding:16px 32px 32px">{inner}</td></tr>
        <tr><td style="padding:16px 32px;border-top:1px solid #2b1f44;color:#7c6f9b;font-size:12px;font-family:Arial,sans-serif">
        Need help? {H(store.Value.SupportEmail)} · {H(store.Value.SupportPhone)}</td></tr>
        </table></td></tr></table></body></html>
        """;

    private async Task<bool> SendAsync(string to, string subject, string html)
    {
        var o = email.Value;
        if (!o.IsConfigured)
        {
            log.LogWarning("[Email] Resend key not configured — skipped '{Subject}' to {To}", subject, to);
            return false;
        }
        if (string.IsNullOrWhiteSpace(to)) return false;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { from = o.From, to = new[] { to }, subject, html }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ResendApiKey);
        try
        {
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                log.LogWarning("[Email] Resend rejected '{Subject}': {Status} {Body}", subject, (int)res.StatusCode, await res.Content.ReadAsStringAsync());
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[Email] Send failed for '{Subject}'", subject);
            return false;
        }
    }
}
