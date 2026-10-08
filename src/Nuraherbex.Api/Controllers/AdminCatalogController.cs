using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

[ApiController, Authorize(Roles = "admin"), Route("api/admin/catalog")]
public class AdminCatalogController(NuraDbContext db) : ControllerBase
{
    private static AdminProductDto ProductDto(Product p) => new()
    {
        Id = p.Id, Sku = p.Sku, Name = p.Name, Subtitle = p.Subtitle, Description = p.Description,
        Price = p.Price, CompareAtPrice = p.CompareAtPrice, CostPrice = p.CostPrice,
        StockQuantity = p.StockQuantity, WeightKg = p.WeightKg, LengthCm = p.LengthCm,
        BreadthCm = p.BreadthCm, HeightCm = p.HeightCm, HsnCode = p.HsnCode,
        ImageUrl = p.ImageUrl, Status = p.Status, Category = p.Category, FssaiLicense = p.FssaiLicense, UpdatedAt = p.UpdatedAt
    };
    private static AdminCouponDto CouponDto(Coupon c, int used = 0) => new()
    {
        Code = c.Code, DiscountType = c.DiscountType, DiscountValue = c.DiscountValue,
        MinOrderAmount = c.MinOrderAmount, MaxDiscountCap = c.MaxDiscountCap,
        IsActive = c.IsActive, ExpiresAt = c.ExpiresAt, OrdersUsed = used
    };
    private static string? ValidateProduct(AdminProductDto p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length > 300) return "Product name is required (maximum 300 characters).";
        if (string.IsNullOrWhiteSpace(p.Sku) || p.Sku.Trim().Length > 64) return "SKU is required (maximum 64 characters).";
        if (p.Price <= 0 || p.Price > 10000000 || p.CostPrice < 0 || p.CostPrice > 10000000) return "Enter a valid selling price and non-negative cost price.";
        if (p.CompareAtPrice.HasValue && (p.CompareAtPrice < p.Price || p.CompareAtPrice > 10000000)) return "Compare-at price must be at least the selling price, or left empty.";
        if (p.StockQuantity < 0 || p.StockQuantity > 1000000) return "Stock must be between 0 and 1,000,000.";
        if (p.WeightKg <= 0 || p.WeightKg > 1000 || p.LengthCm <= 0 || p.BreadthCm <= 0 || p.HeightCm <= 0 || p.LengthCm > 1000 || p.BreadthCm > 1000 || p.HeightCm > 1000) return "Enter positive parcel weight and dimensions (maximum 1,000 each).";
        if (p.Status is not ("active" or "draft" or "archived")) return "Choose Active, Draft or Archived.";
        if ((p.Description?.Length ?? 0) > 10000 || (p.Subtitle?.Length ?? 0) > 300 || (p.Category?.Length ?? 0) > 100 || (p.HsnCode?.Length ?? 0) > 32 || (p.FssaiLicense?.Length ?? 0) > 64) return "One or more product details are too long.";
        var image = p.ImageUrl?.Trim() ?? "";
        if (image.Length > 2048 || (image.Length > 0 && !(image.StartsWith('/') && !image.StartsWith("//") && !image.Contains('\\')) && !(Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))) return "Use an HTTPS image URL or a local path beginning with /.";
        return null;
    }
    [HttpGet("products")]
    public async Task<AdminProductListResponse> Products() => new() { Success = true, Products = (await db.Products.AsNoTracking().OrderByDescending(p => p.UpdatedAt).ToListAsync()).Select(ProductDto).ToList() };

    [HttpPost("products")]
    public Task<IActionResult> CreateProduct(AdminProductDto request) => SaveProduct(null, request);
    [HttpPut("products/{id}")]
    public Task<IActionResult> UpdateProduct(string id, AdminProductDto request) => SaveProduct(id, request);
    private async Task<IActionResult> SaveProduct(string? id, AdminProductDto r)
    {
        var error = ValidateProduct(r);
        if (error is not null) return BadRequest(new ApiResult { Message = error });
        var sku = r.Sku.Trim().ToUpperInvariant();
        if (await db.Products.AnyAsync(p => p.Sku == sku && p.Id != id)) return Conflict(new ApiResult { Message = "This SKU already belongs to another product." });
        var p = id is null ? new Product { Id = Guid.NewGuid().ToString("N") } : await db.Products.FindAsync(id);
        if (p is null) return NotFound(new ApiResult { Message = "Product not found. Refresh the catalog." });
        if (id is not null && r.UpdatedAt != p.UpdatedAt) return Conflict(new ApiResult { Message = "This product changed since you opened it. Close the editor and refresh before editing again." });
        p.Sku = sku; p.Name = r.Name.Trim(); p.Subtitle = r.Subtitle?.Trim(); p.Description = r.Description?.Trim();
        p.Price = decimal.Round(r.Price, 2); p.CompareAtPrice = r.CompareAtPrice.HasValue ? decimal.Round(r.CompareAtPrice.Value, 2) : null;
        p.CostPrice = decimal.Round(r.CostPrice, 2); p.StockQuantity = r.StockQuantity; p.WeightKg = decimal.Round(r.WeightKg, 3);
        p.LengthCm = decimal.Round(r.LengthCm, 2); p.BreadthCm = decimal.Round(r.BreadthCm, 2); p.HeightCm = decimal.Round(r.HeightCm, 2);
        if (p.Price <= 0 || p.WeightKg <= 0 || p.LengthCm <= 0 || p.BreadthCm <= 0 || p.HeightCm <= 0) return BadRequest(new ApiResult { Message = "Price and parcel values are too small." });
        p.HsnCode = r.HsnCode?.Trim() ?? ""; p.FssaiLicense = r.FssaiLicense?.Trim(); p.Category = r.Category?.Trim();
        p.ImageUrl = r.ImageUrl?.Trim() ?? ""; p.Status = r.Status; p.UpdatedAt = DateTime.UtcNow;
        if (id is null) db.Products.Add(p);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Conflict(new ApiResult { Message = "Could not save. Check that the SKU is unique and retry." }); }
        return StatusCode(id is null ? 201 : 200, new AdminProductResponse { Success = true, Product = ProductDto(p), Message = "Product saved." });
    }
    [HttpDelete("products/{id}")]
    public async Task<IActionResult> ArchiveProduct(string id)
    {
        var product = await db.Products.FindAsync(id);
        if (product is null) return NotFound(new ApiResult { Message = "Product not found." });
        product.Status = "archived"; product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = "Product archived. Previous orders are preserved." });
    }
    [HttpGet("coupons")]
    public async Task<AdminCouponListResponse> Coupons()
    {
        var usage = await db.Orders.Where(o => o.CouponCode != null).GroupBy(o => o.CouponCode!).Select(g => new { Code = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Code, x => x.Count);
        return new() { Success = true, Coupons = (await db.Coupons.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync()).Select(c => CouponDto(c, usage.GetValueOrDefault(c.Code))).ToList() };
    }
    [HttpPost("coupons")]
    public Task<IActionResult> CreateCoupon(CouponDto r) => SaveCoupon(null, r);
    [HttpPut("coupons/{code}")]
    public Task<IActionResult> UpdateCoupon(string code, CouponDto r) => SaveCoupon(code, r);
    private async Task<IActionResult> SaveCoupon(string? code, CouponDto r)
    {
        var clean = r.Code?.Trim().ToUpperInvariant() ?? "";
        if (clean.Length is < 3 or > 64 || clean.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return BadRequest(new ApiResult { Message = "Code must be 3–64 letters, numbers, hyphens or underscores." });
        if (r.DiscountType is not ("PERCENTAGE" or "FIXED") || r.DiscountValue <= 0 || r.DiscountValue > 10000000 || (r.DiscountType == "PERCENTAGE" && r.DiscountValue > 100)) return BadRequest(new ApiResult { Message = "Enter a valid discount: 0–100% or a positive fixed amount." });
        if (r.MinOrderAmount < 0 || r.MinOrderAmount > 10000000 || r.MaxDiscountCap < 0 || r.MaxDiscountCap > 10000000) return BadRequest(new ApiResult { Message = "Minimum order and discount cap must be non-negative amounts." });
        if (code is not null && !string.Equals(code, clean, StringComparison.OrdinalIgnoreCase)) return BadRequest(new ApiResult { Message = "Coupon codes cannot be renamed. Create another coupon instead." });
        if (code is null && await db.Coupons.AnyAsync(c => c.Code == clean)) return Conflict(new ApiResult { Message = "This coupon code already exists." });
        var coupon = code is null ? new Coupon { Code = clean } : await db.Coupons.FindAsync(clean);
        if (coupon is null) return NotFound(new ApiResult { Message = "Coupon not found." });
        coupon.DiscountType = r.DiscountType; coupon.DiscountValue = decimal.Round(r.DiscountValue, 2);
        if (coupon.DiscountValue <= 0) return BadRequest(new ApiResult { Message = "Discount must be at least 0.01." });
        coupon.MinOrderAmount = decimal.Round(r.MinOrderAmount, 2); coupon.MaxDiscountCap = decimal.Round(r.MaxDiscountCap, 2);
        coupon.IsActive = r.IsActive; coupon.ExpiresAt = r.ExpiresAt?.ToUniversalTime();
        if (code is null) db.Coupons.Add(coupon);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Conflict(new ApiResult { Message = "Could not save this coupon. Refresh and try again." }); }
        return StatusCode(code is null ? 201 : 200, new AdminCouponResponse { Success = true, Coupon = CouponDto(coupon), Message = "Coupon saved." });
    }
    [HttpDelete("coupons/{code}")]
    public async Task<IActionResult> DisableCoupon(string code)
    {
        var coupon = await db.Coupons.FindAsync(code.Trim().ToUpperInvariant());
        if (coupon is null) return NotFound(new ApiResult { Message = "Coupon not found." });
        coupon.IsActive = false;
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = "Coupon disabled." });
    }
}
