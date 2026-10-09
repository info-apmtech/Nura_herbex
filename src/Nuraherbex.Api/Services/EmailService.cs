using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>Transactional email through Resend. Returns false when delivery cannot be submitted.</summary>
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
        return await SendAsync(o.CustomerEmail, $"Order Confirmed: {o.Id} | STAMIX™ Botanical Vitality Formula", Wrap(body), "order confirmation");
    }

    public async Task<bool> SendOrderDeliveredAsync(Order o)
    {
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:22px;margin:0 0 8px">Your Stamix™ has arrived.</h1>
            <p style="color:#c9bfe0;margin:0 0 16px">Order <b style="color:#fff3bf">{H(o.Id)}</b> was delivered. Consistency compounds — two 10 g scoops a day, every day.</p>
            <p style="color:#c9bfe0;margin:0 0 20px">How was your experience? Your honest review helps other men decide.</p>
            <p style="margin:0"><a href="{store.Value.FrontendUrl}/#reviews" style="background:#9929ea;color:#fff;text-decoration:none;padding:12px 22px;border-radius:999px;font-weight:bold">Leave a review</a></p>
            """;
        return await SendAsync(o.CustomerEmail, "Your STAMIX™ has been delivered! How was your experience? ⭐⭐⭐⭐⭐", Wrap(body), "delivery notification");
    }

    public async Task<bool> SendOtpAsync(string to, string otp, string name)
    {
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:20px;margin:0 0 8px">Hello {H(name)},</h1>
            <p style="color:#c9bfe0;margin:0 0 18px">Use this code to sign in to your Vitality Account. It expires in 10 minutes.</p>
            <div style="font-size:34px;letter-spacing:10px;font-weight:bold;color:#fff3bf;background:#140f22;border:1px solid #2b1f44;border-radius:14px;padding:16px;text-align:center">{H(otp)}</div>
            <p style="color:#9d90bd;margin:18px 0 0;font-size:13px">If you didn't request this, you can safely ignore this email.</p>
            """;
        return await SendAsync(to, $"{otp} is your Nura Herbex Vitality Login Code", Wrap(body), "login code");
    }

    // ---- Order status notifications --------------------------------------------------------

    /// <summary>
    /// Maps a courier status transition to the notification to send, or null when nothing new happened.
    /// Guards repeated scans of the same status and late out-of-order events so a customer is never emailed twice.
    /// </summary>
    public static string? TransitionEmail(string? status, string? previous)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, previous, StringComparison.Ordinal)) return null;
        var firstMove = previous is null or "" or "PENDING" or "SHIPMENT_CREATED" or "AWB_ASSIGNED";
        return status switch
        {
            "PICKED_UP" or "IN_TRANSIT" when firstMove => "SHIPPED",
            "OUT_FOR_DELIVERY" => "OUT_FOR_DELIVERY",
            "DELIVERED" => "DELIVERED",
            "CANCELLED" => "CANCELLED",
            "RTO" => "RTO",
            _ => null,
        };
    }

    /// <summary>Sends the customer email for a notification key produced by <see cref="TransitionEmail"/>.</summary>
    public Task<bool> SendStatusUpdateAsync(Order o, string notification) => notification switch
    {
        "SHIPPED" => SendOrderShippedAsync(o),
        "OUT_FOR_DELIVERY" => SendOutForDeliveryAsync(o),
        "DELIVERED" => SendOrderDeliveredAsync(o),
        "CANCELLED" => SendOrderCancelledAsync(o),
        "RTO" => SendOrderReturnedAsync(o),
        _ => Task.FromResult(true),
    };

    /// <summary>First movement: handed to the courier, with AWB and a tracking link.</summary>
    public async Task<bool> SendOrderShippedAsync(Order o)
    {
        var courier = string.IsNullOrWhiteSpace(o.ShiprocketCourier) ? o.Courier : o.ShiprocketCourier;
        var tracking = string.IsNullOrWhiteSpace(courier) ? "our courier partner" : courier;
        var awbRow = string.IsNullOrWhiteSpace(o.ShiprocketAwb) ? "" :
            $"<p style=\"color:#9d90bd;margin:14px 0 0;font-size:13px\">Tracking number <b style=\"color:#fff3bf\">{H(o.ShiprocketAwb)}</b></p>";
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:22px;margin:0 0 8px">Your order is on the way.</h1>
            <p style="color:#c9bfe0;margin:0 0 6px">Order <b style="color:#fff3bf">{H(o.Id)}</b> has been handed to {H(tracking)} and is moving through the network.</p>
            {awbRow}
            <p style="color:#9d90bd;margin:16px 0 0;font-size:13px">Delivering to {H(o.ShippingAddress.City)}, {H(o.ShippingAddress.State)} – {H(o.ShippingAddress.Pincode)}</p>
            <p style="margin:24px 0 0"><a href="{store.Value.FrontendUrl}/track" style="background:#9929ea;color:#fff;text-decoration:none;padding:12px 22px;border-radius:999px;font-weight:bold">Track your order</a></p>
            """;
        return await SendAsync(o.CustomerEmail, $"Order {o.Id} has shipped | STAMIX™ Botanical Vitality Formula", Wrap(body), "shipping notification");
    }

    /// <summary>The parcel is on the delivery vehicle.</summary>
    public async Task<bool> SendOutForDeliveryAsync(Order o)
    {
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:22px;margin:0 0 8px">Out for delivery.</h1>
            <p style="color:#c9bfe0;margin:0 0 6px">Great news — order <b style="color:#fff3bf">{H(o.Id)}</b> is on the delivery vehicle for {H(o.ShippingAddress.City)} and should reach you shortly.</p>
            <p style="color:#9d90bd;margin:16px 0 0;font-size:13px">Please keep your phone handy; the courier may call before arriving. Delivering to {H(o.ShippingAddress.AddressLine1)}, {H(o.ShippingAddress.City)} – {H(o.ShippingAddress.Pincode)}.</p>
            <p style="margin:24px 0 0"><a href="{store.Value.FrontendUrl}/track" style="background:#9929ea;color:#fff;text-decoration:none;padding:12px 22px;border-radius:999px;font-weight:bold">Track your order</a></p>
            """;
        return await SendAsync(o.CustomerEmail, $"Out for delivery: order {o.Id} | STAMIX™", Wrap(body), "out-for-delivery notification");
    }

    /// <summary>Order or shipment cancelled (admin, courier or customer).</summary>
    public async Task<bool> SendOrderCancelledAsync(Order o, string? reason = null)
    {
        var charged = o.PaymentStatus is "PAID" or "COD_COLLECTED";
        var paymentLine = charged
            ? "You have already paid for this order — our support team will process the refund against this order number."
            : "No payment has been taken for this order.";
        var reasonRow = string.IsNullOrWhiteSpace(reason) ? "" :
            $"<p style=\"color:#9d90bd;margin:0 0 14px;font-size:13px\">Reason: {H(reason)}</p>";
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:22px;margin:0 0 8px">Your order was cancelled.</h1>
            <p style="color:#c9bfe0;margin:0 0 6px">Order <b style="color:#fff3bf">{H(o.Id)}</b> — ₹{o.TotalAmount:N0} — has been cancelled.</p>
            {reasonRow}
            <p style="color:#c9bfe0;margin:14px 0 0">{paymentLine}</p>
            <p style="color:#9d90bd;margin:16px 0 0;font-size:13px">If this was not intended, reply to this email or contact support and we will gladly help.</p>
            """;
        return await SendAsync(o.CustomerEmail, $"Order {o.Id} has been cancelled | STAMIX™", Wrap(body), "cancellation notice");
    }

    /// <summary>Return to origin — the courier could not deliver.</summary>
    public async Task<bool> SendOrderReturnedAsync(Order o)
    {
        var body = $"""
            <h1 style="font-family:Georgia,serif;color:#fff3bf;font-size:22px;margin:0 0 8px">Your order is returning to us.</h1>
            <p style="color:#c9bfe0;margin:0 0 6px">Our courier could not complete delivery of order <b style="color:#fff3bf">{H(o.Id)}</b>, and the parcel is on its way back to our facility.</p>
            <p style="color:#c9bfe0;margin:14px 0 0">Contact support and we will arrange a re-shipment or a refund for you — order value ₹{o.TotalAmount:N0}.</p>
            <p style="margin:24px 0 0"><a href="{store.Value.FrontendUrl}/track" style="background:#9929ea;color:#fff;text-decoration:none;padding:12px 22px;border-radius:999px;font-weight:bold">View order status</a></p>
            """;
        return await SendAsync(o.CustomerEmail, $"Order {o.Id} is returning to us | STAMIX™", Wrap(body), "return notice");
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

    private async Task<bool> SendAsync(string to, string subject, string html, string emailType)
    {
        if (string.IsNullOrWhiteSpace(to)) return false;
        var o = email.Value;

        if (o.Smtp.IsConfigured) return await SendSmtpAsync(o, to, subject, html, emailType);
        if (!string.IsNullOrWhiteSpace(o.ResendApiKey)) return await SendResendAsync(o, to, subject, html, emailType);

        log.LogError("[Email] {EmailType} not sent: configure Email:Smtp:Host (SMTP) or Email:ResendApiKey (Resend).", emailType);
        return false;
    }

    /// <summary>SMTP submission. Returns false when the server rejects or cannot be reached.</summary>
    private async Task<bool> SendSmtpAsync(EmailOptions o, string to, string subject, string html, string emailType)
    {
        var s = o.Smtp;
        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(o.From), // accepts "Nura Herbex <care@nuraherbex.com>"
                Subject = subject,
                Body = html,
                IsBodyHtml = true,
            };
            msg.To.Add(to);

            using var client = new SmtpClient(s.Host, s.Port)
            {
                EnableSsl = s.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
            if (!string.IsNullOrWhiteSpace(s.Username))
            {
                client.UseDefaultCredentials = false;
                client.Credentials = new NetworkCredential(s.Username, s.Password);
            }

            await client.SendMailAsync(msg);
            log.LogInformation("[Email] {EmailType} sent to {To} via {Host}:{Port}.", emailType, to, s.Host, s.Port);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Email] SMTP {EmailType} delivery to {Host}:{Port} failed.", emailType, s.Host, s.Port);
            return false;
        }
    }

    /// <summary>Resend HTTP API — fallback when no SMTP host is configured.</summary>
    private async Task<bool> SendResendAsync(EmailOptions o, string to, string subject, string html, string emailType)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { from = o.From, to = new[] { to }, subject, html }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ResendApiKey);
        try
        {
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                log.LogWarning("[Email] Resend rejected {EmailType} with status {Status}.", emailType, (int)res.StatusCode);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[Email] Sending {EmailType} failed.", emailType);
            return false;
        }
    }
}
