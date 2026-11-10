using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MsOrders.Domain;

namespace MsOrders.Services;

public record ReservationResult(bool Accepted, decimal Total, string? Reason);
public sealed class CatalogClient(HttpClient http)
{
    public async Task<ReservationResult> Reserve(Order order, CancellationToken ct)
    {
        var items = JsonSerializer.Deserialize<List<OrderItemRequest>>(order.ItemsJson)!;
        if (items.Count != 1)
            return new(false, 0, "This version supports one product per order");
        var item = items[0];
        using var productResponse = await http.GetAsync($"api/catalog/products/{item.ProductId}", ct);
        if (productResponse.StatusCode == HttpStatusCode.NotFound)
            return new(false, 0, "Product not found");
        productResponse.EnsureSuccessStatusCode();
        var product = await productResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var price = product.GetProperty("price");
        var unitPrice = price.ValueKind == JsonValueKind.String
            ? decimal.Parse(price.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : price.GetDecimal();
        using var response = await http.PostAsJsonAsync($"api/catalog/products/{item.ProductId}/reservations", new {
            order_id = order.Id, quantity = item.Quantity }, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            return new(false, 0, "Product unavailable or insufficient stock");
        response.EnsureSuccessStatusCode();
        return new(true, unitPrice * item.Quantity, null);
    }
}
