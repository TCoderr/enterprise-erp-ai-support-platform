using Kantar.Application.Abstractions;
using Kantar.Domain.Identity;
using Kantar.Infrastructure;
using Kantar.Infrastructure.Persistence;
using Kantar.Infrastructure.Seeding;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kantar.IntegrationTests.Seeding;

/// <summary>
/// The demo seed, run exactly as the `--seed` process runs it: against a freshly migrated database of
/// its own, through the application's services, with the restricted application login.
/// </summary>
public sealed class DemoSeederTests(KantarFixture fixture)
{
    private sealed class FixedContext : IRequestContext
    {
        public string CorrelationId => "seed-test";
    }

    private static ServiceProvider BuildServices(string appConnection, bool withPasswords, string? omitPasswordFor = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = appConnection,
            ["Security:PasswordHashing:Iterations"] = "1000",
        };
        if (withPasswords)
        {
            foreach (var user in SeedData.Users.Where(u => u.UserName != omitPasswordFor))
            {
                settings[$"Seed:Passwords:{user.UserName}"] = KantarFixture.NewPassword();   // kantar:allow-test-secret (random, per run)
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddKantar(configuration);
        services.AddSingleton<IRequestContext, FixedContext>();
        services.AddScoped<DemoSeeder>();
        return services.BuildServiceProvider();
    }

    private static async Task<int> SeedAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync(CancellationToken.None);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Seed_puts_opening_stock_in_through_receipts_and_a_second_run_changes_nothing()
    {
        var database = await fixture.NewDatabaseAsync("seed1");
        await DatabaseMigrator.MigrateAsync(database.Migrator, null, NullLogger.Instance, CancellationToken.None);
        await using var services = BuildServices(database.App, withPasswords: true);

        Assert.Equal(0, await SeedAsync(services));

        // Every unit of opening stock is in the journal: balance = sum of movements, per warehouse and product.
        var expectedOnHand = SeedData.OpeningStock.Sum(w => w.Value.Sum(q => q.Quantity));
        var shippedBySeed = 40;   // the demo partial shipment: 30 + 10
        Assert.Equal(expectedOnHand - shippedBySeed, await ScalarAsync<int>(database.App, "SELECT SUM(OnHand) FROM inv.StockBalances;"));
        Assert.Equal(expectedOnHand - shippedBySeed, await ScalarAsync<int>(database.App, "SELECT SUM(QuantityDelta) FROM inv.StockMovements;"));
        Assert.Equal(0, await ScalarAsync<int>(database.App, "SELECT COUNT(*) FROM inv.StockMovements WHERE Type = 5;"));   // no adjustment was used to create stock
        Assert.Equal(3, await ScalarAsync<int>(database.App, "SELECT COUNT(*) FROM purch.GoodsReceipts WHERE ExternalReference LIKE 'ACILIS-%';"));
        Assert.Equal(await ScalarAsync<int>(database.App, "SELECT SUM(Reserved) FROM inv.StockBalances;"), await ScalarAsync<int>(database.App, "SELECT SUM(QuantityDelta) FROM inv.ReservationEntries;"));
        Assert.Equal(0, await ScalarAsync<int>(database.App, "SELECT COUNT(*) FROM inv.StockBalances WHERE Reserved < 0 OR OnHand < Reserved;"));

        var before = await SnapshotAsync(database.App);
        Assert.Equal(0, await SeedAsync(services));
        Assert.Equal(before, await SnapshotAsync(database.App));

        Assert.Equal("Consistent", await ScalarAsync<string>(
            database.App,
            "SELECT TOP (1) CASE WHEN FindingCount = 0 THEN 'Consistent' ELSE 'DriftFound' END FROM ops.ReconciliationRuns ORDER BY CompletedAt DESC;"));
    }

    private static async Task<string> SnapshotAsync(string connectionString)
    {
        var counts = new List<string>();
        foreach (var table in new[]
                 {
                     "iam.Users", "md.Products", "md.Customers", "md.Suppliers", "md.Warehouses", "inv.StockMovements", "inv.ReservationEntries",
                     "purch.PurchaseOrders", "purch.GoodsReceipts", "sales.SalesOrders", "sales.Shipments", "sup.KbDocuments", "sup.KbDocumentVersions",
                     "sup.KbChunks", "sup.Tickets",
                 })
        {
            counts.Add($"{table}={await ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM {table};")}");
        }

        counts.Add("onhand=" + await ScalarAsync<int>(connectionString, "SELECT SUM(OnHand) FROM inv.StockBalances;"));
        counts.Add("reserved=" + await ScalarAsync<int>(connectionString, "SELECT SUM(Reserved) FROM inv.StockBalances;"));
        return string.Join(";", counts);
    }

    [Fact]
    public async Task Seed_creates_the_documented_demo_users_and_an_opening_actor_that_cannot_sign_in()
    {
        var database = await fixture.NewDatabaseAsync("seed2");
        await DatabaseMigrator.MigrateAsync(database.Migrator, null, NullLogger.Instance, CancellationToken.None);
        await using var services = BuildServices(database.App, withPasswords: true);
        Assert.Equal(0, await SeedAsync(services));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KantarDbContext>();
        var users = await db.Users.AsNoTracking().Include(u => u.Roles).Include(u => u.Grants).ToListAsync();

        Assert.Equal(SeedData.Users.Length + 1, users.Count);
        var actor = users.Single(u => u.UserName == DemoSeeder.OpeningActor);
        Assert.False(actor.IsActive);

        var admin = users.Single(u => u.UserName == "admin");
        Assert.Equal([Roles.SystemAdmin], admin.Roles.Select(r => r.Role));
        Assert.Empty(admin.Grants);   // no implicit warehouse access for the administrator

        var izmir = await db.Warehouses.AsNoTracking().SingleAsync(w => w.Code == "IZM-01");
        var withIzmirAccess = users.Where(u => u.IsActive && u.Grants.Any(g => g.WarehouseId == izmir.Id)).Select(u => u.UserName).ToArray();
        Assert.Equal(["depo.izmir"], withIzmirAccess);

        Assert.Equal(0m, users.Single(u => u.UserName == "satinalma1").PoApprovalLimit);
        Assert.Equal(250_000m, users.Single(u => u.UserName == "satinalma.onay").PoApprovalLimit);

        // Passwords are stored only as salted hashes.
        Assert.All(users, u => Assert.StartsWith("v1.", u.PasswordHash, StringComparison.Ordinal));

        // One draft article exists and is not served; the rest are published with chunks.
        Assert.Equal(1, await db.KbDocuments.CountAsync(d => d.Status == Domain.Support.KbStatus.Draft));
        Assert.Equal(SeedData.Articles.Count(a => a.Publish), await db.KbDocuments.CountAsync(d => d.Status == Domain.Support.KbStatus.Published));
        Assert.True(await db.KbChunks.AnyAsync());
    }

    [Fact]
    public async Task Seed_refuses_to_invent_a_password_when_one_is_not_configured()
    {
        var database = await fixture.NewDatabaseAsync("seed3");
        await DatabaseMigrator.MigrateAsync(database.Migrator, null, NullLogger.Instance, CancellationToken.None);
        await using var services = BuildServices(database.App, withPasswords: true, omitPasswordFor: "destek1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(services));

        Assert.Contains("Seed:Passwords:destek1", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync<int>(database.App, "SELECT COUNT(*) FROM iam.Users WHERE UserName = 'destek1';"));
    }
}
