using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

[ApiController]
[Route("api/shipping")]
public sealed class ShippingController(OrderService orders, ShippingGateway shipping) : ControllerBase
{
    [HttpGet("{id}/track")]
    [Authorize(Roles = "admin,customer")]
    public async Task<IActionResult> Track(string id)
    {
        var order = await orders.GetAsync(id);
        if (order is null || (!User.IsInRole("admin") && (order.CustomerId is null || order.CustomerId != User.FindFirstValue(ClaimTypes.NameIdentifier))))
            return NotFound(new ApiResult { Message = "Order not found" });
        var dto = order.ToDto();
        await shipping.EnrichTrackingAsync(dto);
        return Ok(new OrderResponse { Success = true, Order = dto });
    }

    [HttpGet("serviceability/{pincode}")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("orders")]
    public async Task<IActionResult> Serviceability(string pincode)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pincode, "^[1-9][0-9]{5}$")) return BadRequest(new ApiResult { Message = "Invalid pincode" });
        return Ok(new { success = true, pincode, serviceable = await shipping.IsServiceableAsync(pincode) });
    }
}