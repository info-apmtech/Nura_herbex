using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// GST tax invoices. Listed prices already include GST (see ProductService.CalculateAsync), so the invoice
/// backs the tax out of each amount: taxable value = gross / (1 + rate), GST = gross - taxable value.
/// Shipping is billed at the same rate. The invoice total therefore always equals the order's TotalAmount.
/// Invoices are rendered as printable HTML (browser "Save as PDF") and opened through short-lived signed links.
/// </summary>
public class InvoiceService(IOptions<JwtOptions> jwt, IOptionsMonitor<StoreOptions> store)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private static readonly CultureInfo Inr = new("en-IN");

    private byte[] Key => Encoding.UTF8.GetBytes("invoice:" + jwt.Value.Secret);
    private string Mac(string orderId, long exp) =>
        Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"{orderId}.{exp}"))).ToLowerInvariant();

    /// <summary>Path relative to the API root (HTML printable view).</summary>
    public string Url(string orderId)
    {
        var exp = new DateTimeOffset(DateTime.UtcNow.Add(Lifetime)).ToUnixTimeSeconds();
        return $"api/invoices/{Uri.EscapeDataString(orderId)}?exp={exp}&sig={Mac(orderId, exp)}";
    }

    /// <summary>Path relative to the API root (direct PDF download).</summary>
    public string DownloadUrl(string orderId)
    {
        var exp = new DateTimeOffset(DateTime.UtcNow.Add(Lifetime)).ToUnixTimeSeconds();
        return $"api/invoices/{Uri.EscapeDataString(orderId)}/download?exp={exp}&sig={Mac(orderId, exp)}";
    }

    public bool IsValid(string orderId, long exp, string? sig)
    {
        if (string.IsNullOrEmpty(sig) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        var a = Encoding.ASCII.GetBytes(Mac(orderId, exp));
        var b = Encoding.ASCII.GetBytes(sig.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>14-digit invoice number: order date (yyMMdd) + 8 digits derived from the order id, so it is stable for every request.</summary>
    public static string InvoiceNumber(Order o)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("invoice-no:" + o.Id));
        var n = BitConverter.ToUInt64(h, 0) % 100_000_000UL;
        return o.CreatedAt.ToString("yyMMdd", CultureInfo.InvariantCulture) + n.ToString("D8", CultureInfo.InvariantCulture);
    }

    private sealed record Line(string Name, string? Hsn, decimal Qty, decimal Gross, decimal Taxable, decimal Gst);

    public string RenderHtml(Order o)
    {
        var s = store.CurrentValue;
        var rate = s.GstRatePercent <= 0 ? 18m : s.GstRatePercent;
        var divisor = 1m + rate / 100m;

        // Spread the discount over the product lines proportionally; the last line absorbs rounding.
        var lines = new List<Line>();
        var discountLeft = Math.Min(o.DiscountAmount, o.Subtotal);
        for (var i = 0; i < o.Items.Count; i++)
        {
            var it = o.Items[i];
            var share = i == o.Items.Count - 1
                ? discountLeft
                : Math.Round(o.Subtotal == 0 ? 0 : o.DiscountAmount * it.Total / o.Subtotal, 2);
            share = Math.Min(share, it.Total);
            discountLeft -= share;
            lines.Add(Split(it.Name, it.Hsn, it.Quantity, it.Total - share, divisor));
        }
        if (o.ShippingFee > 0) lines.Add(Split("Shipping & handling", "9965", 1, o.ShippingFee, divisor));

        var taxable = lines.Sum(l => l.Taxable);
        var gst = lines.Sum(l => l.Gst);
        var total = taxable + gst;

        var sellerState = (s.SellerState ?? "").Trim();
        var buyerState = (o.ShippingAddress.State ?? "").Trim();
        var intra = sellerState.Length > 0 && string.Equals(sellerState, buyerState, StringComparison.OrdinalIgnoreCase);
        var cgst = Math.Round(gst / 2, 2);
        var gstRows = intra
            ? Row($"CGST @ {Pct(rate / 2)}%", cgst) + Row($"SGST @ {Pct(rate / 2)}%", gst - cgst)
            : Row(sellerState.Length > 0 ? $"IGST @ {Pct(rate)}%" : $"GST @ {Pct(rate)}%", gst);

        var a = o.ShippingAddress;
        var paid = o.PaymentStatus is OrderPaymentStatus.Paid or OrderPaymentStatus.CodCollected;
        var chipText = o.PaymentStatus == OrderPaymentStatus.CodCollected ? "COD COLLECTED"
            : o.PaymentStatus == OrderPaymentStatus.CodPending ? "COD PENDING"
            : paid ? "PAID" : "PAYMENT DUE";
        var dlUrl = "/" + DownloadUrl(o.Id);
        var shipModeLabel = o.ShippingMode == ShippingMode.Offline ? "Offline / Local Delivery" : $"Online Courier{(!string.IsNullOrWhiteSpace(o.Courier) ? " (" + o.Courier + ")" : "")}";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Invoice {H(InvoiceNumber(o))}</title><style>{Css}</style></head><body>");
        sb.Append($"<div class=\"bar\"><a class=\"btn\" href=\"{H(dlUrl)}\">Download PDF</a> <button onclick=\"window.print()\">Print / Save as PDF</button></div><div class=\"page\">");

        // Header
        sb.Append("<div class=\"head\"><div>");
        sb.Append($"<div class=\"brand\">{H(s.SellerLegalName)}</div>");
        sb.Append($"<div class=\"small\">{H(s.SellerAddress)}");
        if (!string.IsNullOrWhiteSpace(s.SellerGstin)) sb.Append($"<br><b>GSTIN:</b> {H(s.SellerGstin)}");
        sb.Append($"<br>{H(s.SupportEmail)} &nbsp;|&nbsp; {H(s.SupportPhone)}</div></div>");
        sb.Append($"<div class=\"title\"><h1>TAX INVOICE</h1><span class=\"chip {(paid ? "ok" : "due")}\">{H(chipText)}</span></div></div>");

        // Meta strip
        sb.Append("<div class=\"meta\">");
        sb.Append($"<div><span>Invoice No.</span><b>{H(InvoiceNumber(o))}</b></div>");
        sb.Append($"<div><span>Invoice Date</span><b>{o.CreatedAt.ToLocalTime():dd MMM yyyy}</b></div>");
        sb.Append($"<div><span>Order No.</span><b>{H(o.Id)}</b></div>");
        sb.Append($"<div><span>Payment Mode</span><b>{(o.PaymentMethod == OrderPaymentMethod.Cod ? "Cash on Delivery" : "Online Payment")}</b></div>");
        sb.Append($"<div><span>Dispatch Mode</span><b>{H(shipModeLabel)}</b></div></div>");
        if (o.ShippingMode == ShippingMode.Offline && !string.IsNullOrWhiteSpace(o.OfflineDispatchNote))
            sb.Append($"<div class=\"small\" style=\"margin:-12px 0 14px;padding:8px 12px;background:#faf9fd;border-radius:6px\"><b>Offline Dispatch Note:</b> {H(o.OfflineDispatchNote)}</div>");

        // Parties
        sb.Append("<div class=\"parties\"><div class=\"card\"><h3>Billed To</h3>");
        sb.Append($"<b>{H(o.CustomerName)}</b><div class=\"small\">{H(o.CustomerPhone)}<br>{H(o.CustomerEmail)}</div></div>");
        sb.Append($"<div class=\"card\"><h3>Ship To / Place of Supply</h3><b>{H(o.CustomerName)}</b><div class=\"small\">{H(a.AddressLine1)}");
        if (!string.IsNullOrWhiteSpace(a.AddressLine2)) sb.Append(", " + H(a.AddressLine2));
        sb.Append($"<br>{H(a.City)}, {H(a.State)} - {H(a.Pincode)}<br>{H(a.Country)}</div></div></div>");

        // Items
        sb.Append($"<table class=\"items\"><thead><tr><th>#</th><th>Description</th><th>HSN/SAC</th><th class=\"r\">Qty</th><th class=\"r\">Taxable Value</th><th class=\"r\">GST {Pct(rate)}%</th><th class=\"r\">Amount</th></tr></thead><tbody>");
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            sb.Append($"<tr><td>{i + 1}</td><td>{H(l.Name)}</td><td>{H(l.Hsn)}</td><td class=\"r\">{l.Qty:0.##}</td><td class=\"r\">{M(l.Taxable)}</td><td class=\"r\">{M(l.Gst)}</td><td class=\"r\">{M(l.Gross)}</td></tr>");
        }
        sb.Append("</tbody></table>");

        // Totals + words
        sb.Append("<div class=\"foot\"><div class=\"words\"><span>Amount in words</span><b>");
        sb.Append(Words((long)Math.Round(total, 0, MidpointRounding.AwayFromZero)));
        sb.Append(" Only</b><p class=\"small\">All prices are inclusive of ");
        sb.Append(Pct(rate));
        sb.Append("% GST.</p></div><table class=\"tot\"><tbody>");
        if (o.DiscountAmount > 0)
            sb.Append(Row($"Discount{(string.IsNullOrEmpty(o.CouponCode) ? "" : " (" + H(o.CouponCode) + ")")} - already deducted", o.DiscountAmount));
        sb.Append(Row("Total taxable value", taxable));
        sb.Append(gstRows);
        sb.Append($"<tr class=\"grand\"><td>Invoice Total</td><td class=\"r\">{M(total)}</td></tr></tbody></table></div>");

        // Footer
        sb.Append("<div class=\"sign\"><div class=\"small\"><b>Declaration</b><br>We declare that this invoice shows the actual price of the goods described and that all particulars are true and correct.</div></div>");
        sb.Append("<div class=\"thanks\">Thank you for your purchase. This is a computer-generated invoice and does not require a signature.</div></div></body></html>");
        return sb.ToString();
    }

    private const string Css =
        "*{box-sizing:border-box}body{font-family:'Segoe UI',Helvetica,Arial,sans-serif;color:#1f2433;margin:0;background:#eceef3;-webkit-print-color-adjust:exact;print-color-adjust:exact}" +
        ".page{max-width:860px;margin:20px auto;background:#fff;padding:44px 48px;border-top:6px solid #4b2e83;box-shadow:0 4px 20px rgba(0,0,0,.08)}" +
        ".head{display:flex;justify-content:space-between;align-items:flex-start;gap:24px;flex-wrap:wrap;padding-bottom:22px;border-bottom:1px solid #e3e5ec}" +
        ".brand{font-size:24px;font-weight:700;color:#4b2e83;letter-spacing:.3px}.small{font-size:12.5px;color:#5b6070;line-height:1.6;margin-top:4px}" +
        ".title{text-align:right}h1{margin:0 0 8px;font-size:28px;letter-spacing:2px;color:#1f2433}" +
        ".chip{display:inline-block;padding:4px 12px;border-radius:999px;font-size:11px;font-weight:700;letter-spacing:1px}.ok{background:#e3f6ea;color:#17803d}.due{background:#fff1dc;color:#b45f06}" +
        ".meta{display:flex;flex-wrap:wrap;margin:22px 0;background:#f5f2fb;border-radius:8px}.meta div{flex:1;min-width:160px;padding:12px 16px}" +
        ".meta span{display:block;font-size:11px;text-transform:uppercase;letter-spacing:.8px;color:#6b6f80}.meta b{font-size:14px;font-variant-numeric:tabular-nums}" +
        ".parties{display:flex;gap:18px;flex-wrap:wrap}.card{flex:1;min-width:260px;border:1px solid #e3e5ec;border-radius:8px;padding:14px 16px}" +
        ".card h3{margin:0 0 8px;font-size:11px;text-transform:uppercase;letter-spacing:.9px;color:#4b2e83}" +
        "table{width:100%;border-collapse:collapse}.items{margin-top:24px;font-size:13px}" +
        ".items th{background:#4b2e83;color:#fff;text-align:left;padding:10px 8px;font-weight:600;font-size:12px}.items td{padding:10px 8px;border-bottom:1px solid #eceef3;vertical-align:top}" +
        ".items tbody tr:nth-child(even){background:#faf9fd}.r{text-align:right;font-variant-numeric:tabular-nums}" +
        ".foot{display:flex;justify-content:space-between;gap:28px;flex-wrap:wrap;margin-top:22px}.words{flex:1;min-width:240px}" +
        ".words span{font-size:11px;text-transform:uppercase;letter-spacing:.8px;color:#6b6f80;display:block}.words b{font-size:14px}" +
        ".tot{width:340px;font-size:13px}.tot td{padding:5px 8px}.muted{color:#5b6070}" +
        ".grand td{font-weight:700;font-size:16px;background:#f5f2fb;color:#4b2e83;padding:10px 8px;border-top:2px solid #4b2e83}" +
        ".sign{display:flex;justify-content:space-between;gap:28px;flex-wrap:wrap;margin-top:40px;align-items:flex-end}.sign>.small{flex:1;min-width:240px}" +
        ".thanks{margin-top:28px;padding-top:14px;border-top:1px solid #e3e5ec;text-align:center;font-size:12px;color:#6b6f80}" +
        ".bar{max-width:860px;margin:16px auto 0;text-align:right;display:flex;justify-content:flex-end;gap:10px}.bar .btn,button{background:#4b2e83;color:#fff;border:0;padding:9px 18px;border-radius:6px;font-size:14px;cursor:pointer;text-decoration:none;display:inline-block}" +
        "@page{size:A4;margin:12mm}@media print{body{background:#fff}.page{box-shadow:none;margin:0;padding:0}.bar{display:none}}";

    /// <summary>Zero-dependency A4 portrait PDF invoice (suitable for direct download on offline/local & online orders).</summary>
    public byte[] RenderPdf(Order o)
    {
        var s = store.CurrentValue;
        var rate = s.GstRatePercent <= 0 ? 18m : s.GstRatePercent;
        var divisor = 1m + rate / 100m;
        var lines = new List<Line>();
        var discountLeft = Math.Min(o.DiscountAmount, o.Subtotal);
        for (var i = 0; i < o.Items.Count; i++)
        {
            var it = o.Items[i];
            var share = i == o.Items.Count - 1 ? discountLeft : Math.Round(o.Subtotal == 0 ? 0 : o.DiscountAmount * it.Total / o.Subtotal, 2);
            share = Math.Min(share, it.Total);
            discountLeft -= share;
            lines.Add(Split(it.Name, it.Hsn, it.Quantity, it.Total - share, divisor));
        }
        if (o.ShippingFee > 0) lines.Add(Split("Shipping & handling", "9965", 1, o.ShippingFee, divisor));
        var taxable = lines.Sum(l => l.Taxable);
        var gst = lines.Sum(l => l.Gst);
        var total = taxable + gst;

        var paid = o.PaymentStatus is OrderPaymentStatus.Paid or OrderPaymentStatus.CodCollected;
        var statusTag = o.PaymentStatus == OrderPaymentStatus.CodCollected ? "COD COLLECTED"
            : o.PaymentStatus == OrderPaymentStatus.CodPending ? "COD PENDING"
            : paid ? "PAID" : "PAYMENT DUE";

        var sb = new StringBuilder();
        var y = 800;
        void Text(int x, int py, string font, int size, string txt, string rgb = "0.12 0.14 0.20")
        {
            var safe = PdfClean(txt);
            sb.Append($"BT /{font} {size} Tf {x} {py} Td {rgb} rg ({PdfEsc(safe)}) Tj ET\n");
        }
        void Rule(int yPos) => sb.Append($"0.85 0.86 0.90 RG 0.8 w 40 {yPos} m 555 {yPos} l S\n");

        sb.Append("0.294 0.180 0.514 rg 40 818 515 6 re f\n");
        Text(40, y, "F2", 18, string.IsNullOrWhiteSpace(s.SellerLegalName) ? "Nura Herbex" : s.SellerLegalName, "0.294 0.180 0.514");
        Text(400, y, "F2", 18, "TAX INVOICE", "0.12 0.14 0.20");
        y -= 16;
        if (!string.IsNullOrWhiteSpace(s.SellerAddress)) { Text(40, y, "F1", 9, s.SellerAddress, "0.35 0.38 0.45"); y -= 12; }
        if (!string.IsNullOrWhiteSpace(s.SellerGstin)) { Text(40, y, "F1", 9, "GSTIN: " + s.SellerGstin, "0.35 0.38 0.45"); y -= 12; }
        Text(40, y, "F1", 9, $"{s.SupportEmail} | {s.SupportPhone}", "0.35 0.38 0.45");
        Text(400, 782, "F2", 10, $"Status: {statusTag}", paid ? "0.09 0.50 0.24" : "0.70 0.37 0.02");

        y = Math.Min(y - 16, 742);
        Rule(y + 6);
        Text(40, y - 8, "F1", 9, $"Invoice No: {InvoiceNumber(o)}     Order No: {o.Id}     Date: {o.CreatedAt.ToLocalTime():dd MMM yyyy}");
        y -= 22;
        var modeText = $"Payment: {(o.PaymentMethod == OrderPaymentMethod.Cod ? "Cash on Delivery" : "Online")} ({o.PaymentStatus.ToCode()})   |   Dispatch: {(o.ShippingMode == ShippingMode.Offline ? "Offline / Local Delivery" : (o.Courier ?? "Online Courier"))}";
        Text(40, y - 6, "F1", 9, modeText);
        if (!string.IsNullOrWhiteSpace(o.OfflineDispatchNote))
        {
            y -= 16;
            Text(40, y - 6, "F1", 9, $"Dispatch Note: {o.OfflineDispatchNote}");
        }
        y -= 20;
        Rule(y);

        y -= 16;
        Text(40, y, "F2", 9, "BILLED & SHIPPED TO", "0.294 0.180 0.514");
        y -= 14;
        var a = o.ShippingAddress;
        Text(40, y, "F2", 10, $"{o.CustomerName} ({o.CustomerPhone})");
        y -= 13;
        Text(40, y, "F1", 9, $"{a.AddressLine1}{(string.IsNullOrWhiteSpace(a.AddressLine2) ? "" : ", " + a.AddressLine2)}");
        y -= 12;
        Text(40, y, "F1", 9, $"{a.City}, {a.State} - {a.Pincode}, {a.Country}");

        y -= 24;
        sb.Append($"0.294 0.180 0.514 rg 40 {y - 6} 515 18 re f\n");
        Text(46, y, "F2", 9, "#", "1 1 1");
        Text(66, y, "F2", 9, "Description", "1 1 1");
        Text(275, y, "F2", 9, "HSN", "1 1 1");
        Text(330, y, "F2", 9, "Qty", "1 1 1");
        Text(370, y, "F2", 9, "Taxable", "1 1 1");
        Text(435, y, "F2", 9, $"GST {Pct(rate)}%", "1 1 1");
        Text(500, y, "F2", 9, "Amount", "1 1 1");
        y -= 20;

        for (var i = 0; i < lines.Count && y > 130; i++)
        {
            var l = lines[i];
            if (i % 2 == 1) sb.Append($"0.97 0.96 0.99 rg 40 {y - 5} 515 16 re f\n");
            Text(46, y, "F1", 9, (i + 1).ToString(CultureInfo.InvariantCulture));
            Text(66, y, "F1", 9, TruncPdf(l.Name, 36));
            Text(275, y, "F1", 9, l.Hsn ?? "-");
            Text(330, y, "F1", 9, l.Qty.ToString("0.##", CultureInfo.InvariantCulture));
            Text(370, y, "F1", 9, PdfMoney(l.Taxable));
            Text(435, y, "F1", 9, PdfMoney(l.Gst));
            Text(500, y, "F1", 9, PdfMoney(l.Gross));
            y -= 17;
        }

        y -= 10;
        Rule(y + 6);
        y -= 12;
        if (o.DiscountAmount > 0)
        {
            Text(330, y, "F1", 9, $"Discount: {PdfMoney(o.DiscountAmount)}");
            y -= 14;
        }
        Text(330, y, "F1", 9, $"Taxable Total: {PdfMoney(taxable)}");
        y -= 14;
        Text(330, y, "F1", 9, $"GST ({Pct(rate)}%): {PdfMoney(gst)}");
        y -= 16;
        Text(330, y, "F2", 11, $"Invoice Total: {PdfMoney(total)}", "0.294 0.180 0.514");
        Text(40, y + 14, "F2", 9, $"Amount in words: {Words((long)Math.Round(total, 0, MidpointRounding.AwayFromZero))} Only");

        Text(40, 68, "F1", 8, "Declaration: We declare that this invoice shows the actual price of the goods described and that all particulars are true.", "0.35 0.38 0.45");
        Text(40, 52, "F1", 8, "Thank you for your purchase. Computer-generated invoice - no signature required.", "0.35 0.38 0.45");

        var content = sb.ToString();
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [5 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 6 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
        };
        var outPdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Count; i++)
        {
            offsets.Add(outPdf.Length);
            outPdf.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        var xref = outPdf.Length;
        outPdf.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) outPdf.Append(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        outPdf.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(outPdf.ToString());
    }

    private static string PdfMoney(decimal v) => "Rs." + v.ToString("N2", CultureInfo.InvariantCulture);
    private static string TruncPdf(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
    private static string PdfClean(string s)
    {
        var t = (s ?? "").Replace("₹", "Rs.").Replace("–", "-").Replace("—", "-").Replace("’", "'").Replace("‘", "'").Replace("“", "\"").Replace("”", "\"");
        var arr = t.Select(ch => ch >= 32 && ch <= 126 ? ch : ' ').ToArray();
        return new string(arr);
    }
    private static string PdfEsc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static Line Split(string name, string? hsn, decimal qty, decimal gross, decimal divisor)
    {
        var taxable = Math.Round(gross / divisor, 2, MidpointRounding.AwayFromZero);
        return new Line(name, hsn, qty, gross, taxable, gross - taxable);
    }

    private static string Row(string label, decimal v) => $"<tr><td class=\"muted\">{label}</td><td class=\"r\">{M(v)}</td></tr>";
    private static string M(decimal v) => "₹" + v.ToString("N2", Inr);
    private static string Pct(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Words(long n)
    {
        if (n == 0) return "Zero Rupees";
        string[] ones = ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
        string[] tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];
        string Below100(long x) => x < 20 ? ones[x] : tens[x / 10] + (x % 10 > 0 ? " " + ones[x % 10] : "");
        string Below1000(long x) => (x >= 100 ? ones[x / 100] + " Hundred" + (x % 100 > 0 ? " " : "") : "") + (x % 100 > 0 ? Below100(x % 100) : "");
        var parts = new List<string>();
        foreach (var (div, label) in new[] { (10000000L, "Crore"), (100000L, "Lakh"), (1000L, "Thousand") })
            if (n >= div) { parts.Add(Below1000(n / div) + " " + label); n %= div; }
        if (n > 0) parts.Add(Below1000(n));
        return string.Join(" ", parts) + " Rupees";
    }
}
