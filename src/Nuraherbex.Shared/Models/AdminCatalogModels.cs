namespace Nuraherbex.Shared.Models;

public class AdminProductDto : ProductDto
{
    public decimal CostPrice { get; set; }
    public string? Category { get; set; }
    public string? FssaiLicense { get; set; }
    public DateTime UpdatedAt { get; set; }
}
public class AdminProductListResponse : ApiResult { public List<AdminProductDto> Products { get; set; } = new(); }
public class AdminProductResponse : ApiResult { public AdminProductDto? Product { get; set; } }
public class AdminCouponDto : CouponDto { public int OrdersUsed { get; set; } }
public class AdminCouponListResponse : ApiResult { public List<AdminCouponDto> Coupons { get; set; } = new(); }
public class AdminCouponResponse : ApiResult { public AdminCouponDto? Coupon { get; set; } }
