using Kantar.Domain.Common;
using Kantar.Domain.MasterData;

namespace Kantar.Domain.Sales;

public enum SalesOrderStatus : byte
{
    Draft = 1,
    Confirmed = 2,
    PartiallyShipped = 3,
    Shipped = 4,
    Cancelled = 5,
}

public readonly record struct LineQuantity(Guid LineId, int Quantity);

/// <summary>A quantity change on one order line that the stock ledger must mirror.</summary>
public readonly record struct LineEffect(Guid LineId, Guid ProductId, int Quantity);

public sealed class SalesOrder
{
    public const int MaxLines = 50;
    public const int MaxLineQuantity = 100_000;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Number { get; set; }
    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;
    public Guid CustomerId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string? Note { get; set; }

    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerTaxNumber { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? CancelReason { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<SalesOrderLine> Lines { get; set; } = [];

    public decimal Total => Lines.Sum(l => l.UnitPrice * l.OrderedQty);

    public bool HasShipments => Lines.Any(l => l.ShippedQty > 0);

    public void SnapshotCustomer(Customer customer)
    {
        CustomerId = customer.Id;
        CustomerCode = customer.Code;
        CustomerName = customer.Name;
        CustomerTaxNumber = customer.TaxNumber;
        ShippingAddress = customer.Address;
    }

    /// <summary>Replaces the draft's lines. Prices and names come from the product row, never from the caller.</summary>
    public void ReplaceLines(IReadOnlyList<(Product Product, int Quantity)> lines, DateTime now)
    {
        EnsureStatus(SalesOrderStatus.Draft);
        ValidateDraftLines(lines.Select(l => (l.Product.Id, l.Quantity)).ToArray());

        Lines.Clear();
        var lineNo = 0;
        foreach (var (product, quantity) in lines)
        {
            Lines.Add(new SalesOrderLine
            {
                OrderId = Id,
                LineNo = ++lineNo,
                ProductId = product.Id,
                Sku = product.Sku,
                ProductName = product.Name,
                Unit = product.Unit,
                UnitPrice = product.ListPrice,
                OrderedQty = quantity,
            });
        }

        UpdatedAt = now;
    }

    public static void ValidateDraftLines(IReadOnlyList<(Guid ProductId, int Quantity)> lines)
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
            if (!seen.Add(lines[i].ProductId))
            {
                validator.Add($"lines[{i}].productId", "duplicate_product", "Aynı ürün bir belgede yalnız bir satırda yer alabilir.");
            }
        }

        validator.ThrowIfInvalid();
    }

    /// <summary>Draft → Confirmed. Returns the quantities that must be reserved, all or nothing.</summary>
    public IReadOnlyList<LineEffect> Confirm(Customer customer, DateTime now)
    {
        EnsureStatus(SalesOrderStatus.Draft);
        if (Lines.Count == 0)
        {
            throw Problems.InvalidState("Satırı olmayan sipariş onaylanamaz.");
        }

        SnapshotCustomer(customer);
        Status = SalesOrderStatus.Confirmed;
        ConfirmedAt = now;
        UpdatedAt = now;
        return Lines.OrderBy(l => l.LineNo).Select(l => new LineEffect(l.Id, l.ProductId, l.OrderedQty)).ToArray();
    }

    /// <summary>Ships part or all of the open quantity. Returns what left the warehouse per line.</summary>
    public IReadOnlyList<LineEffect> Ship(IReadOnlyList<LineQuantity> requested, DateTime now)
    {
        if (Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyShipped))
        {
            throw Problems.InvalidState($"Sipariş {Status} durumunda; sevk edilemez.");
        }

        var effects = Fulfilment.Apply(
            requested,
            Lines.ToDictionary(l => l.Id),
            l => l.OpenQty,
            (l, q) => l.ShippedQty += q,
            l => l.ProductId);

        Status = Lines.All(l => l.OpenQty == 0) ? SalesOrderStatus.Shipped : SalesOrderStatus.PartiallyShipped;
        if (Status == SalesOrderStatus.Shipped)
        {
            ClosedAt = now;
        }

        UpdatedAt = now;
        return effects;
    }

    /// <summary>
    /// Cancels whatever has not shipped. Returns the reservations to release; shipped quantities
    /// stay as they are and no stock comes back.
    /// </summary>
    public IReadOnlyList<LineEffect> Cancel(string reason, DateTime now)
    {
        if (Status is SalesOrderStatus.Shipped or SalesOrderStatus.Cancelled)
        {
            throw Problems.InvalidState($"Sipariş {Status} durumunda; iptal edilemez.");
        }

        var released = new List<LineEffect>();
        if (Status != SalesOrderStatus.Draft)
        {
            foreach (var line in Lines.OrderBy(l => l.LineNo))
            {
                var open = line.OpenQty;
                if (open > 0)
                {
                    line.CancelledQty += open;
                    released.Add(new LineEffect(line.Id, line.ProductId, open));
                }
            }
        }

        Status = SalesOrderStatus.Cancelled;
        CancelReason = reason;
        ClosedAt = now;
        UpdatedAt = now;
        return released;
    }

    private void EnsureStatus(SalesOrderStatus expected)
    {
        if (Status != expected)
        {
            throw Problems.InvalidState($"Sipariş {Status} durumunda; bu işlem yalnız {expected} durumunda yapılabilir.");
        }
    }
}

public sealed class SalesOrderLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrderId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public required string Sku { get; set; }
    public required string ProductName { get; set; }
    public required string Unit { get; set; }
    public decimal UnitPrice { get; set; }
    public int OrderedQty { get; set; }
    public int ShippedQty { get; set; }
    public int CancelledQty { get; set; }

    public int OpenQty => OrderedQty - ShippedQty - CancelledQty;
}

public sealed class Shipment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Number { get; set; }
    public Guid SalesOrderId { get; set; }
    public Guid WarehouseId { get; set; }
    public required string DispatchReference { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ShipmentLine> Lines { get; set; } = [];
}

public sealed class ShipmentLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ShipmentId { get; set; }
    public Guid SalesOrderLineId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}

/// <summary>Shared rule for "fulfil part of what is still open" used by shipments and goods receipts.</summary>
public static class Fulfilment
{
    public static IReadOnlyList<LineEffect> Apply<TLine>(
        IReadOnlyList<LineQuantity> requested,
        IReadOnlyDictionary<Guid, TLine> lines,
        Func<TLine, int> openQuantity,
        Action<TLine, int> apply,
        Func<TLine, Guid> productId)
    {
        if (requested.Count == 0 || requested.Count > lines.Count)
        {
            throw Problems.Validation("lines", "line_count", "En az bir satır, en fazla belgedeki satır sayısı kadar satır gönderilebilir.");
        }

        var mismatches = new List<FieldError>();
        var excess = new List<FieldError>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < requested.Count; i++)
        {
            var (lineId, quantity) = requested[i];
            if (!lines.TryGetValue(lineId, out var line) || !seen.Add(lineId))
            {
                mismatches.Add(new FieldError($"lines[{i}].orderLineId", "line_mismatch", "Satır bu belgeye ait değil veya iki kez gönderildi."));
                continue;
            }

            if (quantity < 1)
            {
                throw Problems.Validation($"lines[{i}].quantity", "out_of_range", "Miktar en az 1 olmalıdır.");
            }

            var open = openQuantity(line);
            if (quantity > open)
            {
                excess.Add(new FieldError($"lines[{i}].quantity", "exceeds_remaining", $"İstenen {quantity}, kalan {open}"));
            }
        }

        if (mismatches.Count > 0)
        {
            throw Problems.Unprocessable("line_mismatch", null, mismatches);
        }

        if (excess.Count > 0)
        {
            throw Problems.Conflict("exceeds_remaining", null, excess);
        }

        var effects = new List<LineEffect>(requested.Count);
        foreach (var (lineId, quantity) in requested)
        {
            var line = lines[lineId];
            apply(line, quantity);
            effects.Add(new LineEffect(lineId, productId(line), quantity));
        }

        return effects;
    }
}
