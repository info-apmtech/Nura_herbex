using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public class CouponResult
{
    public bool Valid { get; set; }
    public decimal Discount { get; set; }
    public string? Code { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? Message { get; set; }
}

public class ParcelSpecs
{
    public decimal WeightKg { get; set; } = 0.38m;
    public decimal LengthCm { get; set; } = 10;
    public decimal BreadthCm { get; set; } = 10;
    public decimal HeightCm { get; set; } = 14;
}

public class CalculatedTotals
{
    public OrderTotalsDto Totals { get; set; } = new();
    public ParcelSpecs Parcel { get; set; } = new();
}

public class ProductService(NuraDbContext db, IOptions<StoreOptions> store)
{
    public async Task<List<Product>> ListActiveAsync() =>
        await db.Products.AsNoTracking().Where(p => p.Status == "active").OrderBy(p => p.Price).ToListAsync();

    public Task<Product?> GetAsync(string id) => db.Products.FirstOrDefaultAsync(p => p.Id == id);

    public async Task<CouponResult> ValidateCouponAsync(string? code, decimal subtotal)
    {
        var clean = (code ?? "").Trim().ToUpperInvariant();
        if (clean.Length == 0) return new CouponResult { Valid = false };

        var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == clean && c.IsActive);
        if (coupon is null || (coupon.ExpiresAt.HasValue && coupon.ExpiresAt < DateTime.UtcNow))
            return new CouponResult { Valid = false, Message = "Invalid coupon code" };

        if (subtotal < coupon.MinOrderAmount)
            return new CouponResult { Valid = false, Message = $"Coupon requires minimum order of ₹{coupon.MinOrderAmount:0}" };

        decimal discount;
        if (coupon.DiscountType == "PERCENTAGE")
        {
            discount = Math.Round(subtotal * coupon.DiscountValue / 100m, 0, MidpointRounding.AwayFromZero);
            if (coupon.MaxDiscountCap > 0 && discount > coupon.MaxDiscountCap) discount = coupon.MaxDiscountCap;
        }
        else discount = coupon.DiscountValue;

        return new CouponResult { Valid = true, Discount = discount, Code = clean, DiscountPercent = coupon.DiscountValue };
    }

    /// <summary>Authoritative cart pricing, shipping, tax and parcel dimensions (server-side, never trusts client prices).</summary>
    public async Task<CalculatedTotals> CalculateAsync(IEnumerable<CartLineDto>? rawItems, string? couponCode)
    {
        var lines = rawItems?.ToList() ?? new();
        if (lines.Count == 0) throw new InvalidOperationException("Cart cannot be empty");

        var items = new List<OrderItemDto>();
        decimal subtotal = 0, totalWeight = 0, maxLength = 10, maxBreadth = 10, totalHeight = 0;

        foreach (var line in lines)
        {
            var qty = Math.Max(1, line.Quantity);
            var product = await GetAsync(line.Id) ?? throw new InvalidOperationException($"Product not found: {line.Id}");
            if (product.StockQuantity < qty)
                throw new InvalidOperationException($"Insufficient stock for {product.Name}. Available: {product.StockQuantity}");

            var itemTotal = product.Price * qty;
            subtotal += itemTotal;
            totalWeight += product.WeightKg * qty;
            maxLength = Math.Max(maxLength, product.LengthCm);
            maxBreadth = Math.Max(maxBreadth, product.BreadthCm);
            totalHeight += product.HeightCm * qty;

            items.Add(new OrderItemDto
            {
                Id = product.Id, Sku = product.Sku, Name = product.Name, Price = product.Price, Quantity = qty,
                Total = itemTotal, Hsn = product.HsnCode, WeightKg = product.WeightKg, Image = product.ImageUrl,
            });
        }

        var coupon = await ValidateCouponAsync(couponCode, subtotal);
        var discount = coupon.Valid ? coupon.Discount : 0;
        var s = store.Value;
        var shipping = subtotal >= s.FreeShippingThreshold ? 0 : s.FlatShippingFee;

        // 18% GST is already inclusive in the listed price.
        var taxable = Math.Round((subtotal - discount) * 100m / 118m, 0, MidpointRounding.AwayFromZero);
        var tax = Math.Max(0, subtotal - discount - taxable);
        var finalTotal = Math.Max(0, subtotal - discount + shipping);

        return new CalculatedTotals
        {
            Totals = new OrderTotalsDto
            {
                Success = true, Items = items, Subtotal = subtotal, DiscountAmount = discount,
                CouponCode = coupon.Valid ? coupon.Code : null, DiscountPercent = coupon.Valid ? coupon.DiscountPercent : 0,
                ShippingFee = shipping, TaxAmount = tax, FinalTotal = finalTotal,
            },
            Parcel = new ParcelSpecs
            {
                WeightKg = Math.Round(totalWeight, 2), LengthCm = maxLength, BreadthCm = maxBreadth, HeightCm = Math.Min(60, totalHeight),
            },
        };
    }

    public async Task DecrementStockAsync(Order order)
    {
        foreach (var item in order.Items)
        {
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == item.Id);
            if (p is null) continue;
            p.StockQuantity = Math.Max(0, p.StockQuantity - item.Quantity);
            p.UpdatedAt = DateTime.UtcNow;
        }
    }
}
