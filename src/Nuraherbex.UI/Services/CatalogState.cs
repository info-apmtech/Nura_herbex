using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Services;

/// <summary>Shared, cached view of the product catalogue (the DB via api/products) so every home section reads the same live prices and details.</summary>
public class CatalogState(ApiClient api)
{
    private Task<List<ProductDto>>? _load;

    public Task<List<ProductDto>> GetAsync() => _load ??= LoadAsync();

    private async Task<List<ProductDto>> LoadAsync()
    {
        var list = await api.GetProductsAsync();
        if (list.Count == 0) _load = null;   // API unreachable / empty: try again next time
        return list;
    }

    public async Task<ProductDto?> FindAsync(string id) => (await GetAsync()).FirstOrDefault(p => p.Id == id);
}
