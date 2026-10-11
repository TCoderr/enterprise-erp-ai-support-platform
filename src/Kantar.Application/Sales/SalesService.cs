using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.Inventory;
using Kantar.Application.Purchasing;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Inventory;
using Kantar.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Sales;

public sealed record SalesLineInput(Guid ProductId, int Quantity);

public sealed record SaveSalesOrderCommand(Guid? Id, Guid CustomerId, Guid WarehouseId, string? Note, IReadOnlyList<SalesLineInput> Lines, string? IfMatch);

public sealed record SalesOrderActionCommand(Guid Id, string? Reason, string? IfMatch);

public sealed record ShipCommand(Guid OrderId, string DispatchReference, IReadOnlyList<FulfilmentLineInput> Lines, string? IfMatch);

/// <summary>UnitPrice, LineTotal and Total are null for the fulfilment view.</summary>
public sealed record SalesLineDto(
    Guid Id, int LineNo, Guid ProductId, string Sku, string ProductName, string Unit, decimal? UnitPrice, decimal? LineTotal,
    int OrderedQty, int ShippedQty, int CancelledQty, int OpenQty);

public sealed record ShipmentSummaryDto(Guid Id, string Number, string DispatchReference, DateTime CreatedAt, int TotalQuantity);

public sealed record SalesOrderDto(
    Guid Id, string Number, string Status, string View, Guid CustomerId, string CustomerCode, string CustomerName,
    string? CustomerTaxNumber, string ShippingAddress, Guid WarehouseId, Guid OwnerUserId, string OwnerDisplayName, string? Note, decimal? Total,
    DateTime CreatedAt, DateTime? ConfirmedAt, DateTime? ClosedAt, string? CancelReason,
    IReadOnlyList<SalesLineDto> Lines, IReadOnlyList<ShipmentSummaryDto> Shipments, string ETag);

public sealed record SalesOrderListItemDto(
    Guid Id, string Number, string Status, string CustomerName, Guid WarehouseId, Guid OwnerUserId, string OwnerDisplayName, DateTime CreatedAt,
    decimal? Total, int OrderedQty, int ShippedQty, int OpenQty);

public sealed record ShipmentLineDto(Guid OrderLineId, Guid ProductId, string Sku, string ProductName, string Unit, int Quantity);

public sealed record ShipmentDto(
    Guid Id, string Number, Guid SalesOrderId, string SalesOrderNumber, Guid WarehouseId, string DispatchReference,
    Guid CreatedBy, string CreatedByDisplayName, DateTime CreatedAt, IReadOnlyList<ShipmentLineDto> Lines, string SalesOrderStatus, string SalesOrderETag);

public sealed record SalesOrderSummaryDto(SalesOrderDto Order, string WarehouseCode, string WarehouseName, DateTime GeneratedAt, string LegalNotice);

public sealed class SalesService(
    IKantarDb db,
    IDocumentLocks locks,
    StockPoster poster,
    IDocumentNumbers numbers,
    TimeProvider clock,
    AuditWriter audit,
    OutboxWriter outbox)
{
    public const string LegalNotice = "Bu belge bilgi amaçlı sipariş özetidir; fatura, e-fatura veya irsaliye değildir.";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ queries

    public async Task<SalesOrderDto> GetAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
        var view = order is null ? DocumentView.None : AccessPolicy.ViewOf(user, order);
        if (order is null || view == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        return await ToDtoAsync(order, view, ct);
    }

    public async Task<SalesOrderSummaryDto> SummaryAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var order = await GetAsync(user, id, ct);
        var warehouse = await db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == order.WarehouseId, ct);
        return new SalesOrderSummaryDto(order, warehouse.Code, warehouse.Name, Now, LegalNotice);
    }

    public async Task<Page<SalesOrderListItemDto>> ListAsync(
        CurrentUser user, Guid? warehouseId, SalesOrderStatus? status, bool mine, int? limit, string? cursor, CancellationToken ct)
    {
        var take = Guard.Limit(limit);
        var commercial = AccessPolicy.HasSalesRole(user) ? user.WarehousesWith(WarehouseLevel.View) : [];
        var fulfilment = user.Is(Roles.WarehouseOperator) ? user.WarehousesWith(WarehouseLevel.Operate) : [];
        if (commercial.Length == 0 && fulfilment.Length == 0)
        {
            throw Problems.Forbidden();
        }

        // Mirrors AccessPolicy.ViewOf as a query predicate: managers see every order of their warehouses,
        // agents only their own, operators only confirmed-or-later orders of warehouses they operate.
        var isManager = user.Is(Roles.SalesManager);
        var query = db.SalesOrders.AsNoTracking().Where(o =>
            (commercial.Contains(o.WarehouseId) && (isManager || o.OwnerUserId == user.Id))
            || (fulfilment.Contains(o.WarehouseId) && o.Status != SalesOrderStatus.Draft && !(o.Status == SalesOrderStatus.Cancelled && o.ConfirmedAt == null)));

        if (warehouseId is { } wid)
        {
            if (!commercial.Contains(wid) && !fulfilment.Contains(wid))
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
        var items = page.Select(o => new SalesOrderListItemDto(
            o.Id, o.Number, o.Status.ToString(), o.CustomerName, o.WarehouseId, o.OwnerUserId, owners.Of(o.OwnerUserId), o.CreatedAt,
            AccessPolicy.ViewOf(user, o) == DocumentView.Full ? o.Total : null,
            o.Lines.Sum(l => l.OrderedQty), o.Lines.Sum(l => l.ShippedQty), o.Lines.Sum(l => l.OpenQty))).ToArray();
        return new Page<SalesOrderListItemDto>(items, rows.Count > take ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id) : null);
    }

    public async Task<ShipmentDto> GetShipmentAsync(CurrentUser user, Guid orderId, Guid shipmentId, CancellationToken ct)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        var shipment = await db.Shipments.AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == shipmentId && s.SalesOrderId == orderId, ct) ?? throw Problems.NotFound();
        return ToDto(shipment, order, await UserDisplayNames.OfAsync(db, shipment.CreatedBy, ct));
    }

    // ------------------------------------------------------------------ draft

    public static SaveSalesOrderCommand Normalize(SaveSalesOrderCommand command)
    {
        var v = new Validator();
        var note = v.Text("note", command.Note, 0, 500, multiline: true, required: false);
        v.ThrowIfInvalid();
        var lines = command.Lines ?? [];
        SalesOrder.ValidateDraftLines(lines.Select(l => (l.ProductId, l.Quantity)).ToArray());
        return command with { Note = note.Length == 0 ? null : note, Lines = lines };
    }

    public ICommandHandler<SaveSalesOrderCommand, SalesOrderDto> Create => new DelegateHandler<SaveSalesOrderCommand, SalesOrderDto>(
        (command, user, _) =>
        {
            if (!AccessPolicy.CanCreateSalesOrder(user, command.WarehouseId))
            {
                throw AccessPolicy.HasSalesRole(user) ? Problems.NotFound() : Problems.Forbidden();
            }

            return Task.CompletedTask;
        },
        async (command, user, ct) =>
        {
            var now = Now;
            var order = new SalesOrder
            {
                Number = await numbers.NextAsync(DocumentKind.SalesOrder, ct),
                WarehouseId = command.WarehouseId,
                OwnerUserId = user.Id,
                Note = command.Note,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await FillDraftAsync(order, command, now, ct);
            db.SalesOrders.Add(order);
            audit.Write(user, "sales.order_created", "SalesOrder", order.Id, order.WarehouseId);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, DocumentView.Full, ct);
        });

    public async Task<SalesOrderDto> UpdateDraftAsync(SaveSalesOrderCommand command, CurrentUser user, byte[]? expected, CancellationToken ct)
    {
        var order = await locks.LockSalesOrderAsync(command.Id!.Value, ct);
        if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        if (!AccessPolicy.CanManageSalesOrder(user, order))
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
        db.SalesOrderLines.AddRange(order.Lines);
        audit.Write(user, "sales.order_updated", "SalesOrder", order.Id, order.WarehouseId);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(order, DocumentView.Full, ct);
    }

    private async Task FillDraftAsync(SalesOrder order, SaveSalesOrderCommand command, DateTime now, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == command.CustomerId, ct)
            ?? throw Problems.Validation("customerId", "unknown", "Bilinmeyen müşteri.");
        if (!customer.IsActive || !await db.Warehouses.AnyAsync(w => w.Id == command.WarehouseId && w.IsActive, ct))
        {
            throw Problems.Conflict("master_data_inactive", "Müşteri veya depo pasif.");
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

        order.SnapshotCustomer(customer);
        order.ReplaceLines(command.Lines.Select(l => (products[l.ProductId], l.Quantity)).ToArray(), now);
    }

    // ------------------------------------------------------------------ confirm / cancel

    private async Task AuthorizeManageAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var order = await db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
        {
            throw Problems.NotFound();
        }

        if (!AccessPolicy.CanManageSalesOrder(user, order))
        {
            throw Problems.Forbidden();
        }
    }

    /// <summary>Draft → Confirmed with an all-or-nothing reservation of every line.</summary>
    public ICommandHandler<SalesOrderActionCommand, SalesOrderDto> Confirm => new DelegateHandler<SalesOrderActionCommand, SalesOrderDto>(
        (command, user, ct) => AuthorizeManageAsync(command.Id, user, ct),
        async (command, user, ct) =>
        {
            var now = Now;
            var order = await locks.LockSalesOrderAsync(command.Id, ct) ?? throw Problems.NotFound();
            Guard.Precondition(order.RowVersion, PurchasingService.ParseETag(command.IfMatch));

            var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == order.CustomerId, ct);
            var productIds = order.Lines.Select(l => l.ProductId).ToArray();
            var inactive = !customer.IsActive
                || !await db.Warehouses.AnyAsync(w => w.Id == order.WarehouseId && w.IsActive, ct)
                || await db.Products.AnyAsync(p => productIds.Contains(p.Id) && !p.IsActive, ct);
            if (inactive)
            {
                throw Problems.Conflict("master_data_inactive", "Müşteri, depo veya ürünlerden biri pasif.");
            }

            var lineNo = order.Lines.ToDictionary(l => l.Id, l => l.LineNo);
            var effects = order.Confirm(customer, now);
            await db.SaveChangesAsync(ct);

            await poster.ApplyAsync(
                effects.Select(e => new StockPosting(order.WarehouseId, e.ProductId, 0, e.Quantity, $"lines[{lineNo[e.LineId] - 1}]",
                    after => db.ReservationEntries.Add(Reservation(order, e, e.Quantity, ReservationReason.Reserve, after, null, user.Id, now)))),
                ct);

            audit.Write(user, "sales.order_confirmed", "SalesOrder", order.Id, order.WarehouseId, new { lines = effects.Count });
            outbox.Enqueue(InventoryService.StockChangedEvent, new StockChangedPayload(order.WarehouseId, productIds));
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, DocumentView.Full, ct);
        });

    public static SalesOrderActionCommand NormalizeCancel(SalesOrderActionCommand command)
    {
        var v = new Validator();
        var reason = v.Text("reason", command.Reason, 3, 300, multiline: true);
        v.ThrowIfInvalid();
        return command with { Reason = reason };
    }

    /// <summary>Releases the reservation of whatever has not shipped. Shipped stock does not come back.</summary>
    public ICommandHandler<SalesOrderActionCommand, SalesOrderDto> Cancel => new DelegateHandler<SalesOrderActionCommand, SalesOrderDto>(
        (command, user, ct) => AuthorizeManageAsync(command.Id, user, ct),
        async (command, user, ct) =>
        {
            var now = Now;
            var order = await locks.LockSalesOrderAsync(command.Id, ct) ?? throw Problems.NotFound();
            Guard.Precondition(order.RowVersion, PurchasingService.ParseETag(command.IfMatch));

            var released = order.Cancel(command.Reason!, now);
            await db.SaveChangesAsync(ct);

            await poster.ApplyAsync(
                released.Select(e => new StockPosting(order.WarehouseId, e.ProductId, 0, -e.Quantity, "lines",
                    after => db.ReservationEntries.Add(Reservation(order, e, -e.Quantity, ReservationReason.ReleaseOnCancel, after, null, user.Id, now)))),
                ct);

            audit.Write(user, "sales.order_cancelled", "SalesOrder", order.Id, order.WarehouseId, new { releasedLines = released.Count, hadShipments = order.HasShipments });
            if (released.Count > 0)
            {
                outbox.Enqueue(InventoryService.StockChangedEvent, new StockChangedPayload(order.WarehouseId, released.Select(e => e.ProductId).ToArray()));
            }

            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, DocumentView.Full, ct);
        });

    // ------------------------------------------------------------------ ship

    public static ShipCommand Normalize(ShipCommand command)
    {
        var v = new Validator();
        var reference = v.Code("dispatchReference", command.DispatchReference, 3, 48);
        var lines = command.Lines ?? [];
        if (lines.Count is 0 or > SalesOrder.MaxLines)
        {
            v.Add("lines", "line_count", $"Satır sayısı 1 ile {SalesOrder.MaxLines} arasında olmalıdır.");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            v.Range($"lines[{i}].quantity", lines[i].Quantity, 1, SalesOrder.MaxLineQuantity);
        }

        v.ThrowIfInvalid();
        return command with { DispatchReference = reference, Lines = lines };
    }

    public ICommandHandler<ShipCommand, ShipmentDto> Ship => new DelegateHandler<ShipCommand, ShipmentDto>(
        async (command, user, ct) =>
        {
            var order = await db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == command.OrderId, ct);
            if (order is null || AccessPolicy.ViewOf(user, order) == DocumentView.None)
            {
                throw Problems.NotFound();
            }

            if (!AccessPolicy.CanShip(user, order.WarehouseId))
            {
                throw Problems.Forbidden();
            }
        },
        async (command, user, ct) =>
        {
            var now = Now;
            var order = await locks.LockSalesOrderAsync(command.OrderId, ct) ?? throw Problems.NotFound();
            if (command.IfMatch is not null)
            {
                Guard.Precondition(order.RowVersion, PurchasingService.ParseETag(command.IfMatch));
            }

            if (await db.Shipments.AnyAsync(s => s.SalesOrderId == order.Id && s.DispatchReference == command.DispatchReference, ct))
            {
                throw Problems.Conflict("duplicate_business_reference", "Bu sevk referansı bu sipariş için daha önce kullanıldı; stok ikinci kez düşülmedi.");
            }

            var effects = order.Ship(command.Lines.Select(l => new LineQuantity(l.OrderLineId, l.Quantity)).ToArray(), now);

            var shipment = new Shipment
            {
                Number = await numbers.NextAsync(DocumentKind.Shipment, ct),
                SalesOrderId = order.Id,
                WarehouseId = order.WarehouseId,
                DispatchReference = command.DispatchReference,
                CreatedBy = user.Id,
                CreatedAt = now,
            };

            var postings = new List<StockPosting>();
            for (var i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                var line = new ShipmentLine { ShipmentId = shipment.Id, SalesOrderLineId = effect.LineId, ProductId = effect.ProductId, Quantity = effect.Quantity };
                shipment.Lines.Add(line);

                // A shipment consumes its own reservation: OnHand and Reserved fall together, so the
                // quantity available to other orders does not change.
                postings.Add(new StockPosting(order.WarehouseId, effect.ProductId, -effect.Quantity, -effect.Quantity, $"lines[{i}].quantity", after =>
                {
                    db.StockMovements.Add(InventoryService.Movement(order.WarehouseId, line.ProductId, MovementType.Shipment, -line.Quantity, after, shipment.Id, line.Id, user.Id, now));
                    db.ReservationEntries.Add(Reservation(order, effect, -line.Quantity, ReservationReason.ReleaseOnShipment, after, line.Id, user.Id, now));
                }));
            }

            db.Shipments.Add(shipment);
            await db.SaveChangesAsync(ct);
            await poster.ApplyAsync(postings, ct);

            audit.Write(user, "sales.shipment_posted", "Shipment", shipment.Id, order.WarehouseId, new { lines = shipment.Lines.Count, orderStatus = order.Status.ToString() });
            outbox.Enqueue(InventoryService.StockChangedEvent, new StockChangedPayload(order.WarehouseId, shipment.Lines.Select(l => l.ProductId).ToArray()));
            await db.SaveChangesAsync(ct);
            return ToDto(shipment, order, user.DisplayName);
        });

    // ------------------------------------------------------------------ mapping

    private static ReservationEntry Reservation(
        SalesOrder order, LineEffect effect, int delta, ReservationReason reason, StockLevels after, Guid? shipmentLineId, Guid actor, DateTime now) => new()
        {
            WarehouseId = order.WarehouseId,
            ProductId = effect.ProductId,
            QuantityDelta = delta,
            ReservedAfter = after.Reserved,
            Reason = reason,
            SalesOrderId = order.Id,
            SalesOrderLineId = effect.LineId,
            ShipmentLineId = shipmentLineId,
            ActorUserId = actor,
            OccurredAt = now,
        };

    private async Task<SalesOrderDto> ToDtoAsync(SalesOrder order, DocumentView view, CancellationToken ct)
    {
        var full = view == DocumentView.Full;
        var shipments = await db.Shipments.AsNoTracking().Where(s => s.SalesOrderId == order.Id)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Number)
            .Select(s => new ShipmentSummaryDto(s.Id, s.Number, s.DispatchReference, s.CreatedAt, s.Lines.Sum(l => l.Quantity)))
            .ToListAsync(ct);
        var owner = await UserDisplayNames.OfAsync(db, order.OwnerUserId, ct);

        return new SalesOrderDto(
            order.Id, order.Number, order.Status.ToString(), view.ToString(), order.CustomerId, order.CustomerCode, order.CustomerName,
            full ? order.CustomerTaxNumber : null, order.ShippingAddress, order.WarehouseId, order.OwnerUserId, owner,
            full ? order.Note : null, full ? order.Total : null,
            order.CreatedAt, order.ConfirmedAt, order.ClosedAt, full ? order.CancelReason : null,
            order.Lines.OrderBy(l => l.LineNo).Select(l => new SalesLineDto(
                l.Id, l.LineNo, l.ProductId, l.Sku, l.ProductName, l.Unit, full ? l.UnitPrice : null, full ? l.UnitPrice * l.OrderedQty : null,
                l.OrderedQty, l.ShippedQty, l.CancelledQty, l.OpenQty)).ToArray(),
            shipments,
            ETags.From(order.RowVersion));
    }

    private static ShipmentDto ToDto(Shipment shipment, SalesOrder order, string createdByDisplayName)
    {
        var lines = order.Lines.ToDictionary(l => l.Id);
        return new ShipmentDto(
            shipment.Id, shipment.Number, order.Id, order.Number, shipment.WarehouseId, shipment.DispatchReference, shipment.CreatedBy, createdByDisplayName, shipment.CreatedAt,
            shipment.Lines.OrderBy(l => lines[l.SalesOrderLineId].LineNo).Select(l => new ShipmentLineDto(
                l.SalesOrderLineId, l.ProductId, lines[l.SalesOrderLineId].Sku, lines[l.SalesOrderLineId].ProductName, lines[l.SalesOrderLineId].Unit, l.Quantity)).ToArray(),
            order.Status.ToString(),
            ETags.From(order.RowVersion));
    }
}
