using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.MasterData;

/// <summary>ListPrice is null (and therefore absent from the JSON) for callers who may not read prices.</summary>
public sealed record ProductDto(Guid Id, string Sku, string Name, string Unit, decimal? ListPrice, int ReorderLevel, bool IsActive, string ETag);

public sealed record CustomerDto(Guid Id, string Code, string Name, string TaxNumber, string Address, bool IsActive, string ETag);

public sealed record SupplierDto(Guid Id, string Code, string Name, bool IsActive, string ETag);

public sealed record WarehouseDto(Guid Id, string Code, string Name, bool IsActive, string ETag);

public sealed record ProductInput(string Sku, string Name, string Unit, decimal ListPrice, int ReorderLevel);

public sealed record CustomerInput(string Code, string Name, string TaxNumber, string Address);

public sealed record SupplierInput(string Code, string Name);

public sealed record WarehouseInput(string Code, string Name);

public sealed class MasterDataService(IKantarDb db, TimeProvider clock, AuditWriter audit)
{
    private static readonly HashSet<string> Units = new(StringComparer.Ordinal) { "ADET", "KOLI", "PAKET", "METRE", "KG", "TOP" };

    private static Task RequireManage(CurrentUser user)
    {
        if (!AccessPolicy.CanManageMasterData(user))
        {
            throw Problems.Forbidden();
        }

        return Task.CompletedTask;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- products

    public static ProductInput Normalize(ProductInput input)
    {
        var v = new Validator();
        var sku = v.Code("sku", input.Sku, 3, 32);
        var name = v.Text("name", input.Name, 2, 160);
        var unit = Text.NormalizeCode(input.Unit ?? string.Empty);
        if (!Units.Contains(unit))
        {
            v.Add("unit", "unknown_unit", "Birim şunlardan biri olmalıdır: " + string.Join(", ", Units.Order(StringComparer.Ordinal)));
        }

        v.Money("listPrice", input.ListPrice, 99_999_999.99m);
        v.Range("reorderLevel", input.ReorderLevel, 0, 1_000_000);
        v.ThrowIfInvalid();
        return new ProductInput(sku, name, unit, input.ListPrice, input.ReorderLevel);
    }

    public async Task<Page<ProductDto>> ListProductsAsync(CurrentUser user, string? search, bool? active, int? limit, string? cursor, CancellationToken ct)
    {
        var take = Guard.Limit(limit);
        var query = db.Products.AsNoTracking();
        if (active is { } isActive)
        {
            query = query.Where(p => p.IsActive == isActive);
        }

        if (Guard.Search(search) is { } term)
        {
            var code = term.ToUpperInvariant();
            query = query.Where(p => p.Sku.StartsWith(code) || p.Name.Contains(term));
        }

        // SKU is unique, so it is a total order on its own; the cursor is the last SKU of the previous page.
        if (cursor is not null)
        {
            var after = DecodeCode(cursor);
            // Translated to a SQL comparison on the binary-collated SKU column; the overload with a
            // StringComparison argument is not translatable.
#pragma warning disable CA1309, CA1310
            query = query.Where(p => string.Compare(p.Sku, after) > 0);
#pragma warning restore CA1309, CA1310
        }

        var rows = await query.OrderBy(p => p.Sku).Take(take + 1).ToListAsync(ct);
        var showPrice = AccessPolicy.CanReadPrices(user);
        var next = rows.Count > take ? EncodeCode(rows[take - 1].Sku) : null;
        return new Page<ProductDto>(rows.Take(take).Select(p => ToDto(p, showPrice)).ToArray(), next);
    }

    public async Task<ProductDto> GetProductAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw Problems.NotFound();
        return ToDto(product, AccessPolicy.CanReadPrices(user));
    }

    public ICommandHandler<ProductInput, ProductDto> CreateProduct => new DelegateHandler<ProductInput, ProductDto>(
        (_, user, _) => RequireManage(user),
        async (input, user, ct) =>
        {
            if (await db.Products.AnyAsync(p => p.Sku == input.Sku, ct))
            {
                throw Problems.Conflict("code_taken", "Bu SKU kullanılıyor.");
            }

            var product = new Product
            {
                Sku = input.Sku, Name = input.Name, Unit = input.Unit, ListPrice = input.ListPrice,
                ReorderLevel = input.ReorderLevel, CreatedAt = Now,
            };
            db.Products.Add(product);
            audit.Write(user, "masterdata.product_created", "Product", product.Id);
            await db.SaveChangesAsync(ct);
            return ToDto(product, showPrice: true);
        });

    public async Task<ProductDto> UpdateProductAsync(CurrentUser user, Guid id, ProductInput input, byte[]? expected, CancellationToken ct)
    {
        await RequireManage(user);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw Problems.NotFound();
        Guard.Precondition(product.RowVersion, expected);

        if (product.Sku != input.Sku || product.Unit != input.Unit)
        {
            var used = await db.SalesOrderLines.AnyAsync(l => l.ProductId == id, ct)
                || await db.PurchaseOrderLines.AnyAsync(l => l.ProductId == id, ct)
                || await db.StockMovements.AnyAsync(m => m.ProductId == id, ct);
            if (used)
            {
                throw Problems.Conflict("immutable_field", "Belge veya hareketi olan ürünün SKU ve birimi değiştirilemez.");
            }

            if (product.Sku != input.Sku && await db.Products.AnyAsync(p => p.Sku == input.Sku, ct))
            {
                throw Problems.Conflict("code_taken", "Bu SKU kullanılıyor.");
            }
        }

        var priceChanged = product.ListPrice != input.ListPrice;
        (product.Sku, product.Name, product.Unit, product.ListPrice, product.ReorderLevel) =
            (input.Sku, input.Name, input.Unit, input.ListPrice, input.ReorderLevel);
        audit.Write(user, "masterdata.product_updated", "Product", product.Id, detail: new { priceChanged });
        await db.SaveChangesAsync(ct);
        return ToDto(product, showPrice: true);
    }

    public async Task<ProductDto> SetProductActiveAsync(CurrentUser user, Guid id, bool active, CancellationToken ct)
    {
        await RequireManage(user);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw Problems.NotFound();
        product.IsActive = active;
        audit.Write(user, active ? "masterdata.product_activated" : "masterdata.product_deactivated", "Product", id);
        await db.SaveChangesAsync(ct);
        return ToDto(product, showPrice: true);
    }

    private static ProductDto ToDto(Product p, bool showPrice) =>
        new(p.Id, p.Sku, p.Name, p.Unit, showPrice ? p.ListPrice : null, p.ReorderLevel, p.IsActive, ETags.From(p.RowVersion));

    // ---------------------------------------------------------------- customers

    public static CustomerInput Normalize(CustomerInput input)
    {
        var v = new Validator();
        var code = v.Code("code", input.Code, 2, 32);
        var name = v.Text("name", input.Name, 2, 160);
        var tax = v.Text("taxNumber", input.TaxNumber, 10, 11);
        if (tax.Any(c => c is < '0' or > '9'))
        {
            v.Add("taxNumber", "invalid", "Vergi numarası yalnız rakamlardan oluşmalıdır.");
        }

        var address = v.Text("address", input.Address, 5, 400, multiline: true);
        v.ThrowIfInvalid();
        return new CustomerInput(code, name, tax, address);
    }

    public async Task<IReadOnlyList<CustomerDto>> ListCustomersAsync(CurrentUser user, bool? active, CancellationToken ct)
    {
        if (!AccessPolicy.CanReadCustomers(user))
        {
            throw Problems.Forbidden();
        }

        var query = db.Customers.AsNoTracking();
        if (active is { } isActive)
        {
            query = query.Where(c => c.IsActive == isActive);
        }

        return (await query.OrderBy(c => c.Code).Take(500).ToListAsync(ct)).Select(ToDto).ToArray();
    }

    public async Task<CustomerDto> GetCustomerAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        if (!AccessPolicy.CanReadCustomers(user))
        {
            throw Problems.Forbidden();
        }

        return ToDto(await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw Problems.NotFound());
    }

    public ICommandHandler<CustomerInput, CustomerDto> CreateCustomer => new DelegateHandler<CustomerInput, CustomerDto>(
        (_, user, _) => RequireManage(user),
        async (input, user, ct) =>
        {
            if (await db.Customers.AnyAsync(c => c.Code == input.Code, ct))
            {
                throw Problems.Conflict("code_taken", "Bu müşteri kodu kullanılıyor.");
            }

            var customer = new Customer { Code = input.Code, Name = input.Name, TaxNumber = input.TaxNumber, Address = input.Address, CreatedAt = Now };
            db.Customers.Add(customer);
            audit.Write(user, "masterdata.customer_created", "Customer", customer.Id);
            await db.SaveChangesAsync(ct);
            return ToDto(customer);
        });

    public async Task<CustomerDto> UpdateCustomerAsync(CurrentUser user, Guid id, CustomerInput input, byte[]? expected, CancellationToken ct)
    {
        await RequireManage(user);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw Problems.NotFound();
        Guard.Precondition(customer.RowVersion, expected);
        if (customer.Code != input.Code)
        {
            throw Problems.Conflict("immutable_field", "Müşteri kodu değiştirilemez.");
        }

        (customer.Name, customer.TaxNumber, customer.Address) = (input.Name, input.TaxNumber, input.Address);
        audit.Write(user, "masterdata.customer_updated", "Customer", id);
        await db.SaveChangesAsync(ct);
        return ToDto(customer);
    }

    public async Task<CustomerDto> SetCustomerActiveAsync(CurrentUser user, Guid id, bool active, CancellationToken ct)
    {
        await RequireManage(user);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw Problems.NotFound();
        customer.IsActive = active;
        audit.Write(user, active ? "masterdata.customer_activated" : "masterdata.customer_deactivated", "Customer", id);
        await db.SaveChangesAsync(ct);
        return ToDto(customer);
    }

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.Code, c.Name, c.TaxNumber, c.Address, c.IsActive, ETags.From(c.RowVersion));

    // ---------------------------------------------------------------- suppliers

    public static SupplierInput Normalize(SupplierInput input)
    {
        var v = new Validator();
        var code = v.Code("code", input.Code, 2, 32);
        var name = v.Text("name", input.Name, 2, 160);
        v.ThrowIfInvalid();
        return new SupplierInput(code, name);
    }

    public async Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(CurrentUser user, bool? active, CancellationToken ct)
    {
        if (!AccessPolicy.CanReadSuppliers(user))
        {
            throw Problems.Forbidden();
        }

        var query = db.Suppliers.AsNoTracking();
        if (active is { } isActive)
        {
            query = query.Where(s => s.IsActive == isActive);
        }

        return (await query.OrderBy(s => s.Code).Take(500).ToListAsync(ct)).Select(ToDto).ToArray();
    }

    public ICommandHandler<SupplierInput, SupplierDto> CreateSupplier => new DelegateHandler<SupplierInput, SupplierDto>(
        (_, user, _) => RequireManage(user),
        async (input, user, ct) =>
        {
            if (await db.Suppliers.AnyAsync(s => s.Code == input.Code, ct))
            {
                throw Problems.Conflict("code_taken", "Bu tedarikçi kodu kullanılıyor.");
            }

            var supplier = new Supplier { Code = input.Code, Name = input.Name, CreatedAt = Now };
            db.Suppliers.Add(supplier);
            audit.Write(user, "masterdata.supplier_created", "Supplier", supplier.Id);
            await db.SaveChangesAsync(ct);
            return ToDto(supplier);
        });

    public async Task<SupplierDto> GetSupplierAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        if (!AccessPolicy.CanReadSuppliers(user))
        {
            throw Problems.Forbidden();
        }

        return ToDto(await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw Problems.NotFound());
    }

    public async Task<SupplierDto> UpdateSupplierAsync(CurrentUser user, Guid id, SupplierInput input, byte[]? expected, CancellationToken ct)
    {
        await RequireManage(user);
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw Problems.NotFound();
        Guard.Precondition(supplier.RowVersion, expected);
        if (supplier.Code != input.Code)
        {
            throw Problems.Conflict("immutable_field", "Tedarikçi kodu değiştirilemez.");
        }

        supplier.Name = input.Name;
        audit.Write(user, "masterdata.supplier_updated", "Supplier", id);
        await db.SaveChangesAsync(ct);
        return ToDto(supplier);
    }

    public async Task<SupplierDto> SetSupplierActiveAsync(CurrentUser user, Guid id, bool active, CancellationToken ct)
    {
        await RequireManage(user);
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw Problems.NotFound();
        supplier.IsActive = active;
        audit.Write(user, active ? "masterdata.supplier_activated" : "masterdata.supplier_deactivated", "Supplier", id);
        await db.SaveChangesAsync(ct);
        return ToDto(supplier);
    }

    private static SupplierDto ToDto(Supplier s) => new(s.Id, s.Code, s.Name, s.IsActive, ETags.From(s.RowVersion));

    // ---------------------------------------------------------------- warehouses

    public static WarehouseInput Normalize(WarehouseInput input)
    {
        var v = new Validator();
        var code = v.Code("code", input.Code, 2, 16);
        var name = v.Text("name", input.Name, 2, 120);
        v.ThrowIfInvalid();
        return new WarehouseInput(code, name);
    }

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(CancellationToken ct) =>
        (await db.Warehouses.AsNoTracking().OrderBy(w => w.Code).ToListAsync(ct)).Select(ToDto).ToArray();

    public ICommandHandler<WarehouseInput, WarehouseDto> CreateWarehouse => new DelegateHandler<WarehouseInput, WarehouseDto>(
        (_, user, _) => RequireManage(user),
        async (input, user, ct) =>
        {
            if (await db.Warehouses.AnyAsync(w => w.Code == input.Code, ct))
            {
                throw Problems.Conflict("code_taken", "Bu depo kodu kullanılıyor.");
            }

            var warehouse = new Warehouse { Code = input.Code, Name = input.Name, CreatedAt = Now };
            db.Warehouses.Add(warehouse);
            audit.Write(user, "masterdata.warehouse_created", "Warehouse", warehouse.Id);
            await db.SaveChangesAsync(ct);
            return ToDto(warehouse);
        });

    public async Task<WarehouseDto> GetWarehouseAsync(Guid id, CancellationToken ct) =>
        ToDto(await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw Problems.NotFound());

    public async Task<WarehouseDto> UpdateWarehouseAsync(CurrentUser user, Guid id, WarehouseInput input, byte[]? expected, CancellationToken ct)
    {
        await RequireManage(user);
        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw Problems.NotFound();
        Guard.Precondition(warehouse.RowVersion, expected);
        if (warehouse.Code != input.Code)
        {
            throw Problems.Conflict("immutable_field", "Depo kodu değiştirilemez.");
        }

        warehouse.Name = input.Name;
        audit.Write(user, "masterdata.warehouse_updated", "Warehouse", id);
        await db.SaveChangesAsync(ct);
        return ToDto(warehouse);
    }

    public async Task<WarehouseDto> SetWarehouseActiveAsync(CurrentUser user, Guid id, bool active, CancellationToken ct)
    {
        await RequireManage(user);
        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw Problems.NotFound();
        warehouse.IsActive = active;
        audit.Write(user, active ? "masterdata.warehouse_activated" : "masterdata.warehouse_deactivated", "Warehouse", id);
        await db.SaveChangesAsync(ct);
        return ToDto(warehouse);
    }

    private static WarehouseDto ToDto(Warehouse w) => new(w.Id, w.Code, w.Name, w.IsActive, ETags.From(w.RowVersion));

    private static string EncodeCode(string code) =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(code)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeCode(string cursor)
    {
        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        var buffer = new byte[64];
        if (cursor.Length > 64 || !Convert.TryFromBase64String(padded, buffer, out var written))
        {
            throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
        }

        var code = System.Text.Encoding.ASCII.GetString(buffer, 0, written);
        if (!Text.IsCode(code, 1, 32))
        {
            throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
        }

        return code;
    }
}
