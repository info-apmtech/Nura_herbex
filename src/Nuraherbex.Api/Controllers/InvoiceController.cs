using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuraherbex.Api.Services;

namespace Nuraherbex.Api.Controllers;

/// <summary>Opens a GST invoice from a signed, short-lived link (browsers can't send a login header when opening a new tab).</summary>
[ApiController]
[AllowAnonymous]
[Route("api/invoices")]
public class InvoiceController(OrderService orders, InvoiceService invoices) : ControllerBase
{
    [HttpGet("{orderId}")]
    public async Task<IActionResult> Get(string orderId, [FromQuery] long exp, [FromQuery] string? sig)
    {
        if (!invoices.IsValid(orderId, exp, sig)) return StatusCode(403);
        var order = await orders.GetAsync(orderId);
        if (order is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Content(invoices.RenderHtml(order), "text/html; charset=utf-8");
    }
}
