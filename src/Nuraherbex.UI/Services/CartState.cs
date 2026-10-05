using System.Text.Json;
using Nuraherbex.Shared.Models;
using Nuraherbex.UI.Config;

namespace Nuraherbex.UI.Services;

public class CartItem
{
    public string Id { get; set; } = "";
    public string? VariantId { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Image { get; set; } = "";
    public string? ServingsText { get; set; }
    public int Quantity { get; set; } = 1;
    public bool IsSubscription { get; set; }
}

/// <summary>Port of CartContext.jsx. Scoped singleton in the browser; persisted under localStorage key nh_stamix_cart.</summary>
public class CartState(BrowserInterop browser)
{
    private const string StorageKey = "nh_stamix_cart";
    private bool _loaded;

    public List<CartItem> Items { get; private set; } = new();
    public bool IsCartOpen { get; private set; }

    public event Action? Changed;

    public int ItemCount => Items.Sum(i => i.Quantity);
    public decimal Subtotal => Items.Sum(i => i.Price * i.Quantity);
    /// <summary>0–100, progress towards the free-shipping threshold.</summary>
    public decimal FreeShippingProgress => Math.Min(100, Subtotal / SiteConfig.FreeShippingThreshold * 100m);

    public async Task InitAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var raw = await browser.GetLocalAsync(StorageKey);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { Items = JsonSerializer.Deserialize<List<CartItem>>(raw, NuraJson.Options) ?? new(); } catch { Items = new(); }
        }
        Changed?.Invoke();
    }

    public async Task AddToCartAsync(CartItem item, int? qty = null)
    {
        var q = Math.Max(1, qty ?? item.Quantity);
        var existing = Items.FirstOrDefault(i => i.Id == item.Id);
        if (existing is not null) existing.Quantity += q;
        else { item.Quantity = q; Items.Add(item); }
        IsCartOpen = true;
        await SaveAsync();
    }

    public async Task UpdateQuantityAsync(string id, int delta)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        item.Quantity += delta;
        if (item.Quantity <= 0) Items.Remove(item);
        await SaveAsync();
    }

    public async Task RemoveItemAsync(string id) { Items.RemoveAll(i => i.Id == id); await SaveAsync(); }
    public async Task ClearAsync() { Items.Clear(); await SaveAsync(); }

    public void SetOpen(bool open) { IsCartOpen = open; Changed?.Invoke(); }

    /// <summary>Server-side line format for /api/orders and /api/orders/validate.</summary>
    public List<CartLineDto> ToLines() => Items.Select(i => new CartLineDto { Id = i.Id, Quantity = i.Quantity }).ToList();

    private async Task SaveAsync()
    {
        Changed?.Invoke();
        await browser.SetLocalAsync(StorageKey, JsonSerializer.Serialize(Items, NuraJson.Options));
    }
}
