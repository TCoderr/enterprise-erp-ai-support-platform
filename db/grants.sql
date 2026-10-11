-- Runtime permissions of the restricted application user (kantar_app).
-- Applied by `--migrate` after every migration; safe to run repeatedly.
-- The user is NOT a member of db_datareader/db_datawriter: everything it may do is listed here.
--
-- Kinds:
--   M  mutable            INSERT, UPDATE
--   D  mutable + cleanup  INSERT, UPDATE, DELETE
--   S  replaceable set    INSERT, DELETE            (rows are replaced, never edited)
--   A  append-only        INSERT; UPDATE and DELETE explicitly DENIED
--
-- No ALTER is granted on anything, so TRUNCATE TABLE is refused for every table.

SET NOCOUNT ON;

DECLARE @principal sysname = N'kantar_app';

IF DATABASE_PRINCIPAL_ID(@principal) IS NULL
    THROW 51000, 'Database user kantar_app does not exist; run the bootstrap step first.', 1;

DECLARE @tables TABLE (SchemaName sysname NOT NULL, TableName sysname NOT NULL, Kind char(1) NOT NULL);

INSERT INTO @tables (SchemaName, TableName, Kind) VALUES
    (N'iam',   N'Users',                  'M'),
    (N'iam',   N'UserRoles',              'S'),
    (N'iam',   N'WarehouseGrants',        'D'),
    (N'iam',   N'Sessions',               'D'),
    (N'md',    N'Products',               'M'),
    (N'md',    N'Customers',              'M'),
    (N'md',    N'Suppliers',              'M'),
    (N'md',    N'Warehouses',             'M'),
    (N'inv',   N'StockBalances',          'M'),
    (N'inv',   N'StockMovements',         'A'),
    (N'inv',   N'ReservationEntries',     'A'),
    (N'inv',   N'StockTransfers',         'A'),
    (N'inv',   N'StockTransferLines',     'A'),
    (N'inv',   N'StockAdjustments',       'A'),
    (N'inv',   N'StockAdjustmentLines',   'A'),
    (N'sales', N'SalesOrders',            'M'),
    (N'sales', N'SalesOrderLines',        'D'),
    (N'sales', N'Shipments',              'A'),
    (N'sales', N'ShipmentLines',          'A'),
    (N'purch', N'PurchaseOrders',         'M'),
    (N'purch', N'PurchaseOrderLines',     'D'),
    (N'purch', N'GoodsReceipts',          'A'),
    (N'purch', N'GoodsReceiptLines',      'A'),
    (N'sup',   N'Tickets',                'M'),
    (N'sup',   N'TicketComments',         'A'),
    (N'sup',   N'KbDocuments',            'M'),
    (N'sup',   N'KbDocumentVersions',     'A'),
    (N'sup',   N'KbChunks',               'A'),
    (N'ai',    N'Answers',                'D'),
    (N'ai',    N'Usage',                  'A'),
    (N'ops',   N'IdempotencyRecords',     'D'),
    (N'ops',   N'AuditEntries',           'A'),
    (N'ops',   N'OutboxMessages',         'D'),
    (N'ops',   N'OutboxConsumptions',     'S'),
    (N'ops',   N'Notifications',          'D'),
    (N'ops',   N'ReconciliationRuns',     'S'),
    (N'ops',   N'ReconciliationFindings', 'S');

DECLARE @schema sysname, @table sysname, @kind char(1), @object nvarchar(300), @sql nvarchar(max);

DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT SchemaName, TableName, Kind FROM @tables;
OPEN table_cursor;
FETCH NEXT FROM table_cursor INTO @schema, @table, @kind;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @object = QUOTENAME(@schema) + N'.' + QUOTENAME(@table);
    IF OBJECT_ID(@object, N'U') IS NOT NULL
    BEGIN
        -- Start from a clean slate so that a table whose kind changed does not keep an old grant.
        SET @sql = N'REVOKE SELECT, INSERT, UPDATE, DELETE ON ' + @object + N' FROM ' + QUOTENAME(@principal) + N';';
        EXEC sys.sp_executesql @sql;

        SET @sql = N'GRANT SELECT, INSERT ON ' + @object + N' TO ' + QUOTENAME(@principal) + N';';
        EXEC sys.sp_executesql @sql;

        IF @kind IN ('M', 'D')
        BEGIN
            SET @sql = N'GRANT UPDATE ON ' + @object + N' TO ' + QUOTENAME(@principal) + N';';
            EXEC sys.sp_executesql @sql;
        END;

        IF @kind IN ('D', 'S')
        BEGIN
            SET @sql = N'GRANT DELETE ON ' + @object + N' TO ' + QUOTENAME(@principal) + N';';
            EXEC sys.sp_executesql @sql;
        END;

        IF @kind = 'S'
        BEGIN
            SET @sql = N'DENY UPDATE ON ' + @object + N' TO ' + QUOTENAME(@principal) + N';';
            EXEC sys.sp_executesql @sql;
        END;

        IF @kind = 'A'
        BEGIN
            SET @sql = N'DENY UPDATE, DELETE ON ' + @object + N' TO ' + QUOTENAME(@principal) + N';';
            EXEC sys.sp_executesql @sql;
        END;
    END;

    FETCH NEXT FROM table_cursor INTO @schema, @table, @kind;
END;
CLOSE table_cursor;
DEALLOCATE table_cursor;

-- NEXT VALUE FOR needs UPDATE on the sequence object.
DECLARE sequence_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name)
    FROM sys.sequences
    WHERE SCHEMA_NAME(schema_id) IN (N'inv', N'sales', N'purch', N'sup');
OPEN sequence_cursor;
FETCH NEXT FROM sequence_cursor INTO @object;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'GRANT UPDATE ON OBJECT::' + @object + N' TO ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM sequence_cursor INTO @object;
END;
CLOSE sequence_cursor;
DEALLOCATE sequence_cursor;

-- The readiness probe compares the applied migrations with the ones compiled into the application.
IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]', N'U') IS NOT NULL
BEGIN
    SET @sql = N'GRANT SELECT ON [dbo].[__EFMigrationsHistory] TO ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END;
