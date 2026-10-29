using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Receiving.Api;

public record Item(string Sku, string Name, int Quantity, int ReorderPoint, long Version);
public record Movement(long Id, string Sku, string ItemName, string Kind, int Delta,
    int Balance, string Reference, string Note, string CreatedAt);
public record ReceiptLine(
    [property: Required, MaxLength(32), Description("Item code; trimmed and uppercased before lookup.")] string? Sku,
    [property: Range(1, 100_000)] int Quantity);
public record ReceiveRequest(
    [property: Required, MaxLength(80), Description("Durable receipt identity, trimmed and uppercased. Reuse with the same lines to replay safely.")] string? Reference,
    [property: Required, MinLength(1), MaxLength(20), Description("One to twenty non-null lines, with each item code present once.")] List<ReceiptLine?>? Lines);
public record AdjustRequest(
    [property: Required, MaxLength(32)] string? Sku,
    [property: Range(-100_000, 100_000), Description("Nonzero stock change. Zero is rejected.")] int Delta,
    [property: Range(0, long.MaxValue), Description("Copy the current item version from GET /api/items. A stale version returns 409.")] long ExpectedVersion,
    [property: Required, MaxLength(240)] string? Reason);
public record ReceiptResult(string Reference, string CreatedAt, bool Replayed);
public sealed class InventoryException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
