namespace Kantar.Domain.Inventory;

public enum MovementType : byte
{
    Receipt = 1,
    Shipment = 2,
    TransferOut = 3,
    TransferIn = 4,
    Adjustment = 5,
}

public enum ReferenceType : byte
{
    GoodsReceipt = 1,
    Shipment = 2,
    StockTransfer = 3,
    StockAdjustment = 4,
}

public enum ReservationReason : byte
{
    Reserve = 1,
    ReleaseOnShipment = 2,
    ReleaseOnCancel = 3,
}

public enum AdjustmentReason : byte
{
    CountDifference = 1,
    Damage = 2,
    Loss = 3,
    Found = 4,
    DataCorrection = 5,
}

/// <summary>
/// One row per (warehouse, product). Never written through the change tracker:
/// every change goes through the conditional UPDATE in the stock ledger.
/// </summary>
public sealed class StockBalance
{
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public int Available => OnHand - Reserved;
}

/// <summary>Append-only journal of every OnHand change.</summary>
public sealed class StockMovement
{
    public long Id { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public MovementType Type { get; set; }
    public int QuantityDelta { get; set; }
    public int OnHandAfter { get; set; }
    public ReferenceType ReferenceType { get; set; }
    public Guid ReferenceId { get; set; }
    public Guid ReferenceLineId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTime OccurredAt { get; set; }
}

/// <summary>Append-only ledger of every Reserved change, kept apart from the OnHand journal.</summary>
public sealed class ReservationEntry
{
    public long Id { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public int QuantityDelta { get; set; }
    public int ReservedAfter { get; set; }
    public ReservationReason Reason { get; set; }
    public Guid SalesOrderId { get; set; }
    public Guid SalesOrderLineId { get; set; }
    public Guid? ShipmentLineId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTime OccurredAt { get; set; }
}

public sealed class StockTransfer
{
    public const int MaxLines = 50;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Number { get; set; }
    public required string Reference { get; set; }
    public Guid FromWarehouseId { get; set; }
    public Guid ToWarehouseId { get; set; }
    public string? Note { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<StockTransferLine> Lines { get; set; } = [];
}

public sealed class StockTransferLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TransferId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}

public sealed class StockAdjustment
{
    public const int MaxLines = 50;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Number { get; set; }
    public required string Reference { get; set; }
    public Guid WarehouseId { get; set; }
    public AdjustmentReason ReasonCode { get; set; }
    public required string Note { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<StockAdjustmentLine> Lines { get; set; } = [];
}

public sealed class StockAdjustmentLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AdjustmentId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public int QuantityDelta { get; set; }
}
