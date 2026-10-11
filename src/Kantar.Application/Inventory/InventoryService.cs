using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Inventory;

public sealed record BalanceDto(Guid ProductId, string Sku, string Name, string Unit, int OnHand, int Reserved, int Available, int ReorderLevel, bool IsLow);

public sealed record MovementDto(
    long Id, DateTime OccurredAt, Guid ProductId, string Sku, string Name, string Type, int QuantityDelta, int OnHandAfter,
    string ReferenceType, Guid ReferenceId, string? ReferenceNumber);

public sealed record ReservationDto(
    long Id, DateTime OccurredAt, Guid ProductId, string Sku, string Name, string Reason, int QuantityDelta, int ReservedAfter,
    Guid SalesOrderId, string? SalesOrderNumber);

public sealed record TransferLineInput(Guid ProductId, int Quantity);

public sealed record CreateTransferCommand(string Reference, Guid FromWarehouseId, Guid ToWarehouseId, string? Note, IReadOnlyList<TransferLineInput> Lines);

public sealed record TransferLineDto(int LineNo, Guid ProductId, string Sku, string Name, string Unit, int Quantity);

public sealed record TransferDto(
    Guid Id, string Number, string Reference, Guid FromWarehouseId, Guid ToWarehouseId, string? Note,
    Guid CreatedBy, string CreatedByDisplayName, DateTime CreatedAt, IReadOnlyList<TransferLineDto> Lines);

public sealed record AdjustmentLineInput(Guid ProductId, int QuantityDelta);

public sealed record CreateAdjustmentCommand(string Reference, Guid WarehouseId, string ReasonCode, string Note, IReadOnlyList<AdjustmentLineInput> Lines);

public sealed record AdjustmentLineDto(int LineNo, Guid ProductId, string Sku, string Name, string Unit, int QuantityDelta);

public sealed record AdjustmentDto(
    Guid Id, string Number, string Reference, Guid WarehouseId, string ReasonCode, string Note,
    Guid CreatedBy, string CreatedByDisplayName, DateTime CreatedAt, IReadOnlyList<AdjustmentLineDto> Lines);

public sealed class InventoryService(
    IKantarDb db,
    StockPoster poster,
    IDocumentNumbers numbers,
    TimeProvider clock,
    AuditWriter audit,
    OutboxWriter outbox)
{
    public const string StockChangedEvent = "inventory.stock_changed";

    /// <summary>A warehouse the caller cannot read is reported as if it did not exist.</summary>
    private static void RequireRead(CurrentUser user, Guid warehouseId)
    {
        if (!AccessPolicy.CanReadStock(user, warehouseId))
        {
            throw Problems.NotFound();
        }
    }

    // ------------------------------------------------------------------ queries

    public async Task<Page<BalanceDto>> BalancesAsync(CurrentUser user, Guid warehouseId, string? search, bool lowStockOnly, int? limit, string? cursor, CancellationToken ct)
    {
        RequireRead(user, warehouseId);
        var take = Guard.Limit(limit);

        var query =
            from b in db.StockBalances.AsNoTracking()
            join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
            where b.WarehouseId == warehouseId
            select new { b, p };

        if (Guard.Search(search) is { } term)
        {
            var code = term.ToUpperInvariant();
            query = query.Where(x => x.p.Sku.StartsWith(code) || x.p.Name.Contains(term));
        }

        if (lowStockOnly)
        {
            query = query.Where(x => x.p.IsActive && x.b.OnHand - x.b.Reserved <= x.p.ReorderLevel);
        }

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out long offsetKey) || offsetKey < 0)
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            // Balances are few per warehouse and sorted by a unique SKU; the cursor carries the
            // number of rows already returned for this exact filter.
            query = query.OrderBy(x => x.p.Sku).Skip((int)Math.Min(offsetKey, int.MaxValue));
            var rowsAfter = await query.Take(take + 1).ToListAsync(ct);
            return ToPage(rowsAfter.Select(x => ToDto(x.b, x.p)).ToList(), take, offsetKey);
        }

        var rows = await query.OrderBy(x => x.p.Sku).Take(take + 1).ToListAsync(ct);
        return ToPage(rows.Select(x => ToDto(x.b, x.p)).ToList(), take, 0);

        static Page<BalanceDto> ToPage(List<BalanceDto> items, int take, long skipped) =>
            new(items.Take(take).ToArray(), items.Count > take ? Cursor.Encode(skipped + take) : null);

        static BalanceDto ToDto(StockBalance b, Domain.MasterData.Product p) =>
            new(p.Id, p.Sku, p.Name, p.Unit, b.OnHand, b.Reserved, b.Available, p.ReorderLevel, p.IsActive && b.Available <= p.ReorderLevel);
    }

    public async Task<Page<MovementDto>> MovementsAsync(
        CurrentUser user, Guid warehouseId, Guid? productId, MovementType? type, DateTime? from, DateTime? to, int? limit, string? cursor, CancellationToken ct)
    {
        RequireRead(user, warehouseId);
        var take = Guard.Limit(limit);

        var query = db.StockMovements.AsNoTracking().Where(m => m.WarehouseId == warehouseId);
        if (productId is { } pid)
        {
            query = query.Where(m => m.ProductId == pid);
        }

        if (type is { } t)
        {
            query = query.Where(m => m.Type == t);
        }

        if (from is { } f)
        {
            query = query.Where(m => m.OccurredAt >= f);
        }

        if (to is { } until)
        {
            query = query.Where(m => m.OccurredAt < until);
        }

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out long before))
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            query = query.Where(m => m.Id < before);
        }

        var rows = await (
            from m in query
            join p in db.Products.AsNoTracking() on m.ProductId equals p.Id
            orderby m.Id descending
            select new { m, p.Sku, p.Name }).Take(take + 1).ToListAsync(ct);

        var page = rows.Take(take).ToList();
        var numbers = await ReferenceNumbersAsync(page.Select(r => (r.m.ReferenceType, r.m.ReferenceId)), ct);
        var items = page.Select(r => new MovementDto(
            r.m.Id, r.m.OccurredAt, r.m.ProductId, r.Sku, r.Name, r.m.Type.ToString(), r.m.QuantityDelta, r.m.OnHandAfter,
            r.m.ReferenceType.ToString(), r.m.ReferenceId, numbers.GetValueOrDefault(r.m.ReferenceId))).ToArray();
        return new Page<MovementDto>(items, rows.Count > take ? Cursor.Encode(page[^1].m.Id) : null);
    }

    public async Task<Page<ReservationDto>> ReservationsAsync(CurrentUser user, Guid warehouseId, Guid? productId, int? limit, string? cursor, CancellationToken ct)
    {
        RequireRead(user, warehouseId);
        var take = Guard.Limit(limit);

        var query = db.ReservationEntries.AsNoTracking().Where(r => r.WarehouseId == warehouseId);
        if (productId is { } pid)
        {
            query = query.Where(r => r.ProductId == pid);
        }

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out long before))
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            query = query.Where(r => r.Id < before);
        }

        var rows = await (
            from r in query
            join p in db.Products.AsNoTracking() on r.ProductId equals p.Id
            join o in db.SalesOrders.AsNoTracking() on r.SalesOrderId equals o.Id
            orderby r.Id descending
            select new { r, p.Sku, p.Name, o.Number }).Take(take + 1).ToListAsync(ct);

        var items = rows.Take(take).Select(x => new ReservationDto(
            x.r.Id, x.r.OccurredAt, x.r.ProductId, x.Sku, x.Name, x.r.Reason.ToString(), x.r.QuantityDelta, x.r.ReservedAfter,
            x.r.SalesOrderId, x.Number)).ToArray();
        return new Page<ReservationDto>(items, rows.Count > take ? Cursor.Encode(items[^1].Id) : null);
    }

    /// <summary>Display numbers of the documents behind a page of movements, one query per document type.</summary>
    private async Task<Dictionary<Guid, string>> ReferenceNumbersAsync(IEnumerable<(ReferenceType Type, Guid Id)> references, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var group in references.Distinct().GroupBy(r => r.Type))
        {
            var ids = group.Select(g => g.Id).ToArray();
            var found = group.Key switch
            {
                ReferenceType.GoodsReceipt => await db.GoodsReceipts.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Number }).ToListAsync(ct),
                ReferenceType.Shipment => await db.Shipments.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Number }).ToListAsync(ct),
                ReferenceType.StockTransfer => await db.StockTransfers.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Number }).ToListAsync(ct),
                _ => await db.StockAdjustments.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Number }).ToListAsync(ct),
            };
            foreach (var row in found)
            {
                result[row.Id] = row.Number;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ transfer

    public static CreateTransferCommand Normalize(CreateTransferCommand command)
    {
        var v = new Validator();
        var reference = v.Code("reference", command.Reference, 3, 48);
        var note = v.Text("note", command.Note, 0, 500, multiline: true, required: false);
        if (command.FromWarehouseId == command.ToWarehouseId)
        {
            v.Add("toWarehouseId", "same_warehouse", "Kaynak ve hedef depo aynı olamaz.");
        }

        var lines = command.Lines ?? [];
        if (lines.Count is 0 or > StockTransfer.MaxLines)
        {
            v.Add("lines", "line_count", $"Satır sayısı 1 ile {StockTransfer.MaxLines} arasında olmalıdır.");
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            v.Range($"lines[{i}].quantity", lines[i].Quantity, 1, 100_000);
            if (!seen.Add(lines[i].ProductId))
            {
                v.Add($"lines[{i}].productId", "duplicate_product", "Aynı ürün bir belgede yalnız bir satırda yer alabilir.");
            }
        }

        v.ThrowIfInvalid();
        return command with { Reference = reference, Note = note.Length == 0 ? null : note, Lines = lines };
    }

    public ICommandHandler<CreateTransferCommand, TransferDto> CreateTransfer => new DelegateHandler<CreateTransferCommand, TransferDto>(
        (command, user, _) =>
        {
            if (!AccessPolicy.CanTransfer(user, command.FromWarehouseId, command.ToWarehouseId))
            {
                // Unknown to the caller on either side → not found; visible but not operable → forbidden.
                var visible = user.HasLevel(command.FromWarehouseId, WarehouseLevel.View) && user.HasLevel(command.ToWarehouseId, WarehouseLevel.View);
                throw visible ? Problems.Forbidden() : Problems.NotFound();
            }

            return Task.CompletedTask;
        },
        CreateTransferAsync);

    private async Task<TransferDto> CreateTransferAsync(CreateTransferCommand command, CurrentUser user, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.Id == command.FromWarehouseId || w.Id == command.ToWarehouseId).ToListAsync(ct);
        if (warehouses.Count != 2 || warehouses.Any(w => !w.IsActive))
        {
            throw Problems.Conflict("master_data_inactive", "Depo pasif.");
        }

        var products = await LoadProductsAsync(command.Lines.Select(l => l.ProductId), ct);
        if (await db.StockTransfers.AnyAsync(t => t.Reference == command.Reference, ct))
        {
            throw Problems.Conflict("duplicate_business_reference", "Bu transfer referansı daha önce kullanıldı.");
        }

        var transfer = new StockTransfer
        {
            Number = await numbers.NextAsync(DocumentKind.StockTransfer, ct),
            Reference = command.Reference,
            FromWarehouseId = command.FromWarehouseId,
            ToWarehouseId = command.ToWarehouseId,
            Note = command.Note,
            CreatedBy = user.Id,
            CreatedAt = now,
        };

        var postings = new List<StockPosting>();
        for (var i = 0; i < command.Lines.Count; i++)
        {
            var input = command.Lines[i];
            var line = new StockTransferLine { TransferId = transfer.Id, LineNo = i + 1, ProductId = input.ProductId, Quantity = input.Quantity };
            transfer.Lines.Add(line);
            var field = $"lines[{i}].quantity";
            postings.Add(new StockPosting(transfer.FromWarehouseId, input.ProductId, -input.Quantity, 0, field,
                after => db.StockMovements.Add(Movement(transfer.FromWarehouseId, line.ProductId, MovementType.TransferOut, -line.Quantity, after, transfer.Id, line.Id, user.Id, now))));
            postings.Add(new StockPosting(transfer.ToWarehouseId, input.ProductId, input.Quantity, 0, field,
                after => db.StockMovements.Add(Movement(transfer.ToWarehouseId, line.ProductId, MovementType.TransferIn, line.Quantity, after, transfer.Id, line.Id, user.Id, now)),
                EnsureRow: true));
        }

        db.StockTransfers.Add(transfer);
        await db.SaveChangesAsync(ct);
        await poster.ApplyAsync(postings, ct);

        audit.Write(user, "inventory.transfer_posted", "StockTransfer", transfer.Id, transfer.FromWarehouseId, new { lines = transfer.Lines.Count });
        outbox.Enqueue(StockChangedEvent, new StockChangedPayload(transfer.FromWarehouseId, transfer.Lines.Select(l => l.ProductId).ToArray()));
        await db.SaveChangesAsync(ct);
        return ToDto(transfer, products, user.DisplayName);
    }

    public async Task<TransferDto> GetTransferAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var transfer = await db.StockTransfers.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null || !(AccessPolicy.CanReadStock(user, transfer.FromWarehouseId) || AccessPolicy.CanReadStock(user, transfer.ToWarehouseId)))
        {
            throw Problems.NotFound();
        }

        return ToDto(transfer, await LoadProductsAsync(transfer.Lines.Select(l => l.ProductId), ct), await UserDisplayNames.OfAsync(db, transfer.CreatedBy, ct));
    }

    public async Task<Page<TransferDto>> ListTransfersAsync(CurrentUser user, Guid warehouseId, int? limit, string? cursor, CancellationToken ct)
    {
        RequireRead(user, warehouseId);
        var take = Guard.Limit(limit);
        var query = db.StockTransfers.AsNoTracking().Include(t => t.Lines)
            .Where(t => t.FromWarehouseId == warehouseId || t.ToWarehouseId == warehouseId);
        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var at, out var lastId))
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            query = query.Where(t => t.CreatedAt < at || (t.CreatedAt == at && t.Id.CompareTo(lastId) < 0));
        }

        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var products = await LoadProductsAsync(page.SelectMany(t => t.Lines).Select(l => l.ProductId), ct);
        var creators = await UserDisplayNames.LoadAsync(db, page.Select(t => t.CreatedBy), ct);
        return new Page<TransferDto>(
            page.Select(t => ToDto(t, products, creators.Of(t.CreatedBy))).ToArray(),
            rows.Count > take ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id) : null);
    }

    // ------------------------------------------------------------------ adjustment

    public static CreateAdjustmentCommand Normalize(CreateAdjustmentCommand command)
    {
        var v = new Validator();
        var reference = v.Code("reference", command.Reference, 3, 48);
        var note = v.Text("note", command.Note, 10, 500, multiline: true);
        if (!Enum.TryParse<AdjustmentReason>(command.ReasonCode, out var reason) || !Enum.IsDefined(reason) || char.IsAsciiDigit((command.ReasonCode ?? "0")[0]))
        {
            v.Add("reasonCode", "invalid", "Gerekçe kodu: " + string.Join(", ", Enum.GetNames<AdjustmentReason>()));
        }

        var lines = command.Lines ?? [];
        if (lines.Count is 0 or > StockAdjustment.MaxLines)
        {
            v.Add("lines", "line_count", $"Satır sayısı 1 ile {StockAdjustment.MaxLines} arasında olmalıdır.");
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].QuantityDelta == 0 || Math.Abs((long)lines[i].QuantityDelta) > 100_000)
            {
                v.Add($"lines[{i}].quantityDelta", "out_of_range", "Miktar 0 olamaz ve mutlak değeri en fazla 100000 olabilir.");
            }

            if (!seen.Add(lines[i].ProductId))
            {
                v.Add($"lines[{i}].productId", "duplicate_product", "Aynı ürün bir belgede yalnız bir satırda yer alabilir.");
            }
        }

        v.ThrowIfInvalid();
        return command with { Reference = reference, Note = note, Lines = lines };
    }

    public ICommandHandler<CreateAdjustmentCommand, AdjustmentDto> CreateAdjustment => new DelegateHandler<CreateAdjustmentCommand, AdjustmentDto>(
        (command, user, _) =>
        {
            if (!AccessPolicy.CanAdjust(user, command.WarehouseId))
            {
                throw user.HasLevel(command.WarehouseId, WarehouseLevel.View) ? Problems.Forbidden() : Problems.NotFound();
            }

            return Task.CompletedTask;
        },
        CreateAdjustmentAsync);

    private async Task<AdjustmentDto> CreateAdjustmentAsync(CreateAdjustmentCommand command, CurrentUser user, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.Warehouses.AnyAsync(w => w.Id == command.WarehouseId && w.IsActive, ct))
        {
            throw Problems.Conflict("master_data_inactive", "Depo pasif.");
        }

        var products = await LoadProductsAsync(command.Lines.Select(l => l.ProductId), ct);
        if (await db.StockAdjustments.AnyAsync(a => a.Reference == command.Reference, ct))
        {
            throw Problems.Conflict("duplicate_business_reference", "Bu düzeltme referansı daha önce kullanıldı.");
        }

        var adjustment = new StockAdjustment
        {
            Number = await numbers.NextAsync(DocumentKind.StockAdjustment, ct),
            Reference = command.Reference,
            WarehouseId = command.WarehouseId,
            ReasonCode = Enum.Parse<AdjustmentReason>(command.ReasonCode),
            Note = command.Note,
            CreatedBy = user.Id,
            CreatedAt = now,
        };

        var postings = new List<StockPosting>();
        for (var i = 0; i < command.Lines.Count; i++)
        {
            var input = command.Lines[i];
            var line = new StockAdjustmentLine { AdjustmentId = adjustment.Id, LineNo = i + 1, ProductId = input.ProductId, QuantityDelta = input.QuantityDelta };
            adjustment.Lines.Add(line);
            postings.Add(new StockPosting(adjustment.WarehouseId, input.ProductId, input.QuantityDelta, 0, $"lines[{i}].quantityDelta",
                after => db.StockMovements.Add(Movement(adjustment.WarehouseId, line.ProductId, MovementType.Adjustment, line.QuantityDelta, after, adjustment.Id, line.Id, user.Id, now)),
                EnsureRow: input.QuantityDelta > 0));
        }

        db.StockAdjustments.Add(adjustment);
        await db.SaveChangesAsync(ct);
        await poster.ApplyAsync(postings, ct);

        audit.Write(user, "inventory.adjustment_posted", "StockAdjustment", adjustment.Id, adjustment.WarehouseId,
            new { reason = adjustment.ReasonCode.ToString(), lines = adjustment.Lines.Count });
        outbox.Enqueue(StockChangedEvent, new StockChangedPayload(adjustment.WarehouseId, adjustment.Lines.Select(l => l.ProductId).ToArray()));
        await db.SaveChangesAsync(ct);
        return ToDto(adjustment, products, user.DisplayName);
    }

    public async Task<AdjustmentDto> GetAdjustmentAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var adjustment = await db.StockAdjustments.AsNoTracking().Include(a => a.Lines).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (adjustment is null || !AccessPolicy.CanReadStock(user, adjustment.WarehouseId))
        {
            throw Problems.NotFound();
        }

        return ToDto(adjustment, await LoadProductsAsync(adjustment.Lines.Select(l => l.ProductId), ct), await UserDisplayNames.OfAsync(db, adjustment.CreatedBy, ct));
    }

    public async Task<Page<AdjustmentDto>> ListAdjustmentsAsync(CurrentUser user, Guid warehouseId, int? limit, string? cursor, CancellationToken ct)
    {
        RequireRead(user, warehouseId);
        var take = Guard.Limit(limit);
        var query = db.StockAdjustments.AsNoTracking().Include(a => a.Lines).Where(a => a.WarehouseId == warehouseId);
        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var at, out var lastId))
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            query = query.Where(a => a.CreatedAt < at || (a.CreatedAt == at && a.Id.CompareTo(lastId) < 0));
        }

        var rows = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var products = await LoadProductsAsync(page.SelectMany(a => a.Lines).Select(l => l.ProductId), ct);
        var creators = await UserDisplayNames.LoadAsync(db, page.Select(a => a.CreatedBy), ct);
        return new Page<AdjustmentDto>(
            page.Select(a => ToDto(a, products, creators.Of(a.CreatedBy))).ToArray(),
            rows.Count > take ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id) : null);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Dictionary<Guid, Domain.MasterData.Product>> LoadProductsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(p => distinct.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != distinct.Length)
        {
            throw Problems.Validation("lines", "unknown_product", "Bilinmeyen ürün.");
        }

        return products;
    }

    internal static StockMovement Movement(
        Guid warehouseId, Guid productId, MovementType type, int delta, StockLevels after, Guid referenceId, Guid referenceLineId, Guid actor, DateTime now) => new()
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Type = type,
            QuantityDelta = delta,
            OnHandAfter = after.OnHand,
            ReferenceType = type switch
            {
                MovementType.Receipt => ReferenceType.GoodsReceipt,
                MovementType.Shipment => ReferenceType.Shipment,
                MovementType.TransferOut or MovementType.TransferIn => ReferenceType.StockTransfer,
                _ => ReferenceType.StockAdjustment,
            },
            ReferenceId = referenceId,
            ReferenceLineId = referenceLineId,
            ActorUserId = actor,
            OccurredAt = now,
        };

    private static TransferDto ToDto(StockTransfer t, Dictionary<Guid, Domain.MasterData.Product> products, string createdByDisplayName) => new(
        t.Id, t.Number, t.Reference, t.FromWarehouseId, t.ToWarehouseId, t.Note, t.CreatedBy, createdByDisplayName, t.CreatedAt,
        t.Lines.OrderBy(l => l.LineNo).Select(l => new TransferLineDto(l.LineNo, l.ProductId, products[l.ProductId].Sku, products[l.ProductId].Name, products[l.ProductId].Unit, l.Quantity)).ToArray());

    private static AdjustmentDto ToDto(StockAdjustment a, Dictionary<Guid, Domain.MasterData.Product> products, string createdByDisplayName) => new(
        a.Id, a.Number, a.Reference, a.WarehouseId, a.ReasonCode.ToString(), a.Note, a.CreatedBy, createdByDisplayName, a.CreatedAt,
        a.Lines.OrderBy(l => l.LineNo).Select(l => new AdjustmentLineDto(l.LineNo, l.ProductId, products[l.ProductId].Sku, products[l.ProductId].Name, products[l.ProductId].Unit, l.QuantityDelta)).ToArray());
}

public sealed record StockChangedPayload(Guid WarehouseId, IReadOnlyList<Guid> ProductIds);
