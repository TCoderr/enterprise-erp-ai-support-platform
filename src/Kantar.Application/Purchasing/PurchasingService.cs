using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.Inventory;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Inventory;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Purchasing;

public sealed record PurchaseLineInput(Guid ProductId, int Quantity, decimal UnitCost);

public sealed record SavePurchaseOrderCommand(Guid? Id, Guid SupplierId, Guid WarehouseId, string? Note, IReadOnlyList<PurchaseLineInput> Lines, string? IfMatch);

public sealed record PurchaseOrderActionCommand(Guid Id, string? Reason, string? IfMatch);

public sealed record FulfilmentLineInput(Guid OrderLineId, int Quantity);

public sealed record ReceiveCommand(Guid OrderId, string ExternalReference, IReadOnlyList<FulfilmentLineInput> Lines, string? IfMatch);

/// <summary>UnitCost and Total are null for the fulfilment view (warehouse operators see quantities only).</summary>
public sealed record PurchaseLineDto(
    Guid Id, int LineNo, Guid ProductId, string Sku, string ProductName, string Unit, decimal? UnitCost,
    int OrderedQty, int ReceivedQty, int CancelledQty, int OpenQty);

public sealed record ReceiptSummaryDto(Guid Id, string Number, string ExternalReference, DateTime CreatedAt, int TotalQuantity);

public sealed record PurchaseOrderDto(
    Guid Id, string Number, string Status, string View, Guid SupplierId, string SupplierCode, string SupplierName,
    Guid WarehouseId, Guid OwnerUserId, string OwnerDisplayName, string? Note, decimal? Total, DateTime CreatedAt, DateTime? ApprovedAt, DateTime? ClosedAt,
    string? CancelReason, IReadOnlyList<PurchaseLineDto> Lines, IReadOnlyList<ReceiptSummaryDto> Receipts, string ETag);

public sealed record PurchaseOrderListItemDto(
    Guid Id, string Number, string Status, string SupplierName, Guid WarehouseId, Guid OwnerUserId, string OwnerDisplayName, DateTime CreatedAt,
    decimal? Total, int OrderedQty, int ReceivedQty, int OpenQty);

public sealed record ReceiptLineDto(Guid OrderLineId, Guid ProductId, string Sku, string ProductName, string Unit, int Quantity);

public sealed record ReceiptDto(
    Guid Id, string Number, Guid PurchaseOrderId, string PurchaseOrderNumber, Guid WarehouseId, string ExternalReference,
    Guid CreatedBy, string CreatedByDisplayName, DateTime CreatedAt, IReadOnlyList<ReceiptLineDto> Lines, string PurchaseOrderStatus, string PurchaseOrderETag);

public sealed class PurchasingService(
    IKantarDb db,
    IDocumentLocks locks,
    StockPoster poster,
    IDocumentNumbers numbers,
    TimeProvider clock,
    AuditWriter audit,
    OutboxWriter outbox)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ queries

    public async Task<PurchaseOrderDto> GetAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
        var view = order is null ? DocumentView.None : AccessPolicy.ViewOf(user, order);
        if (order is null || view == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        return await ToDtoAsync(order, view, ct);
    }

    public async Task<Page<PurchaseOrderListItemDto>> ListAsync(
        CurrentUser user, Guid? warehouseId, PurchaseOrderStatus? status, bool mine, int? limit, string? cursor, CancellationToken ct)
    {
        var take = Guard.Limit(limit);
        var full = user.Is(Roles.PurchasingAgent) ? user.WarehousesWith(WarehouseLevel.View) : [];
        var fulfilment = user.Is(Roles.WarehouseOperator) ? user.WarehousesWith(WarehouseLevel.Operate) : [];
        if (full.Length == 0 && fulfilment.Length == 0)
        {
            throw Problems.Forbidden();
        }

        var query = db.PurchaseOrders.AsNoTracking().Where(o =>
            full.Contains(o.WarehouseId) || (fulfilment.Contains(o.WarehouseId) && o.Status != PurchaseOrderStatus.Draft && !(o.Status == PurchaseOrderStatus.Cancelled && o.ApprovedAt == null)));

        if (warehouseId is { } wid)
        {
            if (!full.Contains(wid) && !fulfilment.Contains(wid))
            {
                throw Problems.NotFound();
            }

            query = query.Where(o => o.WarehouseId == wid);
        }

        if (status is { } s)
        {
            query = query.Where(o => o.Status == s);
        }

        if (mine)
        {
            query = query.Where(o => o.OwnerUserId == user.Id);
        }

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var at, out var lastId))
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            query = query.Where(o => o.CreatedAt < at || (o.CreatedAt == at && o.Id.CompareTo(lastId) < 0));
        }

        var rows = await query.Include(o => o.Lines).OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var owners = await UserDisplayNames.LoadAsync(db, page.Select(o => o.OwnerUserId), ct);
        var items = page.Select(o => new PurchaseOrderListItemDto(
            o.Id, o.Number, o.Status.ToString(), o.SupplierName, o.WarehouseId, o.OwnerUserId, owners.Of(o.OwnerUserId), o.CreatedAt,
            full.Contains(o.WarehouseId) ? o.Total : null,
            o.Lines.Sum(l => l.OrderedQty), o.Lines.Sum(l => l.ReceivedQty), o.Lines.Sum(l => l.OpenQty))).ToArray();
        return new Page<PurchaseOrderListItemDto>(items, rows.Count > take ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id) : null);
    }

    public async Task<ReceiptDto> GetReceiptAsync(CurrentUser user, Guid orderId, Guid receiptId, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        var receipt = await db.GoodsReceipts.AsNoTracking().Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == receiptId && r.PurchaseOrderId == orderId, ct) ?? throw Problems.NotFound();
        return ToDto(receipt, order, await UserDisplayNames.OfAsync(db, receipt.CreatedBy, ct));
    }

    // ------------------------------------------------------------------ draft

    public static SavePurchaseOrderCommand Normalize(SavePurchaseOrderCommand command)
    {
        var v = new Validator();
        var note = v.Text("note", command.Note, 0, 500, multiline: true, required: false);
        v.ThrowIfInvalid();
        var lines = command.Lines ?? [];
        PurchaseOrder.ValidateDraftLines(lines.Select(l => (l.ProductId, l.Quantity, l.UnitCost)).ToArray());
        return command with { Note = note.Length == 0 ? null : note, Lines = lines };
    }

    public ICommandHandler<SavePurchaseOrderCommand, PurchaseOrderDto> Create => new DelegateHandler<SavePurchaseOrderCommand, PurchaseOrderDto>(
        (command, user, _) =>
        {
            if (!AccessPolicy.CanCreatePurchaseOrder(user, command.WarehouseId))
            {
                throw user.Is(Roles.PurchasingAgent) ? Problems.NotFound() : Problems.Forbidden();
            }

            return Task.CompletedTask;
        },
        async (command, user, ct) =>
        {
            var now = Now;
            var order = new PurchaseOrder
            {
                Number = await numbers.NextAsync(DocumentKind.PurchaseOrder, ct),
                WarehouseId = command.WarehouseId,
                OwnerUserId = user.Id,
                Note = command.Note,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await FillDraftAsync(order, command, now, ct);
            db.PurchaseOrders.Add(order);
            audit.Write(user, "purchasing.order_created", "PurchaseOrder", order.Id, order.WarehouseId);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, DocumentView.Full, ct);
        });

    public async Task<PurchaseOrderDto> UpdateDraftAsync(SavePurchaseOrderCommand command, CurrentUser user, byte[]? expected, CancellationToken ct)
    {
        var order = await locks.LockPurchaseOrderAsync(command.Id!.Value, ct);
        if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        if (!AccessPolicy.CanEditPurchaseOrder(user, order))
        {
            throw Problems.Forbidden();
        }

        Guard.Precondition(order.RowVersion, expected);
        if (command.WarehouseId != order.WarehouseId)
        {
            throw Problems.Conflict("immutable_field", "Siparişin deposu değiştirilemez.");
        }

        order.Note = command.Note;
        await FillDraftAsync(order, command, Now, ct);

        // The replacement lines carry keys generated here. Reached only through the collection of an
        // already tracked order, EF Core would take them for existing rows and send UPDATEs that match
        // nothing (surfacing as a false 412), so they are registered as new rows explicitly.
        db.PurchaseOrderLines.AddRange(order.Lines);
        audit.Write(user, "purchasing.order_updated", "PurchaseOrder", order.Id, order.WarehouseId);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(order, DocumentView.Full, ct);
    }

    private async Task FillDraftAsync(PurchaseOrder order, SavePurchaseOrderCommand command, DateTime now, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == command.SupplierId, ct)
            ?? throw Problems.Validation("supplierId", "unknown", "Bilinmeyen tedarikçi.");
        if (!supplier.IsActive || !await db.Warehouses.AnyAsync(w => w.Id == command.WarehouseId && w.IsActive, ct))
        {
            throw Problems.Conflict("master_data_inactive", "Tedarikçi veya depo pasif.");
        }

        var ids = command.Lines.Select(l => l.ProductId).ToArray();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var errors = new List<FieldError>();
        for (var i = 0; i < command.Lines.Count; i++)
        {
            if (!products.TryGetValue(command.Lines[i].ProductId, out var product))
            {
                throw Problems.Validation($"lines[{i}].productId", "unknown", "Bilinmeyen ürün.");
            }

            if (!product.IsActive)
            {
                errors.Add(new FieldError($"lines[{i}].productId", "master_data_inactive", $"{product.Sku} pasif."));
            }
        }

        if (errors.Count > 0)
        {
            throw Problems.Conflict("master_data_inactive", null, errors);
        }

        order.SnapshotSupplier(supplier);
        order.ReplaceLines(command.Lines.Select(l => (products[l.ProductId], l.Quantity, l.UnitCost)).ToArray(), now);
    }

    // ------------------------------------------------------------------ approve / cancel

    private async Task<PurchaseOrder> AuthorizeOnAsync(Guid id, CurrentUser user, Func<CurrentUser, PurchaseOrder, bool> allowed, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        return allowed(user, order) ? order : throw Problems.Forbidden();
    }

    public ICommandHandler<PurchaseOrderActionCommand, PurchaseOrderDto> Approve => new DelegateHandler<PurchaseOrderActionCommand, PurchaseOrderDto>(
        (command, user, ct) => AuthorizeOnAsync(command.Id, user, AccessPolicy.CanAttemptApproval, ct),
        async (command, user, ct) =>
        {
            var order = await locks.LockPurchaseOrderAsync(command.Id, ct) ?? throw Problems.NotFound();
            Guard.Precondition(order.RowVersion, ParseETag(command.IfMatch));

            var supplier = await db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == order.SupplierId, ct);
            var productIds = order.Lines.Select(l => l.ProductId).ToArray();
            var inactive = !supplier.IsActive
                || !await db.Warehouses.AnyAsync(w => w.Id == order.WarehouseId && w.IsActive, ct)
                || await db.Products.AnyAsync(p => productIds.Contains(p.Id) && !p.IsActive, ct);
            if (inactive)
            {
                throw Problems.Conflict("master_data_inactive", "Tedarikçi, depo veya ürünlerden biri pasif.");
            }

            order.Approve(supplier, user.Id, user.PoApprovalLimit, Now);
            audit.Write(user, "purchasing.order_approved", "PurchaseOrder", order.Id, order.WarehouseId);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, DocumentView.Full, ct);
        });

    public ICommandHandler<PurchaseOrderActionCommand, PurchaseOrderDto> Cancel => new DelegateHandler<PurchaseOrderActionCommand, PurchaseOrderDto>(
        (command, user, ct) => AuthorizeOnAsync(command.Id, user, AccessPolicy.CanCancelPurchaseOrder, ct),
        async (command, user, ct) =>
        {
            var order = await locks.LockPurchaseOrderAsync(command.Id, ct) ?? throw Problems.NotFound();
            Guard.Precondition(order.RowVersion, ParseETag(command.IfMatch));
            order.Cancel(command.Reason!, Now);
            audit.Write(user, "purchasing.order_cancelled", "PurchaseOrder", order.Id, order.WarehouseId, new { hadReceipts = order.HasReceipts });
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, DocumentView.Full, ct);
        });

    public static PurchaseOrderActionCommand NormalizeCancel(PurchaseOrderActionCommand command)
    {
        var v = new Validator();
        var reason = v.Text("reason", command.Reason, 3, 300, multiline: true);
        v.ThrowIfInvalid();
        return command with { Reason = reason };
    }

    // ------------------------------------------------------------------ receive

    public static ReceiveCommand Normalize(ReceiveCommand command)
    {
        var v = new Validator();
        var reference = v.Code("externalReference", command.ExternalReference, 3, 48);
        var lines = command.Lines ?? [];
        if (lines.Count is 0 or > PurchaseOrder.MaxLines)
        {
            v.Add("lines", "line_count", $"Satır sayısı 1 ile {PurchaseOrder.MaxLines} arasında olmalıdır.");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            v.Range($"lines[{i}].quantity", lines[i].Quantity, 1, PurchaseOrder.MaxLineQuantity);
        }

        v.ThrowIfInvalid();
        return command with { ExternalReference = reference, Lines = lines };
    }

    public ICommandHandler<ReceiveCommand, ReceiptDto> Receive => new DelegateHandler<ReceiveCommand, ReceiptDto>(
        async (command, user, ct) =>
        {
            // The receiving warehouse is the order's warehouse; the caller's scope is checked against it,
            // never against a warehouse id taken from the request.
            var order = await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == command.OrderId, ct);
            if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
            {
                throw Problems.NotFound();
            }

            if (!AccessPolicy.CanReceive(user, order.WarehouseId))
            {
                throw Problems.Forbidden();
            }
        },
        async (command, user, ct) =>
        {
            var now = Now;
            var order = await locks.LockPurchaseOrderAsync(command.OrderId, ct) ?? throw Problems.NotFound();
            if (command.IfMatch is not null)
            {
                Guard.Precondition(order.RowVersion, ParseETag(command.IfMatch));
            }

            if (await db.GoodsReceipts.AnyAsync(r => r.SupplierId == order.SupplierId && r.ExternalReference == command.ExternalReference, ct))
            {
                throw Problems.Conflict("duplicate_business_reference", "Bu tedarikçi irsaliye numarası daha önce kabul edildi; stok ikinci kez artırılmadı.");
            }

            var effects = order.Receive(command.Lines.Select(l => new LineQuantity(l.OrderLineId, l.Quantity)).ToArray(), now);

            var receipt = new GoodsReceipt
            {
                Number = await numbers.NextAsync(DocumentKind.GoodsReceipt, ct),
                PurchaseOrderId = order.Id,
                SupplierId = order.SupplierId,
                WarehouseId = order.WarehouseId,
                ExternalReference = command.ExternalReference,
                CreatedBy = user.Id,
                CreatedAt = now,
            };

            var postings = new List<StockPosting>();
            for (var i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                var line = new GoodsReceiptLine { ReceiptId = receipt.Id, PurchaseOrderLineId = effect.LineId, ProductId = effect.ProductId, Quantity = effect.Quantity };
                receipt.Lines.Add(line);
                postings.Add(new StockPosting(order.WarehouseId, effect.ProductId, effect.Quantity, 0, $"lines[{i}].quantity",
                    after => db.StockMovements.Add(InventoryService.Movement(order.WarehouseId, line.ProductId, MovementType.Receipt, line.Quantity, after, receipt.Id, line.Id, user.Id, now)),
                    EnsureRow: true));
            }

            db.GoodsReceipts.Add(receipt);
            await db.SaveChangesAsync(ct);
            await poster.ApplyAsync(postings, ct);

            audit.Write(user, "purchasing.goods_received", "GoodsReceipt", receipt.Id, order.WarehouseId, new { lines = receipt.Lines.Count, orderStatus = order.Status.ToString() });
            outbox.Enqueue(InventoryService.StockChangedEvent, new StockChangedPayload(order.WarehouseId, receipt.Lines.Select(l => l.ProductId).ToArray()));
            await db.SaveChangesAsync(ct);
            return ToDto(receipt, order, user.DisplayName);
        });

    // ------------------------------------------------------------------ mapping

    internal static byte[]? ParseETag(string? header)
    {
        if (header is null)
        {
            return null;
        }

        return ETags.TryParse(header, out var version) ? version : throw Problems.PreconditionFailed();
    }

    private async Task<PurchaseOrderDto> ToDtoAsync(PurchaseOrder order, DocumentView view, CancellationToken ct)
    {
        var full = view == DocumentView.Full;
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(r => r.PurchaseOrderId == order.Id)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Number)
            .Select(r => new ReceiptSummaryDto(r.Id, r.Number, r.ExternalReference, r.CreatedAt, r.Lines.Sum(l => l.Quantity)))
            .ToListAsync(ct);
        var owner = await UserDisplayNames.OfAsync(db, order.OwnerUserId, ct);

        return new PurchaseOrderDto(
            order.Id, order.Number, order.Status.ToString(), view.ToString(), order.SupplierId, order.SupplierCode, order.SupplierName,
            order.WarehouseId, order.OwnerUserId, owner, full ? order.Note : null, full ? order.Total : null,
            order.CreatedAt, order.ApprovedAt, order.ClosedAt, full ? order.CancelReason : null,
            order.Lines.OrderBy(l => l.LineNo).Select(l => new PurchaseLineDto(
                l.Id, l.LineNo, l.ProductId, l.Sku, l.ProductName, l.Unit, full ? l.UnitCost : null,
                l.OrderedQty, l.ReceivedQty, l.CancelledQty, l.OpenQty)).ToArray(),
            receipts,
            ETags.From(order.RowVersion));
    }

    private static ReceiptDto ToDto(GoodsReceipt receipt, PurchaseOrder order, string createdByDisplayName)
    {
        var lines = order.Lines.ToDictionary(l => l.Id);
        return new ReceiptDto(
            receipt.Id, receipt.Number, order.Id, order.Number, receipt.WarehouseId, receipt.ExternalReference, receipt.CreatedBy, createdByDisplayName, receipt.CreatedAt,
            receipt.Lines.OrderBy(l => lines[l.PurchaseOrderLineId].LineNo).Select(l => new ReceiptLineDto(
                l.PurchaseOrderLineId, l.ProductId, lines[l.PurchaseOrderLineId].Sku, lines[l.PurchaseOrderLineId].ProductName, lines[l.PurchaseOrderLineId].Unit, l.Quantity)).ToArray(),
            order.Status.ToString(),
            ETags.From(order.RowVersion));
    }
}
