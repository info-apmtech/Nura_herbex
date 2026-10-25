using System.Text.Json;

namespace Nuraherbex.Api.Services;

/// <summary>Turns the error WhatsApp (Meta) returns into a sentence staff can act on, instead of a bare "HTTP 400".</summary>
public static class WhatsAppErrors
{
    /// <summary>Reads Meta's error from either a send response ({"error":{...}}) or a webhook status ({"code":...}).</summary>
    public static (int? Code, string? Message, string? Details) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err)) root = err;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null);
            int? code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : null;
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : root.TryGetProperty("title", out var t) ? t.GetString() : null;
            string? details = null;
            if (root.TryGetProperty("error_data", out var ed) && ed.ValueKind == JsonValueKind.Object && ed.TryGetProperty("details", out var d)) details = d.GetString();
            return (code, msg, details);
        }
        catch (JsonException) { return (null, null, null); }
    }

    public static string Friendly(string? json, int httpStatus = 0)
    {
        var (code, message, details) = Parse(json);
        var reason = !string.IsNullOrWhiteSpace(details) ? details : message;
        switch (code)
        {
            case 131047:
                return "The 24-hour reply window is closed. WhatsApp only lets you send free-text messages within 24 hours of the customer's last message to you. Send an approved template message instead, or wait until they message you again.";
            case 131030:
                return "This number is not on the allowed recipients list of your WhatsApp test number. Add it in Meta (WhatsApp → API Setup → To), or switch to your live business number.";
            case 190 or 102 or 463 or 467:
                return "The WhatsApp access token has expired or is not valid. Create a new permanent token in Meta Business settings and update WhatsApp:AccessToken in the API configuration.";
            case 131026:
                return "WhatsApp could not deliver to this number. It may not be on WhatsApp, or the customer has not accepted WhatsApp's latest terms.";
            case 131021:
                return "You cannot send a message to your own business number.";
            case 131042:
                return "WhatsApp Business payment method problem. Check billing in Meta Business Manager, then try again.";
            case 131048 or 131056 or 130429 or 80007:
                return "WhatsApp is limiting how fast messages can be sent to this number. Wait a few minutes and try again.";
            case 131009:
                return "WhatsApp rejected a value in the message. " + (reason ?? "Check the phone number and text.");
            case 133010 or 133004:
                return "The WhatsApp business phone number is not registered or is not available right now. Check it in Meta WhatsApp Manager.";
            case 368:
                return "WhatsApp has temporarily restricted this business account for policy reasons. Check the account quality page in Meta Business Manager.";
            case >= 132000 and <= 132016:
                return "The template message was rejected: " + (reason ?? "the template name, language or parameters do not match an approved template.");
        }
        if (code is not null || !string.IsNullOrWhiteSpace(reason))
            return $"WhatsApp rejected the message{(code is null ? "" : $" (code {code})")}: {reason ?? "no further detail was given"}.";
        return httpStatus > 0
            ? $"WhatsApp rejected the message (HTTP {httpStatus}). The customer may need to message you first, or use an approved template."
            : "WhatsApp rejected the message.";
    }
}
