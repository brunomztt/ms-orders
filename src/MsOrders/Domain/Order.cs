using System.ComponentModel.DataAnnotations;

namespace MsOrders.Domain;

public record OrderItemRequest([Range(1, int.MaxValue)] int ProductId,
    [Range(1, 100000)] int Quantity);
public sealed class OrderRequest : IValidatableObject
{
    [Required, StringLength(100)] public string UserId { get; set; } = "";
    [Required, MinLength(1), MaxLength(1)] public List<OrderItemRequest> Items { get; set; } = [];
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Items is not null && (Items.Any(i => i is null)
            || Items.Select(i => i?.ProductId).Distinct().Count() != Items.Count))
            yield return new("Items must be non-null and use distinct product IDs", [nameof(Items)]);
    }
}
public sealed class Order
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Status { get; set; } = "DRAFT";
    public string ItemsJson { get; set; } = "[]";
    public decimal? Total { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public object ToResponse() => new { Id, UserId, Status, Items = System.Text.Json.JsonSerializer.Deserialize<object>(ItemsJson), Total, FailureReason, CreatedAt, UpdatedAt };
}
public sealed class OrderConflictException(string message) : Exception(message);
