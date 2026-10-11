using System.Net;
using System.Text.Json.Nodes;
using Kantar.Application.Abstractions;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>
/// Synthetic data for one test class. Every code carries a unique suffix so classes sharing the one
/// database never see each other's rows. Arrangement uses the restricted application login; stock
/// is put in place through the real purchase-order and goods-receipt endpoints.
/// </summary>
public sealed class World(KantarFixture fixture)
{
    public const string Password = "Test!Parola123aA";
    private static int _counter;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    private string? _passwordHash;
    private ApiClient? _buyer;
    private ApiClient? _receiver;
    private Guid _supplierId;
    private readonly HashSet<Guid> _stockerWarehouses = [];
    private Guid _stockerId;

    public KantarFixture Fixture => fixture;

    private string Next(string prefix) => $"{prefix}-{_tag}-{Interlocked.Increment(ref _counter)}";

    private async Task<T> DbAsync<T>(Func<KantarDbContext, Task<T>> work)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<KantarDbContext>());
    }

    public async Task<Warehouse> WarehouseAsync(bool active = true) => await DbAsync(async db =>
    {
        var code = Next("W");
        var warehouse = new Warehouse { Code = code[..Math.Min(16, code.Length)], Name = "Depo " + code, IsActive = active, CreatedAt = DateTime.UtcNow };
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse;
    });

    public async Task<Product> ProductAsync(decimal price = 100m, int reorderLevel = 0, bool active = true) => await DbAsync(async db =>
    {
        var product = new Product { Sku = Next("SKU"), Name = "Ürün " + _tag, Unit = "ADET", ListPrice = price, ReorderLevel = reorderLevel, IsActive = active, CreatedAt = DateTime.UtcNow };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    });

    public async Task<Customer> CustomerAsync(bool active = true) => await DbAsync(async db =>
    {
        var customer = new Customer { Code = Next("C"), Name = "Müşteri " + _tag, TaxNumber = "1234567890", Address = "Sentetik Mah. Örnek Sk. No:1 İstanbul", IsActive = active, CreatedAt = DateTime.UtcNow };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    });

    public async Task<Supplier> SupplierAsync(bool active = true) => await DbAsync(async db =>
    {
        var supplier = new Supplier { Code = Next("S"), Name = "Tedarikçi " + _tag, IsActive = active, CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    });

    public async Task<User> UserAsync(string[] roles, (Guid WarehouseId, WarehouseLevel Level)[]? grants = null, decimal poLimit = 0, bool mustChangePassword = false)
    {
        _passwordHash ??= fixture.Services.GetRequiredService<IPasswordHasher>().Hash(Password);
        return await DbAsync(async db =>
        {
            var user = new User
            {
                UserName = Next("u").ToLowerInvariant(),
                DisplayName = "Test Kullanıcısı",
                PasswordHash = _passwordHash,
                PoApprovalLimit = poLimit,
                MustChangePassword = mustChangePassword,
                CreatedAt = DateTime.UtcNow,
            };
            user.Roles.AddRange(roles.Select(r => new UserRole { UserId = user.Id, Role = r }));
            user.Grants.AddRange((grants ?? []).Select(g => new WarehouseGrant { UserId = user.Id, WarehouseId = g.WarehouseId, Level = g.Level }));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        });
    }

    public async Task<ApiClient> LoginAsync(User user, string password = Password)
    {
        var client = fixture.NewClient();
        using var response = await client.LoginAsync(user.UserName, password);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Login failed for {user.UserName}: {(int)response.StatusCode}");
        return client;
    }

    public async Task<ApiClient> LoginAsAsync(string[] roles, (Guid WarehouseId, WarehouseLevel Level)[]? grants = null, decimal poLimit = 0) =>
        await LoginAsync(await UserAsync(roles, grants, poLimit));

    /// <summary>Receives <paramref name="quantity"/> units into a warehouse through an approved PO and a goods receipt.</summary>
    public async Task StockAsync(Warehouse warehouse, Product product, int quantity) =>
        await StockAsync(warehouse, [(product, quantity)]);

    public async Task StockAsync(Warehouse warehouse, IReadOnlyList<(Product Product, int Quantity)> items)
    {
        if (_buyer is null)
        {
            _supplierId = (await SupplierAsync()).Id;
            var stocker = await UserAsync([Roles.PurchasingAgent, Roles.WarehouseOperator], poLimit: 999_999_999m);
            _stockerId = stocker.Id;
            _buyer = await LoginAsync(stocker);
            _receiver = _buyer;
        }

        if (_stockerWarehouses.Add(warehouse.Id))
        {
            await DbAsync(async db =>
            {
                db.WarehouseGrants.Add(new WarehouseGrant { UserId = _stockerId, WarehouseId = warehouse.Id, Level = WarehouseLevel.Operate });
                return await db.SaveChangesAsync();
            });
        }

        using var created = await _buyer.PostAsync("/api/purchasing/orders", new
        {
            supplierId = _supplierId,
            warehouseId = warehouse.Id,
            lines = items.Select(i => new { productId = i.Product.Id, quantity = i.Quantity, unitCost = 10.00m }),
        }, ApiClient.NewKey());
        var order = await created.JsonAsync(HttpStatusCode.Created);

        using var approved = await _buyer.PostAsync($"/api/purchasing/orders/{order.Id()}/approve", null, ApiClient.NewKey(), created.ETag());
        var approvedOrder = await approved.JsonAsync(HttpStatusCode.OK);

        using var received = await _receiver!.PostAsync($"/api/purchasing/orders/{order.Id()}/receipts", new
        {
            externalReference = Next("IRS"),
            lines = approvedOrder["lines"]!.AsArray().Select(l => new { orderLineId = l!.Id(), quantity = l!.Number("orderedQty") }),
        }, ApiClient.NewKey());
        await received.JsonAsync(HttpStatusCode.Created);
    }

    public async Task<(int OnHand, int Reserved)> BalanceAsync(Guid warehouseId, Guid productId) => await DbAsync(async db =>
    {
        var balance = await db.StockBalances.AsNoTracking().FirstOrDefaultAsync(b => b.WarehouseId == warehouseId && b.ProductId == productId);
        return balance is null ? (0, 0) : (balance.OnHand, balance.Reserved);
    });

    public async Task<int> MovementCountAsync(Guid warehouseId, Guid productId) =>
        await DbAsync(db => db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId && m.ProductId == productId));

    public async Task<int> ReservationCountAsync(Guid warehouseId, Guid productId) =>
        await DbAsync(db => db.ReservationEntries.CountAsync(r => r.WarehouseId == warehouseId && r.ProductId == productId));

    /// <summary>
    /// The three reconciliation equalities of docs/04 §6 for one balance, computed straight from the tables:
    /// journal sum = OnHand, reservation ledger sum = Reserved, open confirmed quantity = Reserved.
    /// </summary>
    public async Task AssertReconciledAsync(Guid warehouseId, Guid productId)
    {
        const string sql = """
            SELECT
              ISNULL((SELECT OnHand FROM inv.StockBalances WHERE WarehouseId = @w AND ProductId = @p), 0) AS OnHand,
              ISNULL((SELECT Reserved FROM inv.StockBalances WHERE WarehouseId = @w AND ProductId = @p), 0) AS Reserved,
              ISNULL((SELECT SUM(QuantityDelta) FROM inv.StockMovements WHERE WarehouseId = @w AND ProductId = @p), 0) AS JournalSum,
              ISNULL((SELECT SUM(QuantityDelta) FROM inv.ReservationEntries WHERE WarehouseId = @w AND ProductId = @p), 0) AS LedgerSum,
              ISNULL((SELECT SUM(l.OrderedQty - l.ShippedQty - l.CancelledQty)
                      FROM sales.SalesOrderLines l JOIN sales.SalesOrders o ON o.Id = l.OrderId
                      WHERE o.WarehouseId = @w AND l.ProductId = @p AND o.Status IN (2, 3)), 0) AS OpenConfirmed;
            """;
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@w", warehouseId);
        command.Parameters.AddWithValue("@p", productId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var (onHand, reserved, journal, ledger, open) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
        Assert.Equal(onHand, journal);
        Assert.Equal(reserved, ledger);
        Assert.Equal(reserved, open);
        Assert.True(reserved >= 0 && onHand >= reserved, $"Invariant broken: OnHand={onHand}, Reserved={reserved}");
    }

    // ---- request shortcuts used by many tests ----

    public static async Task<(JsonNode Order, string ETag)> CreateSalesOrderAsync(ApiClient client, Guid customerId, Guid warehouseId, params (Guid ProductId, int Quantity)[] lines)
    {
        using var response = await client.PostAsync("/api/sales/orders", new
        {
            customerId,
            warehouseId,
            lines = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity }),
        }, ApiClient.NewKey());
        var order = await response.JsonAsync(HttpStatusCode.Created);
        return (order, response.ETag());
    }

    public static async Task<(JsonNode Order, string ETag)> ConfirmAsync(ApiClient client, JsonNode order, string etag)
    {
        using var response = await client.PostAsync($"/api/sales/orders/{order.Id()}/confirm", null, ApiClient.NewKey(), etag);
        return (await response.JsonAsync(HttpStatusCode.OK), response.ETag());
    }

    public static async Task<(JsonNode Order, string ETag)> ConfirmedSalesOrderAsync(ApiClient client, Guid customerId, Guid warehouseId, params (Guid ProductId, int Quantity)[] lines)
    {
        var (order, etag) = await CreateSalesOrderAsync(client, customerId, warehouseId, lines);
        return await ConfirmAsync(client, order, etag);
    }

    public static async Task<(JsonNode Order, string ETag)> CreatePurchaseOrderAsync(ApiClient client, Guid supplierId, Guid warehouseId, params (Guid ProductId, int Quantity)[] lines)
    {
        using var response = await client.PostAsync("/api/purchasing/orders", new
        {
            supplierId,
            warehouseId,
            lines = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity, unitCost = 10.00m }),
        }, ApiClient.NewKey());
        var order = await response.JsonAsync(HttpStatusCode.Created);
        return (order, response.ETag());
    }

    /// <summary>A purchase order created and approved by <paramref name="client"/>, who needs an approval limit above ten times the quantity.</summary>
    public static async Task<(JsonNode Order, string ETag)> ApprovedPurchaseOrderAsync(ApiClient client, Guid supplierId, Guid warehouseId, params (Guid ProductId, int Quantity)[] lines)
    {
        var (order, etag) = await CreatePurchaseOrderAsync(client, supplierId, warehouseId, lines);
        using var response = await client.PostAsync($"/api/purchasing/orders/{order.Id()}/approve", null, ApiClient.NewKey(), etag);
        return (await response.JsonAsync(HttpStatusCode.OK), response.ETag());
    }

    public static Task<HttpResponseMessage> ReceiveAsync(ApiClient client, Guid orderId, string externalReference, params (Guid LineId, int Quantity)[] lines) =>
        client.PostAsync($"/api/purchasing/orders/{orderId}/receipts", ReceiptBody(externalReference, lines), ApiClient.NewKey());

    public static object ReceiptBody(string externalReference, params (Guid LineId, int Quantity)[] lines) => new
    {
        externalReference,
        lines = lines.Select(l => new { orderLineId = l.LineId, quantity = l.Quantity }).ToArray(),
    };

    public static Task<HttpResponseMessage> ShipAsync(ApiClient client, Guid orderId, string dispatchReference, params (Guid LineId, int Quantity)[] lines) =>
        client.PostAsync($"/api/sales/orders/{orderId}/shipments", ShipmentBody(dispatchReference, lines), ApiClient.NewKey());

    public static object ShipmentBody(string dispatchReference, params (Guid LineId, int Quantity)[] lines) => new
    {
        dispatchReference,
        lines = lines.Select(l => new { orderLineId = l.LineId, quantity = l.Quantity }).ToArray(),
    };

    public static Task<HttpResponseMessage> CancelSalesOrderAsync(ApiClient client, Guid orderId, string? etag) =>
        client.PostAsync($"/api/sales/orders/{orderId}/cancel", new { reason = "Müşteri siparişten vazgeçti" }, ApiClient.NewKey(), etag);

    public static Task<HttpResponseMessage> CancelPurchaseOrderAsync(ApiClient client, Guid orderId, string? etag) =>
        client.PostAsync($"/api/purchasing/orders/{orderId}/cancel", new { reason = "Tedarikçi teslim edemiyor" }, ApiClient.NewKey(), etag);

    public static Task<HttpResponseMessage> TransferAsync(ApiClient client, string reference, Guid fromWarehouseId, Guid toWarehouseId, params (Guid ProductId, int Quantity)[] lines) =>
        client.PostAsync("/api/inventory/transfers", TransferBody(reference, fromWarehouseId, toWarehouseId, lines), ApiClient.NewKey());

    public static object TransferBody(string reference, Guid fromWarehouseId, Guid toWarehouseId, params (Guid ProductId, int Quantity)[] lines) => new
    {
        reference,
        fromWarehouseId,
        toWarehouseId,
        lines = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity }).ToArray(),
    };

    public static Task<HttpResponseMessage> AdjustAsync(ApiClient client, string reference, Guid warehouseId, string reasonCode, params (Guid ProductId, int QuantityDelta)[] lines) =>
        client.PostAsync("/api/inventory/adjustments", AdjustmentBody(reference, warehouseId, reasonCode, lines), ApiClient.NewKey());

    public static object AdjustmentBody(string reference, Guid warehouseId, string reasonCode, params (Guid ProductId, int QuantityDelta)[] lines) => new
    {
        reference,
        warehouseId,
        reasonCode,
        note = "Sayım sonucu düzeltme kaydı",
        lines = lines.Select(l => new { productId = l.ProductId, quantityDelta = l.QuantityDelta }).ToArray(),
    };

    /// <summary>The current ETag of a document or master-data row, read the way a client would.</summary>
    public static async Task<string> ETagAsync(ApiClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} returned {(int)response.StatusCode}");
        return response.ETag();
    }

    /// <summary>A business reference that no other test uses.</summary>
    public string Reference(string prefix) => Next(prefix);

    /// <summary>Independent HTTP clients presenting the same session, one per parallel request.</summary>
    public ApiClient[] Clones(ApiClient source, int count) =>
        Enumerable.Range(0, count).Select(_ => source.CloneWith(fixture.Factory.CreateDefaultClient(new Uri("https://localhost")))).ToArray();

    /// <summary>Replaces a user's warehouse grants through the admin endpoint, as an administrator would.</summary>
    public static async Task SetGrantsAsync(ApiClient admin, Guid userId, params (Guid WarehouseId, WarehouseLevel Level)[] grants)
    {
        var etag = await ETagAsync(admin, $"/api/admin/users/{userId}");
        using var response = await admin.PutAsync(
            $"/api/admin/users/{userId}/warehouse-grants",
            new { grants = grants.Select(g => new { warehouseId = g.WarehouseId, level = g.Level.ToString() }).ToArray() },
            etag);
        await response.JsonAsync(HttpStatusCode.OK);
    }

    public static async Task SetRolesAsync(ApiClient admin, Guid userId, params string[] roles)
    {
        var etag = await ETagAsync(admin, $"/api/admin/users/{userId}");
        using var response = await admin.PutAsync($"/api/admin/users/{userId}/roles", new { roles }, etag);
        await response.JsonAsync(HttpStatusCode.OK);
    }

    // ---- row counts read with the restricted login, outside the application ----

    public Task<int> CountAsync(string sql, params SqlParameter[] parameters) => fixture.AppScalarAsync<int>(sql, parameters);

    public Task<int> MovementCountAsync(Guid warehouseId, Guid productId, string type) => CountAsync(
        "SELECT COUNT(*) FROM inv.StockMovements WHERE WarehouseId = @w AND ProductId = @p AND Type = @t;",
        new SqlParameter("@w", warehouseId), new SqlParameter("@p", productId),
        new SqlParameter("@t", (byte)Enum.Parse<Kantar.Domain.Inventory.MovementType>(type)));

    public Task<int> ReservationCountAsync(Guid warehouseId, Guid productId, string reason) => CountAsync(
        "SELECT COUNT(*) FROM inv.ReservationEntries WHERE WarehouseId = @w AND ProductId = @p AND Reason = @r;",
        new SqlParameter("@w", warehouseId), new SqlParameter("@p", productId),
        new SqlParameter("@r", (byte)Enum.Parse<Kantar.Domain.Inventory.ReservationReason>(reason)));

    public Task<int> IdempotencyRecordCountAsync(string key) => CountAsync(
        "SELECT COUNT(*) FROM ops.IdempotencyRecords WHERE IdempotencyKey = @k;", new SqlParameter("@k", key));

    public Task<int> AuditCountAsync(Guid actorUserId, string action) => CountAsync(
        "SELECT COUNT(*) FROM ops.AuditEntries WHERE ActorUserId = @u AND Action = @a;",
        new SqlParameter("@u", actorUserId), new SqlParameter("@a", action));
}
