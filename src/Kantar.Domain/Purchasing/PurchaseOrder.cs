using Kantar.Domain.Common;
using Kantar.Domain.MasterData;
using Kantar.Domain.Sales;

namespace Kantar.Domain.Purchasing;

public enum PurchaseOrderStatus : byte
{
    Draft = 1,
    Approved = 2,
    PartiallyReceived = 3,
    Received = 4,
    Cancelled = 5,
}

public sealed class PurchaseOrder
{
    public const int MaxLines = 50;
    public const int MaxLineQuantity = 100_000;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Number { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public Guid SupplierId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string? Note { get; set; }

    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? CancelReason { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<PurchaseOrderLine> Lines { get; set; } = [];

    public decimal Total => Lines.Sum(l => l.UnitCost * l.OrderedQty);

    public bool HasReceipts => Lines.Any(l => l.ReceivedQty > 0);

    public void SnapshotSupplier(Supplier supplier)
    {
        SupplierId = supplier.Id;
        SupplierCode = supplier.Code;
        SupplierName = supplier.Name;
    }

    public void ReplaceLines(IReadOnlyList<(Product Product, int Quantity, decimal UnitCost)> lines, DateTime now)
    {
        EnsureStatus(PurchaseOrderStatus.Draft);
        ValidateDraftLines(lines.Select(l => (l.Product.Id, l.Quantity, l.UnitCost)).ToArray());

        Lines.Clear();
        var lineNo = 0;
        foreach (var (product, quantity, unitCost) in lines)
        {
            Lines.Add(new PurchaseOrderLine
            {
                OrderId = Id,
                LineNo = ++lineNo,
                ProductId = product.Id,
                Sku = product.Sku,
                ProductName = product.Name,
                Unit = product.Unit,
                UnitCost = unitCost,
                OrderedQty = quantity,
            });
        }

        UpdatedAt = now;
    }

    public static void ValidateDraftLines(IReadOnlyList<(Guid ProductId, int Quantity, decimal UnitCost)> lines)
    {
        var validator = new Validator();
        if (lines.Count is 0 or > MaxLines)
        {
            validator.Add("lines", "line_count", $"Satır sayısı 1 ile {MaxLines} arasında olmalıdır.");
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            validator.Range($"lines[{i}].quantity", lines[i].Quantity, 1, MaxLineQuantity);
            validator.Money($"lines[{i}].unitCost", lines[i].UnitCost, 99_999_999.99m);
            if (!seen.Add(lines[i].ProductId))
            {
                validator.Add($"lines[{i}].productId", "duplicate_product", "Aynı ürün bir belgede yalnız bir satırda yer alabilir.");
            }
        }

        validator.ThrowIfInvalid();
    }

    public void Approve(Supplier supplier, Guid approverId, decimal approverLimit, DateTime now)
    {
        EnsureStatus(PurchaseOrderStatus.Draft);
        if (Lines.Count == 0)
        {
            throw Problems.InvalidState("Satırı olmayan sipariş onaylanamaz.");
        }

        // The limit is part of the state transition, not only of the endpoint: a retry after the
        // order grew, or after the approver's limit was lowered, is refused here.
        if (approverLimit <= 0 || Total > approverLimit)
        {
            throw Problems.Forbidden("approval_limit_exceeded", "Sipariş toplamı onay limitinizi aşıyor.");
        }

        SnapshotSupplier(supplier);
        Status = PurchaseOrderStatus.Approved;
        ApprovedBy = approverId;
        ApprovedAt = now;
        UpdatedAt = now;
    }

    public IReadOnlyList<LineEffect> Receive(IReadOnlyList<LineQuantity> requested, DateTime now)
    {
        if (Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))
        {
            throw Problems.InvalidState($"Sipariş {Status} durumunda; mal kabul yapılamaz.");
        }

        var effects = Fulfilment.Apply(
            requested,
            Lines.ToDictionary(l => l.Id),
            l => l.OpenQty,
            (l, q) => l.ReceivedQty += q,
            l => l.ProductId);

        Status = Lines.All(l => l.OpenQty == 0) ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        if (Status == PurchaseOrderStatus.Received)
        {
            ClosedAt = now;
        }

        UpdatedAt = now;
        return effects;
    }

    /// <summary>Closes the open quantity. Received quantities stay; there is no stock effect.</summary>
    public void Cancel(string reason, DateTime now)
    {
        if (Status is PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled)
        {
            throw Problems.InvalidState($"Sipariş {Status} durumunda; iptal edilemez.");
        }

        if (Status != PurchaseOrderStatus.Draft)
        {
            foreach (var line in Lines)
            {
                line.CancelledQty += line.OpenQty;
            }
        }

        Status = PurchaseOrderStatus.Cancelled;
        CancelReason = reason;
        ClosedAt = now;
        UpdatedAt = now;
    }

    private void EnsureStatus(PurchaseOrderStatus expected)
    {
        if (Status != expected)
        {
            throw Problems.InvalidState($"Sipariş {Status} durumunda; bu işlem yalnız {expected} durumunda yapılabilir.");
        }
    }
}

public sealed class PurchaseOrderLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrderId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public required string Sku { get; set; }
    public required string ProductName { get; set; }
    public required string Unit { get; set; }
    public decimal UnitCost { get; set; }
    public int OrderedQty { get; set; }
    public int ReceivedQty { get; set; }
    public int CancelledQty { get; set; }

    public int OpenQty => OrderedQty - ReceivedQty - CancelledQty;
}

public sealed class GoodsReceipt
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Number { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid WarehouseId { get; set; }
    public required string ExternalReference { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<GoodsReceiptLine> Lines { get; set; } = [];
}

public sealed class GoodsReceiptLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ReceiptId { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}
