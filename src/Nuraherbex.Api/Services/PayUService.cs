using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public class PayUService(OrderService orders, IOptions<PayUOptions> payu, IOptions<StoreOptions> store, ILogger<PayUService> log)
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
        var order = await orders.GetAsync(orderId) ?? throw new InvalidOperationException($"Order {orderId} not found");
        if (order.PaymentStatus == "PAID") throw new InvalidOperationException($"Order {orderId} is already paid");

        var txnid = $"TXN_{new string(order.Id.Where(char.IsLetterOrDigit).ToArray())}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var amount = order.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        const string productinfo = "Stamix Botanical Vitality Formula";
        var firstname = order.CustomerName.Split(' ')[0];
        var email = string.IsNullOrWhiteSpace(order.CustomerEmail) ? store.Value.SupportEmail : order.CustomerEmail;

        // PayU POSTs the result to the API, which then redirects the browser to the web app's checkout page.
        var surl = $"{callbackBase}/api/payments/payu-response";
        var furl = surl;

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

    /// <summary>Verifies the PayU callback/webhook (reverse hash, status, amount) then settles the order.</summary>
    public async Task<Order> ProcessResponseAsync(IReadOnlyDictionary<string, string> p)
    {
        string G(string k) => p.TryGetValue(k, out var v) ? v : "";
        var txnid = G("txnid");
        var orderId = G("udf1");
        if (string.IsNullOrEmpty(orderId)) orderId = G("order_id");
        if (string.IsNullOrEmpty(orderId) && txnid.Contains('_')) orderId = txnid.Split('_')[1];
        if (string.IsNullOrEmpty(orderId)) throw new InvalidOperationException("Order identifier missing in PayU response");

        var order = await orders.GetAsync(orderId) ?? throw new InvalidOperationException($"Order {orderId} not found");
        var raw = JsonSerializer.Serialize(p);

        if (!_p.IsConfigured && G("simulated") == "true")
        {
            log.LogInformation("[PayU Simulation] Verifying order {Order}", orderId);
            return await orders.MarkPaidAsync(orderId, string.IsNullOrEmpty(G("mihpayid")) ? $"payu_sim_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : G("mihpayid"), txnid, raw);
        }
        if (!_p.IsConfigured) throw new InvalidOperationException("PayU is not configured on the server");

        if (!VerifyReverseHash(p, _p.MerchantSalt))
        {
            log.LogError("[PayU] Reverse hash mismatch for order {Order}", orderId);
            throw new InvalidOperationException("PayU reverse hash verification failed. Potential tampering detected.");
        }
        if (G("status") != "success") throw new InvalidOperationException($"Payment was not successful (Status: {G("status")})");

        if (!decimal.TryParse(G("amount"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var paid)
            || Math.Abs(paid - order.TotalAmount) > 1m)
            throw new InvalidOperationException($"Payment amount mismatch: Expected ₹{order.TotalAmount}, received ₹{G("amount")}");

        return await orders.MarkPaidAsync(orderId, G("mihpayid"), txnid, raw);
    }
}
