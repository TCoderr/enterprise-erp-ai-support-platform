using System.Globalization;
using Kantar.IntegrationTests.Infrastructure;
using Kantar.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kantar.IntegrationTests.Database;

/// <summary>
/// Migrations against real, separate databases inside this run's own container: from empty, as an
/// upgrade over existing rows, and repeated. The main test database is not touched here.
/// </summary>
public sealed class MigrationTests(KantarFixture fixture)
{
    private static readonly string[] CoreTables =
    [
        "iam.Sessions", "iam.UserRoles", "iam.Users", "iam.WarehouseGrants",
        "inv.ReservationEntries", "inv.StockAdjustmentLines", "inv.StockAdjustments", "inv.StockBalances", "inv.StockMovements",
        "inv.StockTransferLines", "inv.StockTransfers",
        "md.Customers", "md.Products", "md.Suppliers", "md.Warehouses",
        "ops.AuditEntries", "ops.IdempotencyRecords",
        "purch.GoodsReceiptLines", "purch.GoodsReceipts", "purch.PurchaseOrderLines", "purch.PurchaseOrders",
        "sales.SalesOrderLines", "sales.SalesOrders", "sales.ShipmentLines", "sales.Shipments",
    ];

    private static readonly string[] SupportAndOperationsTables =
    [
        "ai.Answers", "ai.Usage",
        "ops.Notifications", "ops.OutboxConsumptions", "ops.OutboxMessages", "ops.ReconciliationFindings", "ops.ReconciliationRuns",
        "sup.KbChunks", "sup.KbDocumentVersions", "sup.KbDocuments", "sup.TicketComments", "sup.Tickets",
    ];

    private static readonly string[] Sequences =
    [
        "inv.StockAdjustmentNumbers", "inv.StockTransferNumbers", "purch.GoodsReceiptNumbers", "purch.PurchaseOrderNumbers",
        "sales.SalesOrderNumbers", "sales.ShipmentNumbers", "sup.TicketNumbers",
    ];

    private static string Suffix(string kind) => $"{kind}_{Guid.NewGuid():N}"[..(kind.Length + 9)];

    private static KantarDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<KantarDbContext>().UseSqlServer(connectionString).Options);

    private static Task MigrateAsync(string migratorConnection, string? target = null) =>
        DatabaseMigrator.MigrateAsync(migratorConnection, target, NullLogger.Instance, CancellationToken.None);

    private static async Task<List<object?[]>> QueryAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task<int> ExecuteAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static string Format(object? value) => value switch
    {
        null => "(null)",
        byte[] bytes => Convert.ToHexString(bytes),
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// One sorted line per row, columns joined on the client. Catalog columns such as permission_name
    /// have a fixed collation, so concatenating them with object names in T-SQL would fail in this database.
    /// </summary>
    private static async Task<string[]> LinesAsync(string connectionString, string sql, string separator = " ", params SqlParameter[] parameters) =>
        (await QueryAsync(connectionString, sql, parameters)).Select(r => string.Join(separator, r.Select(Format))).Order(StringComparer.Ordinal).ToArray();

    private static Task<string[]> TablesAsync(string connectionString) => LinesAsync(
        connectionString,
        "SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name <> N'dbo';",
        ".");

    private static Task<string[]> SequencesAsync(string connectionString) => LinesAsync(
        connectionString,
        "SELECT s.name, q.name FROM sys.sequences q JOIN sys.schemas s ON s.schema_id = q.schema_id;",
        ".");

    private static Task<string[]> HistoryAsync(string connectionString) => LinesAsync(connectionString, "SELECT MigrationId FROM dbo.__EFMigrationsHistory;");

    /// <summary>What the restricted login may do, as the server itself records it: one line per table, action and grant state.</summary>
    private static Task<string[]> AppPermissionsAsync(string connectionString) => LinesAsync(
        connectionString,
        """
        SELECT s.name, o.name, p.permission_name, p.state_desc
        FROM sys.database_permissions p
        JOIN sys.objects o ON o.object_id = p.major_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE p.class = 1 AND p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'kantar_app');
        """);

    private static string[] Sorted(params string[][] groups) => groups.SelectMany(g => g).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Removes a database this test created. Best effort: the whole container is discarded at the end of the run anyway.</summary>
    private static async Task DropAsync((string Name, string Migrator, string App, string Sysadmin) database)
    {
        foreach (var connectionString in new[] { database.Migrator, database.App, database.Sysadmin })
        {
            await using var pooled = new SqlConnection(connectionString);
            SqlConnection.ClearPool(pooled);
        }

        try
        {
            var master = new SqlConnectionStringBuilder(database.Sysadmin) { InitialCatalog = "master" }.ConnectionString;
            await ExecuteAsync(
                master,
                """
                DECLARE @statement nvarchar(max) =
                    N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';';
                IF DB_ID(@name) IS NOT NULL AND @name LIKE N'kantar[_]test[_]%' EXEC sys.sp_executesql @statement;
                """,
                new SqlParameter("@name", database.Name));
        }
        catch (SqlException)
        {
            // Leaving the database behind is harmless; failing the test for it would hide the real result.
        }
    }

    [Fact]
    public async Task Empty_database_migrates_to_the_current_model_with_the_runtime_grants_in_place()
    {
        var database = await fixture.NewDatabaseAsync(Suffix("empty"));
        try
        {
            await MigrateAsync(database.Migrator);

            await using var context = Context(database.Migrator);
            var compiled = context.Database.GetMigrations().ToArray();
            Assert.True(compiled.Length >= 2, "Both the core and the support/operations migrations are expected in the assembly.");
            Assert.Contains(compiled, m => m.EndsWith("_InitialCore", StringComparison.Ordinal));
            Assert.Contains(compiled, m => m.EndsWith("_SupportAndOperations", StringComparison.Ordinal));
            Assert.Equal(compiled.Order(StringComparer.Ordinal).ToArray(), await HistoryAsync(database.Migrator));
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());

            // The migrations describe exactly the model the application is compiled against.
            Assert.False(context.Database.HasPendingModelChanges());

            Assert.Equal(Sorted(CoreTables, SupportAndOperationsTables), await TablesAsync(database.Migrator));
            Assert.Equal(Sorted(Sequences), await SequencesAsync(database.Migrator));
            var settings = Assert.Single(await QueryAsync(
                database.Migrator, "SELECT collation_name, is_read_committed_snapshot_on, snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID();"));
            Assert.Equal(DatabaseBootstrapper.DatabaseCollation, settings[0]);
            Assert.Equal(true, settings[1]);
            Assert.Equal((byte)0, settings[2]);

            // The restricted login works on mutable tables and is refused on the journal.
            Assert.Equal("kantar_app", Assert.Single(await QueryAsync(database.App, "SELECT USER_NAME();"))[0]);
            Assert.Equal(1, await ExecuteAsync(
                database.App,
                "INSERT INTO md.Warehouses (Id, Code, Name, IsActive, CreatedAt) VALUES (NEWID(), 'W-MIG-1', N'Göç testi deposu', 1, SYSUTCDATETIME());"));
            Assert.Equal(1, await ExecuteAsync(database.App, "UPDATE md.Warehouses SET Name = N'Yeni ad' WHERE Code = 'W-MIG-1';"));
            var update = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.App, "UPDATE inv.StockMovements SET QuantityDelta = QuantityDelta WHERE 1 = 0;"));
            Assert.Equal(229, update.Number);
            var delete = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.App, "DELETE FROM md.Warehouses WHERE Code = 'W-MIG-1';"));
            Assert.Equal(229, delete.Number);
            var ddl = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.App, "CREATE TABLE md.Injected (Id int);"));
            Assert.Contains(ddl.Number, new[] { 262, 2760 });
            Assert.Equal(1L, Assert.Single(await QueryAsync(database.App, "SELECT NEXT VALUE FOR sup.TicketNumbers;"))[0]);

            // The readiness probe needs to read the migration history with the restricted login.
            Assert.Equal(compiled.Order(StringComparer.Ordinal).ToArray(), await HistoryAsync(database.App));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Upgrade_from_the_core_schema_keeps_existing_rows_and_adds_the_new_tables_with_their_grants()
    {
        var database = await fixture.NewDatabaseAsync(Suffix("upgrade"));
        try
        {
            string[] compiled;
            await using (var context = Context(database.Migrator))
            {
                compiled = context.Database.GetMigrations().ToArray();
            }

            var core = compiled[0];
            Assert.EndsWith("_InitialCore", core, StringComparison.Ordinal);

            // --- the database as it was after the earlier stage ---
            await MigrateAsync(database.Migrator, core);
            Assert.Equal([core], await HistoryAsync(database.Migrator));
            Assert.Equal(Sorted(CoreTables), await TablesAsync(database.Migrator));
            Assert.DoesNotContain("sup.TicketNumbers", await SequencesAsync(database.Migrator));

            // Synthetic rows that satisfy the stock constraints: a receipt of 12 units and the matching balance.
            var (warehouse, product, actor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            SqlParameter[] Ids() => [new("@w", warehouse), new("@p", product), new("@u", actor)];
            await ExecuteAsync(
                database.Migrator,
                """
                INSERT INTO md.Warehouses (Id, Code, Name, IsActive, CreatedAt) VALUES (@w, 'W-ESKI-1', N'Eski aşama deposu', 1, '2026-01-05T08:00:00');
                INSERT INTO md.Products (Id, Sku, Name, Unit, ListPrice, ReorderLevel, IsActive, CreatedAt)
                VALUES (@p, 'SKU-ESKI-1', N'Eski aşama ürünü — çğıöşü', 'ADET', 149.90, 5, 1, '2026-01-05T08:00:00');
                INSERT INTO iam.Users (Id, UserName, DisplayName, PasswordHash, SecurityStamp, IsActive, MustChangePassword, PoApprovalLimit, FailedLoginCount, CreatedAt)
                VALUES (@u, 'eski.kullanici', N'Eski Kullanıcı', 'sentetik-ozet-degeri', NEWID(), 0, 0, 0, 0, '2026-01-05T08:00:00');
                INSERT INTO inv.StockBalances (WarehouseId, ProductId, OnHand, Reserved) VALUES (@w, @p, 12, 0);
                INSERT INTO inv.StockMovements (WarehouseId, ProductId, Type, QuantityDelta, OnHandAfter, ReferenceType, ReferenceId, ReferenceLineId, ActorUserId, OccurredAt)
                VALUES (@w, @p, 1, 12, 12, 1, NEWID(), NEWID(), @u, '2026-01-05T08:30:00');
                INSERT INTO ops.AuditEntries (OccurredAt, ActorUserId, Action, EntityType, EntityId, WarehouseId, CorrelationId, DetailJson)
                VALUES ('2026-01-05T08:30:00', @u, 'purchasing.goods_received', 'GoodsReceipt', NEWID(), @w, 'eski-asama-korelasyon', N'{"lines":1}');
                """,
                Ids());

            (string Name, string Sql)[] existingRows =
            [
                ("warehouse", "SELECT Id, Code, Name, IsActive, CreatedAt, RowVersion FROM md.Warehouses WHERE Id = @w;"),
                ("product", "SELECT Id, Sku, Name, Unit, ListPrice, ReorderLevel, IsActive, CreatedAt, RowVersion FROM md.Products WHERE Id = @p;"),
                ("user", "SELECT Id, UserName, DisplayName, PasswordHash, SecurityStamp, IsActive, MustChangePassword, PoApprovalLimit, FailedLoginCount, LockedUntil, CreatedAt, RowVersion FROM iam.Users WHERE Id = @u;"),
                ("balance", "SELECT WarehouseId, ProductId, OnHand, Reserved, RowVersion FROM inv.StockBalances WHERE WarehouseId = @w AND ProductId = @p;"),
                ("movement", "SELECT Id, WarehouseId, ProductId, Type, QuantityDelta, OnHandAfter, ReferenceType, ReferenceId, ReferenceLineId, ActorUserId, OccurredAt FROM inv.StockMovements WHERE WarehouseId = @w AND ProductId = @p;"),
                ("audit", "SELECT Id, OccurredAt, ActorUserId, Action, EntityType, EntityId, WarehouseId, CorrelationId, DetailJson FROM ops.AuditEntries WHERE ActorUserId = @u;"),
            ];

            // One line per row with every column, including the rowversion where the table has one.
            async Task<string[]> SnapshotAsync()
            {
                var lines = new List<string>();
                foreach (var (name, sql) in existingRows)
                {
                    lines.Add(name + ": " + Assert.Single(await LinesAsync(database.Migrator, sql, " | ", Ids())));
                }

                return lines.ToArray();
            }

            var before = await SnapshotAsync();
            Assert.Contains("çğıöşü", before[1], StringComparison.Ordinal);
            Assert.Contains(" | 12 | 0 | ", before[3], StringComparison.Ordinal);

            // Already at this stage the restricted login has its grants on the tables that exist.
            Assert.Equal(12, Assert.Single(await QueryAsync(database.App, "SELECT OnHand FROM inv.StockBalances WHERE WarehouseId = @w AND ProductId = @p;", Ids()[..2]))[0]);
            var missing = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.App, "SELECT COUNT(*) FROM sup.Tickets;"));
            Assert.Equal(208, missing.Number);

            // --- upgrade to the current schema ---
            await MigrateAsync(database.Migrator);

            Assert.Equal(compiled.Order(StringComparer.Ordinal).ToArray(), await HistoryAsync(database.Migrator));
            Assert.Equal(Sorted(CoreTables, SupportAndOperationsTables), await TablesAsync(database.Migrator));
            Assert.Equal(Sorted(Sequences), await SequencesAsync(database.Migrator));
            await using (var context = Context(database.Migrator))
            {
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                Assert.False(context.Database.HasPendingModelChanges());
            }

            // Existing rows are byte-for-byte what they were: same values and, where the table has one, the same rowversion.
            Assert.Equal(before, await SnapshotAsync());
            Assert.Equal(12, Assert.Single(await QueryAsync(
                database.App, "SELECT SUM(QuantityDelta) FROM inv.StockMovements WHERE WarehouseId = @w AND ProductId = @p;", Ids()[..2]))[0]);

            // New tables are empty and usable by the restricted login according to their kind.
            foreach (var table in SupportAndOperationsTables)
            {
                Assert.Equal(0, Assert.Single(await QueryAsync(database.App, $"SELECT COUNT(*) FROM {table};"))[0]);
            }

            Assert.Equal(1, await ExecuteAsync(
                database.App,
                """
                INSERT INTO ops.OutboxMessages (Id, Type, PayloadJson, CreatedAt, AvailableAt, Attempts, LeaseVersion)
                VALUES (NEWID(), 'test.noop', N'{}', SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 0);
                """));
            Assert.Equal(1, await ExecuteAsync(database.App, "UPDATE ops.OutboxMessages SET Attempts = Attempts + 1 WHERE Type = 'test.noop';"));
            Assert.Equal(1, await ExecuteAsync(database.App, "DELETE FROM ops.OutboxMessages WHERE Type = 'test.noop';"));
            foreach (var appendOnly in new[] { ("sup.KbDocumentVersions", "Title"), ("sup.KbChunks", "Heading"), ("sup.TicketComments", "Body"), ("ai.Usage", "Status"), ("ops.AuditEntries", "Action") })
            {
                var (table, column) = appendOnly;
                var update = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.App, $"UPDATE {table} SET {column} = {column} WHERE 1 = 0;"));
                Assert.Equal(229, update.Number);
                var delete = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.App, $"DELETE FROM {table} WHERE 1 = 0;"));
                Assert.Equal(229, delete.Number);
            }

            Assert.Equal(1L, Assert.Single(await QueryAsync(database.App, "SELECT NEXT VALUE FOR sup.TicketNumbers;"))[0]);
            var permissions = await AppPermissionsAsync(database.Migrator);
            Assert.Contains("ops OutboxMessages INSERT GRANT", permissions);
            Assert.Contains("sup KbDocumentVersions UPDATE DENY", permissions);
            Assert.Contains("ai Answers DELETE GRANT", permissions);
            Assert.DoesNotContain(permissions, p => p.Contains(" ALTER ", StringComparison.Ordinal) || p.Contains(" CONTROL ", StringComparison.Ordinal));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Migrating_an_up_to_date_database_again_changes_nothing()
    {
        var database = await fixture.NewDatabaseAsync(Suffix("twice"));
        try
        {
            await MigrateAsync(database.Migrator);
            await ExecuteAsync(
                database.App, "INSERT INTO md.Warehouses (Id, Code, Name, IsActive, CreatedAt) VALUES (NEWID(), 'W-IKI-1', N'İkinci çalıştırma', 1, SYSUTCDATETIME());");
            const string objectsSql =
                """
                SELECT o.object_id, s.name, o.name, o.type, o.create_date, o.modify_date
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'SO', 'PK', 'UQ', 'F', 'C', 'D');
                """;
            var history = await HistoryAsync(database.Migrator);
            var objects = await LinesAsync(database.Migrator, objectsSql);
            var permissions = await AppPermissionsAsync(database.Migrator);
            var warehouse = await QueryAsync(database.Migrator, "SELECT Id, Name, RowVersion FROM md.Warehouses WHERE Code = 'W-IKI-1';");

            await MigrateAsync(database.Migrator);
            await MigrateAsync(database.Migrator);

            Assert.Equal(history, await HistoryAsync(database.Migrator));
            Assert.Equal(objects, await LinesAsync(database.Migrator, objectsSql));
            Assert.Equal(permissions, await AppPermissionsAsync(database.Migrator));
            var warehouseAfter = await QueryAsync(database.Migrator, "SELECT Id, Name, RowVersion FROM md.Warehouses WHERE Code = 'W-IKI-1';");
            Assert.Equal(warehouse.Single()[0], warehouseAfter.Single()[0]);
            Assert.Equal((byte[])warehouse.Single()[2]!, (byte[])warehouseAfter.Single()[2]!);

            // Still usable, still restricted.
            Assert.Equal(1, await ExecuteAsync(database.App, "UPDATE md.Warehouses SET Name = N'Üçüncü ad' WHERE Code = 'W-IKI-1';"));
            var denied = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.App, "UPDATE inv.StockMovements SET QuantityDelta = QuantityDelta WHERE 1 = 0;"));
            Assert.Equal(229, denied.Number);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task Migrating_to_an_unknown_target_fails_and_leaves_the_database_as_it_was()
    {
        var database = await fixture.NewDatabaseAsync(Suffix("target"));
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(database.Migrator, "20990101000000_BoyleBirSeyYok"));

            Assert.Empty(await TablesAsync(database.Migrator));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [Theory]
    [InlineData("x; DROP DATABASE y")]
    [InlineData("Kantar")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("kantar test")]
    [InlineData("kantar-test")]
    [InlineData("1kantar")]
    [InlineData("_kantar")]
    [InlineData("kantar]; DROP DATABASE master; --")]
    [InlineData("kantar'; DROP DATABASE master; --")]
    [InlineData("[kantar]")]
    [InlineData("kantar.dbo")]
    [InlineData("kantarçğ")]
    [InlineData("kantar\n")]
    [InlineData("kantar_0123456789012345678901234567890123456789012345678901234567")]
    public async Task Bootstrap_refuses_a_database_name_outside_its_pattern_before_touching_the_server(string name)
    {
        var before = Assert.Single(await QueryAsync(fixture.SysadminConnectionString, "SELECT COUNT(*) FROM sys.databases;"))[0];

        // An unreachable server proves the name is rejected before any connection is opened.
        const string unreachable = "Server=127.0.0.1,1;User ID=sa;Password=kullanilmayan-deger;Connect Timeout=1;Encrypt=False";   // kantar:allow-test-secret (dummy value; nothing listens on that port)
        var offline = await Assert.ThrowsAsync<ArgumentException>(() =>
            DatabaseBootstrapper.BootstrapAsync(unreachable, name, KantarFixture.NewPassword(), KantarFixture.NewPassword(), CancellationToken.None));
        Assert.Equal("databaseName", offline.ParamName);

        // And against the real server nothing is created.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DatabaseBootstrapper.BootstrapAsync(fixture.SysadminConnectionString, name, KantarFixture.NewPassword(), KantarFixture.NewPassword(), CancellationToken.None));
        Assert.Equal(before, Assert.Single(await QueryAsync(fixture.SysadminConnectionString, "SELECT COUNT(*) FROM sys.databases;"))[0]);
    }
}
