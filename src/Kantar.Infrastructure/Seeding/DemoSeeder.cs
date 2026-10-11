using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.Operations;
using Kantar.Application.Purchasing;
using Kantar.Application.Sales;
using Kantar.Application.Support;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kantar.Infrastructure.Seeding;

/// <summary>
/// Idempotent demo data, written with the restricted application login. Master data and users are
/// added by code/user name and left alone when present. Stock is never written to balances directly:
/// opening quantities arrive through an approved purchase order and a goods receipt with a fixed
/// supplier reference, so a second run finds the reference and adds nothing. The run ends with the
/// read-only reconciliation and fails if journals and balances disagree.
/// </summary>
public sealed partial class DemoSeeder(
    KantarDbContext db,
    IUnitOfWork unitOfWork,
    IPasswordHasher hasher,
    IConfiguration configuration,
    PurchasingService purchasing,
    SalesService sales,
    KnowledgeService knowledge,
    TicketService tickets,
    OperationsService operations,
    TimeProvider clock,
    ILogger<DemoSeeder> logger)
{
    /// <summary>Owner of the opening documents. Inactive: it can never sign in.</summary>
    public const string OpeningActor = "sistem.acilis";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var warehouses = await SeedWarehousesAsync(ct);
        var products = await SeedProductsAsync(ct);
        var customers = await SeedCustomersAsync(ct);
        var suppliers = await SeedSuppliersAsync(ct);
        var users = await SeedUsersAsync(warehouses, ct);

        var opening = await ActorAsync(OpeningActor, ct);
        foreach (var (code, quantities) in SeedData.OpeningStock)
        {
            await SeedOpeningStockAsync(opening, warehouses[code], suppliers["TED-ANADOLU"], products, quantities, ct);
        }

        await SeedKnowledgeAsync(await ActorAsync("destek.mudur", ct), warehouses, ct);
        await SeedSalesAsync(await ActorAsync("satis1", ct), await ActorAsync("depo1", ct), warehouses["IST-01"], customers, products, ct);
        await SeedTicketAsync(await ActorAsync("satis1", ct), ct);

        var run = await operations.RunReconciliationAsync(null, ct);
        LogDone(logger, users, run.BalancesChecked, run.FindingCount);
        return run.FindingCount == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ master data

    private async Task<Dictionary<string, Warehouse>> SeedWarehousesAsync(CancellationToken ct)
    {
        foreach (var (code, name) in SeedData.Warehouses)
        {
            if (!await db.Warehouses.AnyAsync(w => w.Code == code, ct))
            {
                db.Warehouses.Add(new Warehouse { Code = code, Name = name, CreatedAt = Now });
            }
        }

        await db.SaveChangesAsync(ct);
        return await db.Warehouses.AsNoTracking().ToDictionaryAsync(w => w.Code, ct);
    }

    private async Task<Dictionary<string, Product>> SeedProductsAsync(CancellationToken ct)
    {
        foreach (var (sku, name, unit, price, reorder) in SeedData.Products)
        {
            if (!await db.Products.AnyAsync(p => p.Sku == sku, ct))
            {
                db.Products.Add(new Product { Sku = sku, Name = name, Unit = unit, ListPrice = price, ReorderLevel = reorder, CreatedAt = Now });
            }
        }

        await db.SaveChangesAsync(ct);
        return await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Sku, ct);
    }

    private async Task<Dictionary<string, Customer>> SeedCustomersAsync(CancellationToken ct)
    {
        foreach (var (code, name, tax, address) in SeedData.Customers)
        {
            if (!await db.Customers.AnyAsync(c => c.Code == code, ct))
            {
                db.Customers.Add(new Customer { Code = code, Name = name, TaxNumber = tax, Address = address, CreatedAt = Now });
            }
        }

        await db.SaveChangesAsync(ct);
        return await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Code, ct);
    }

    private async Task<Dictionary<string, Supplier>> SeedSuppliersAsync(CancellationToken ct)
    {
        foreach (var (code, name) in SeedData.Suppliers)
        {
            if (!await db.Suppliers.AnyAsync(s => s.Code == code, ct))
            {
                db.Suppliers.Add(new Supplier { Code = code, Name = name, CreatedAt = Now });
            }
        }

        await db.SaveChangesAsync(ct);
        return await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Code, ct);
    }

    // ------------------------------------------------------------------ users

    private async Task<int> SeedUsersAsync(Dictionary<string, Warehouse> warehouses, CancellationToken ct)
    {
        var created = 0;
        foreach (var seed in SeedData.Users)
        {
            if (await db.Users.AnyAsync(u => u.UserName == seed.UserName, ct))
            {
                continue;
            }

            // Passwords are read from private files (Seed:Passwords:<user>); they are not in the source,
            // not generated here and not written to any log. An existing user's password is never reset.
            var password = configuration[$"Seed:Passwords:{seed.UserName}"];
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException($"Seed:Passwords:{seed.UserName} is not configured. Run scripts/local-stack init to generate demo passwords.");
            }

            var user = new User
            {
                UserName = seed.UserName,
                DisplayName = seed.DisplayName,
                PasswordHash = hasher.Hash(password.Trim()),
                PoApprovalLimit = seed.PoApprovalLimit,
                CreatedAt = Now,
            };
            user.Roles.AddRange(seed.Roles.Select(r => new UserRole { UserId = user.Id, Role = r }));
            user.Grants.AddRange(seed.Grants.Select(g => new WarehouseGrant { UserId = user.Id, WarehouseId = warehouses[g.Warehouse].Id, Level = g.Level }));
            db.Users.Add(user);
            created++;
        }

        if (!await db.Users.AnyAsync(u => u.UserName == OpeningActor, ct))
        {
            var actor = new User
            {
                UserName = OpeningActor,
                DisplayName = "Açılış Kaydı (oturum açamaz)",

                // A hash of random bytes nobody knows, on an inactive account.
                PasswordHash = hasher.Hash(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
                IsActive = false,
                PoApprovalLimit = 100_000_000m,
                CreatedAt = Now,
            };
            actor.Roles.Add(new UserRole { UserId = actor.Id, Role = Roles.PurchasingAgent });
            actor.Roles.Add(new UserRole { UserId = actor.Id, Role = Roles.WarehouseOperator });
            actor.Grants.AddRange(warehouses.Values.Select(w => new WarehouseGrant { UserId = actor.Id, WarehouseId = w.Id, Level = WarehouseLevel.Operate }));
            db.Users.Add(actor);
        }

        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>An in-process caller identity for seed commands. No session exists for it.</summary>
    private async Task<CurrentUser> ActorAsync(string userName, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Roles).Include(u => u.Grants).FirstAsync(u => u.UserName == userName, ct);
        return new CurrentUser(
            user.Id, user.UserName, user.DisplayName,
            user.Roles.Select(r => r.Role).ToHashSet(StringComparer.Ordinal),
            user.Grants.ToDictionary(g => g.WarehouseId, g => g.Level),
            user.PoApprovalLimit, Guid.Empty, MustChangePassword: false);
    }

    // ------------------------------------------------------------------ opening stock

    private async Task SeedOpeningStockAsync(
        CurrentUser actor, Warehouse warehouse, Supplier supplier, Dictionary<string, Product> products,
        IReadOnlyList<(string Sku, int Quantity, decimal UnitCost)> quantities, CancellationToken ct)
    {
        var reference = $"ACILIS-{warehouse.Code}-2026";
        if (await db.GoodsReceipts.AnyAsync(r => r.SupplierId == supplier.Id && r.ExternalReference == reference, ct))
        {
            return;
        }

        await unitOfWork.ExecuteAsync(
            async token =>
            {
                var command = PurchasingService.Normalize(new SavePurchaseOrderCommand(
                    null, supplier.Id, warehouse.Id, "Açılış stoku (demo)",
                    quantities.Select(q => new PurchaseLineInput(products[q.Sku].Id, q.Quantity, q.UnitCost)).ToArray(), null));
                var order = await purchasing.Create.ExecuteAsync(command, actor, token);
                order = await purchasing.Approve.ExecuteAsync(new PurchaseOrderActionCommand(order.Id, null, order.ETag), actor, token);
                await purchasing.Receive.ExecuteAsync(
                    new ReceiveCommand(order.Id, reference, order.Lines.Select(l => new FulfilmentLineInput(l.Id, l.OrderedQty)).ToArray(), order.ETag), actor, token);
                return true;
            },
            ct);
        db.ChangeTracker.Clear();
    }

    // ------------------------------------------------------------------ demo documents

    private async Task SeedSalesAsync(
        CurrentUser salesAgent, CurrentUser warehouseOperator, Warehouse warehouse, Dictionary<string, Customer> customers,
        Dictionary<string, Product> products, CancellationToken ct)
    {
        if (await db.SalesOrders.AnyAsync(ct))
        {
            return;
        }

        await unitOfWork.ExecuteAsync(
            async token =>
            {
                SaveSalesOrderCommand Draft(string customer, params (string Sku, int Quantity)[] lines) => SalesService.Normalize(new SaveSalesOrderCommand(
                    null, customers[customer].Id, warehouse.Id, null, lines.Select(l => new SalesLineInput(products[l.Sku].Id, l.Quantity)).ToArray(), null));

                // One draft, one confirmed and one partially shipped order, so every screen has something to show.
                await sales.Create.ExecuteAsync(Draft("MUS-YILDIZ", ("KBL-NYA-3X25", 4), ("PRZ-TOP-BYZ", 60)), salesAgent, token);

                var confirmed = await sales.Create.ExecuteAsync(Draft("MUS-MARMARA", ("AMP-LED-9W", 200), ("SLT-TEK-BYZ", 120)), salesAgent, token);
                await sales.Confirm.ExecuteAsync(new SalesOrderActionCommand(confirmed.Id, null, confirmed.ETag), salesAgent, token);

                var partial = await sales.Create.ExecuteAsync(Draft("MUS-EGE", ("VDA-SUN-4X40", 30), ("MTK-SDS-10", 25), ("DBL-PLS-8", 40)), salesAgent, token);
                partial = await sales.Confirm.ExecuteAsync(new SalesOrderActionCommand(partial.Id, null, partial.ETag), salesAgent, token);
                await sales.Ship.ExecuteAsync(
                    new ShipCommand(partial.Id, "DEMO-SEVK-0001", [new FulfilmentLineInput(partial.Lines[0].Id, 30), new FulfilmentLineInput(partial.Lines[1].Id, 10)], null),
                    warehouseOperator, token);
                return true;
            },
            ct);
        db.ChangeTracker.Clear();
    }

    private async Task SeedKnowledgeAsync(CurrentUser supportManager, Dictionary<string, Warehouse> warehouses, CancellationToken ct)
    {
        foreach (var article in SeedData.Articles)
        {
            if (await db.KbDocuments.AnyAsync(d => d.Slug == article.Slug, ct))
            {
                continue;
            }

            await unitOfWork.ExecuteAsync(
                async token =>
                {
                    var command = KnowledgeService.Normalize(new CreateKbDocumentCommand(
                        article.Slug, article.Visibility, article.Module, article.Warehouse is null ? null : warehouses[article.Warehouse].Id,
                        article.ErrorCode, article.Title, article.Body));
                    var document = await knowledge.CreateDocument.ExecuteAsync(command, supportManager, token);
                    if (article.Publish)
                    {
                        await knowledge.Publish.ExecuteAsync(new KbPublishCommand(document.Id, 1, document.ETag), supportManager, token);
                    }

                    return true;
                },
                ct);
            db.ChangeTracker.Clear();
        }
    }

    private async Task SeedTicketAsync(CurrentUser requester, CancellationToken ct)
    {
        if (await db.Tickets.AnyAsync(ct))
        {
            return;
        }

        await unitOfWork.ExecuteAsync(
            async token =>
            {
                var command = TicketService.Normalize(new CreateTicketCommand(
                    "Sipariş onaylanırken stok yetersiz hatası",
                    "Sipariş ekranında Onayla düğmesine bastığımda STK-409 kodlu hata alıyorum. Stok listesinde üründen var görünüyor; ne yapmam gerekir?",
                    "Normal", "Sales", "sales.order", "STK-409", null));
                await tickets.Create.ExecuteAsync(command, requester, token);
                return true;
            },
            ct);
        db.ChangeTracker.Clear();
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Seed finished: {UsersCreated} users created, {Balances} balances reconciled, {Findings} differences")]
    private static partial void LogDone(ILogger logger, int usersCreated, int balances, int findings);
}
