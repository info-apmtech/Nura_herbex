using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public class PayUService(NuraDbContext db, OrderService orders, IOptions<PayUOptions> payu, IOptions<StoreOptions> store, ILogger<PayUService> log)
{
    private readonly PayUOptions _p = payu.Value;

    private static string Sha512(string input) => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    /// <summary>sha512(key|txnid|amount|productinfo|firstname|email|udf1..udf5||||||SALT)</summary>
    public static string ForwardHash(string key, string txnid, string amount, string productinfo, string firstname, string email,
        string udf1, string udf2, string udf3, string udf4, string udf5, string salt) =>
        Sha512($"{key}|{txnid}|{amount}|{productinfo}|{firstname}|{email}|{udf1}|{udf2}|{udf3}|{udf4}|{udf5}||||||{salt}");

    /// <summary>sha512([additionalCharges|]SALT|status||||||udf5..udf1|email|firstname|productinfo|amount|txnid|key)</summary>
    public static bool VerifyReverseHash(IReadOnlyDictionary<string, string> p, string salt)
    {
        string G(string k) => p.TryGetValue(k, out var v) ? v : "";
        var seq = $"{salt}|{G("status")}||||||{G("udf5")}|{G("udf4")}|{G("udf3")}|{G("udf2")}|{G("udf1")}|{G("email")}|{G("firstname")}|{G("productinfo")}|{G("amount")}|{G("txnid")}|{G("key")}";
        if (!string.IsNullOrEmpty(G("additionalCharges"))) seq = $"{G("additionalCharges")}|{seq}";
        var calc = Sha512(seq);
        var recv = G("hash").ToLowerInvariant();
        return calc.Length == recv.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(calc), Encoding.ASCII.GetBytes(recv));
    }

    public async Task<PayUInitiateResponse> InitiateAsync(string orderId, string callbackBase)
    {
        // The order is either already saved (admin-created) or held as a snapshot until payment succeeds (public checkout).
        var saved = await orders.GetAsync(orderId);
        if (saved?.PaymentStatus == "PAID") throw new InvalidOperationException($"Order {orderId} is already paid");

        var held = await orders.FindHeldAttemptAsync(orderId);
        var order = saved ?? (held is null ? null : JsonSerializer.Deserialize<Order>(held.OrderSnapshotJson))
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        var txnid = $"TXN_{new string(order.Id.Where(char.IsLetterOrDigit).ToArray())}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var amount = order.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        const string productinfo = "Stamix Botanical Vitality Formula";
        var firstname = order.CustomerName.Split(' ')[0];
        var email = string.IsNullOrWhiteSpace(order.CustomerEmail) ? store.Value.SupportEmail : order.CustomerEmail;

        // Every PayU attempt gets its own row (a retry after a failure keeps the earlier failure on record).
        var attempt = held is { TxnId: null } ? held : new PaymentAttempt
        {
            OrderId = order.Id, CustomerName = order.CustomerName, CustomerEmail = order.CustomerEmail, CustomerPhone = order.CustomerPhone,
            OrderSnapshotJson = held?.OrderSnapshotJson ?? JsonSerializer.Serialize(order),
        };
        attempt.TxnId = txnid; attempt.Amount = order.TotalAmount; attempt.Status = "INITIATED"; attempt.UpdatedAt = DateTime.UtcNow;
        if (db.Entry(attempt).State == EntityState.Detached) db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync();

        // PayU POSTs the result to the API, which then redirects the browser to the web app's checkout page.
        var surl = $"{callbackBase}/api/payments/payu-response";
        var furl = surl;

        if (!_p.IsConfigured && !_p.AllowSimulation)
            throw new InvalidOperationException("Online payment is not available: PayU merchant key/salt are not configured on the server.");

        if (!_p.IsConfigured)
        {
            log.LogWarning("[PayU] Merchant key/salt not set — simulation mode");
            return new PayUInitiateResponse
            {
                Success = true, Simulated = true, ActionUrl = _p.ActionUrl, OrderId = order.Id, Amount = amount,
                Fields = new() { ["key"] = "TEST_KEY_SIMULATED", ["txnid"] = txnid, ["amount"] = amount, ["productinfo"] = productinfo, ["firstname"] = firstname,
                    ["email"] = email, ["phone"] = order.CustomerPhone, ["surl"] = surl, ["furl"] = furl, ["hash"] = "simulated_sha512_hash", ["udf1"] = order.Id },
            };
        }

        var hash = ForwardHash(_p.MerchantKey, txnid, amount, productinfo, firstname, email, order.Id, "", "", "", "", _p.MerchantSalt);
        return new PayUInitiateResponse
        {
            Success = true, Simulated = false, ActionUrl = _p.ActionUrl, OrderId = order.Id, Amount = amount,
            Fields = new() { ["key"] = _p.MerchantKey, ["txnid"] = txnid, ["amount"] = amount, ["productinfo"] = productinfo, ["firstname"] = firstname,
                ["email"] = email, ["phone"] = order.CustomerPhone, ["surl"] = surl, ["furl"] = furl, ["hash"] = hash, ["udf1"] = order.Id },
        };
    }

    /// <summary>
    /// Verifies the PayU callback/webhook (reverse hash, status, amount). Success creates the order and settles it;
    /// anything else is stored as a failure record and no order is created.
    /// </summary>
    public async Task<Order> ProcessResponseAsync(IReadOnlyDictionary<string, string> p)
    {
        string G(string k) => p.TryGetValue(k, out var v) ? v : "";
        var txnid = G("txnid");
        var orderId = G("udf1");
        if (string.IsNullOrEmpty(orderId)) orderId = G("order_id");
        if (string.IsNullOrEmpty(orderId) && txnid.Contains('_')) orderId = txnid.Split('_')[1];
        if (string.IsNullOrEmpty(orderId)) throw new InvalidOperationException("Order identifier missing in PayU response");

        var raw = JsonSerializer.Serialize(p);
        var attempt = (string.IsNullOrEmpty(txnid) ? null : await db.PaymentAttempts.FirstOrDefaultAsync(a => a.TxnId == txnid))
            ?? await orders.FindHeldAttemptAsync(orderId);
        var saved = await orders.GetAsync(orderId);
        if (attempt is null && saved is null) throw new InvalidOperationException($"Order {orderId} not found");

        // PayU also calls this as a webhook, so a repeat of an already-settled payment is a no-op.
        if (attempt is { Status: "SUCCESS" } && saved is not null) return saved;

        if (!_p.IsConfigured && _p.AllowSimulation && G("simulated") == "true")
        {
            log.LogInformation("[PayU Simulation] Verifying order {Order}", orderId);
            return await SettleAsync(attempt, saved, orderId, string.IsNullOrEmpty(G("mihpayid")) ? $"payu_sim_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : G("mihpayid"), txnid, raw, p);
        }
        if (!_p.IsConfigured) throw new InvalidOperationException("PayU is not configured on the server");

        if (!VerifyReverseHash(p, _p.MerchantSalt))
        {
            log.LogError("[PayU] Reverse hash mismatch for order {Order}", orderId);
            await RecordFailureAsync(attempt, orderId, "FAILED", "Response hash verification failed (possible tampering)", raw, p);
            throw new InvalidOperationException("PayU reverse hash verification failed. Potential tampering detected.");
        }

        var status = G("status");
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            var reason = FirstNonEmpty(G("error_Message"), G("error_message"), G("field9"), G("error"), G("unmappedstatus"), status, "Payment was not completed");
            var cancelled = reason.Contains("cancel", StringComparison.OrdinalIgnoreCase) || G("unmappedstatus").Contains("userCancelled", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
                await RecordFailureAsync(attempt, orderId, "INITIATED", "Payment pending at bank/UPI", raw, p);
            else
                await RecordFailureAsync(attempt, orderId, cancelled ? "CANCELLED" : "FAILED", reason, raw, p);
            throw new InvalidOperationException(cancelled ? "Payment was cancelled." : $"Payment was not successful ({reason})");
        }

        var expected = saved?.TotalAmount ?? attempt!.Amount;
        if (!decimal.TryParse(G("amount"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var paid)
            || Math.Abs(paid - expected) > 1m)
        {
            var msg = $"Payment amount mismatch: Expected ₹{expected}, received ₹{G("amount")}";
            await RecordFailureAsync(attempt, orderId, "FAILED", msg, raw, p);
            throw new InvalidOperationException(msg);
        }

        return await SettleAsync(attempt, saved, orderId, G("mihpayid"), txnid, raw, p);
    }

    private async Task<Order> SettleAsync(PaymentAttempt? attempt, Order? saved, string orderId, string? paymentRef, string txnid, string raw, IReadOnlyDictionary<string, string> p)
    {
        // Only now does a held online order become a real order row.
        var order = attempt is not null && saved is null
            ? await orders.ConfirmHeldOrderAsync(attempt, paymentRef, txnid, raw)
            : await orders.MarkPaidAsync(orderId, paymentRef, txnid, raw);

        if (attempt is not null)
        {
            attempt.Status = "SUCCESS"; attempt.FailureReason = null;
            attempt.GatewayStatus = p.GetValueOrDefault("status"); attempt.PayuPaymentId = paymentRef;
            attempt.PaymentMode = p.GetValueOrDefault("mode"); attempt.GatewayResponse = raw; attempt.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return order;
    }

    private async Task RecordFailureAsync(PaymentAttempt? attempt, string orderId, string status, string reason, string raw, IReadOnlyDictionary<string, string> p)
    {
        try
        {
            if (attempt is null)
            {
                var o = await orders.GetAsync(orderId);
                attempt = new PaymentAttempt
                {
                    OrderId = orderId, TxnId = p.GetValueOrDefault("txnid"), Amount = o?.TotalAmount ?? 0,
                    CustomerName = o?.CustomerName ?? "", CustomerEmail = o?.CustomerEmail ?? "", CustomerPhone = o?.CustomerPhone ?? "",
                };
                db.PaymentAttempts.Add(attempt);
            }
            if (attempt.Status != "SUCCESS")
            {
                attempt.Status = status; attempt.FailureReason = reason.Length > 500 ? reason[..500] : reason;
                attempt.GatewayStatus = p.GetValueOrDefault("status"); attempt.PayuPaymentId = p.GetValueOrDefault("mihpayid");
                attempt.PaymentMode = p.GetValueOrDefault("mode"); attempt.GatewayResponse = raw; attempt.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogError(ex, "[PayU] Could not store payment failure for {Order}", orderId); }
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
