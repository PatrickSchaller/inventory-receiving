namespace Receiving.Api;

public record Item(string Sku, string Name, int Quantity, int ReorderPoint, long Version);
public record Movement(long Id, string Sku, string ItemName, string Kind, int Delta,
    int Balance, string Reference, string Note, string CreatedAt);
public record ReceiptLine(string? Sku, int Quantity);
public record ReceiveRequest(string? Reference, List<ReceiptLine?>? Lines);
public record AdjustRequest(string? Sku, int Delta, long ExpectedVersion, string? Reason);
public record ReceiptResult(string Reference, string CreatedAt, bool Replayed);
public sealed class InventoryException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
