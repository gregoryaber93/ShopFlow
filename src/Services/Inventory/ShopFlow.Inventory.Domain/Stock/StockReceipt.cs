namespace ShopFlow.Inventory.Domain.Stock;

/// <summary>
/// Stock receipt into a warehouse. The identifier is assigned by the warehouse management system (WMS) that reports it.
/// </summary>
/// <remarks>
/// WHY: idempotency through a natural key, unlike the Idempotency-Key header in Orders. A receipt has its own
/// identifier from the warehouse document, and the table's primary key guarantees that the same receipt does not
/// increase the stock twice, even when the WMS resends it after a timeout. No separate key store and no stored
/// responses are needed.
/// </remarks>
public sealed record StockReceipt(Guid ReceiptId, string WarehouseCode, string Sku, int Quantity, DateTimeOffset ReceivedAt, string ReceivedBy);
