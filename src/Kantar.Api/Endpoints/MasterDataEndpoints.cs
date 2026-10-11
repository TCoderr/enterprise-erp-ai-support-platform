using Kantar.Api.Http;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.MasterData;

namespace Kantar.Api.Endpoints;

public static class MasterDataEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---- products ----
        var products = api.MapGroup("/products").WithTags("MasterData");

        products.MapGet("", async (HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery("search", "active", "limit", "cursor");
            var page = await service.ListProductsAsync(
                context.Caller(), context.QueryString("search"), context.QueryBool("active"),
                context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted);
            await context.WriteJsonAsync(page);
        }).Produces<Page<ProductDto>>();

        products.MapGet("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery();
            var product = await service.GetProductAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(product, etag: product.ETag);
        }).Produces<ProductDto>();

        products.MapPost("", async (HttpContext context, MasterDataService service, CommandExecutor executor) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<ProductInput>(context));
            var outcome = await executor.RunAsync(
                service.CreateProduct, input, context.Caller(), context.Idempotency("products.create", input),
                p => new ResponseShape(StatusCodes.Status201Created, p, $"/api/products/{p.Id}", p.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<ProductInput>("application/json").Produces<ProductDto>(StatusCodes.Status201Created);

        products.MapPut("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<ProductInput>(context));
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var product = await unitOfWork.ExecuteAsync(ct => service.UpdateProductAsync(caller, id, input, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(product, etag: product.ETag);
        }).Accepts<ProductInput>("application/json").Produces<ProductDto>();

        products.MapPost("/{id:guid}/deactivate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetProductActiveAsync(user, id, false, ct), p => p.ETag));
        products.MapPost("/{id:guid}/activate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetProductActiveAsync(user, id, true, ct), p => p.ETag));

        // ---- customers ----
        var customers = api.MapGroup("/customers").WithTags("MasterData");

        customers.MapGet("", async (HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery("active");
            await context.WriteJsonAsync(await service.ListCustomersAsync(context.Caller(), context.QueryBool("active"), context.RequestAborted));
        }).Produces<IReadOnlyList<CustomerDto>>();

        customers.MapGet("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery();
            var customer = await service.GetCustomerAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(customer, etag: customer.ETag);
        }).Produces<CustomerDto>();

        customers.MapPost("", async (HttpContext context, MasterDataService service, CommandExecutor executor) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<CustomerInput>(context));
            var outcome = await executor.RunAsync(
                service.CreateCustomer, input, context.Caller(), context.Idempotency("customers.create", input),
                c => new ResponseShape(StatusCodes.Status201Created, c, $"/api/customers/{c.Id}", c.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CustomerInput>("application/json").Produces<CustomerDto>(StatusCodes.Status201Created);

        customers.MapPut("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<CustomerInput>(context));
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var customer = await unitOfWork.ExecuteAsync(ct => service.UpdateCustomerAsync(caller, id, input, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(customer, etag: customer.ETag);
        }).Accepts<CustomerInput>("application/json").Produces<CustomerDto>();

        customers.MapPost("/{id:guid}/deactivate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetCustomerActiveAsync(user, id, false, ct), c => c.ETag));
        customers.MapPost("/{id:guid}/activate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetCustomerActiveAsync(user, id, true, ct), c => c.ETag));

        // ---- suppliers ----
        var suppliers = api.MapGroup("/suppliers").WithTags("MasterData");

        suppliers.MapGet("", async (HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery("active");
            await context.WriteJsonAsync(await service.ListSuppliersAsync(context.Caller(), context.QueryBool("active"), context.RequestAborted));
        }).Produces<IReadOnlyList<SupplierDto>>();

        suppliers.MapPost("", async (HttpContext context, MasterDataService service, CommandExecutor executor) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<SupplierInput>(context));
            var outcome = await executor.RunAsync(
                service.CreateSupplier, input, context.Caller(), context.Idempotency("suppliers.create", input),
                s => new ResponseShape(StatusCodes.Status201Created, s, $"/api/suppliers/{s.Id}", s.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<SupplierInput>("application/json").Produces<SupplierDto>(StatusCodes.Status201Created);

        // docs/05 §4: suppliers and warehouses follow the same read-one / update pattern as products and customers.
        suppliers.MapGet("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery();
            var supplier = await service.GetSupplierAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(supplier, etag: supplier.ETag);
        }).Produces<SupplierDto>();

        suppliers.MapPut("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<SupplierInput>(context));
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var supplier = await unitOfWork.ExecuteAsync(ct => service.UpdateSupplierAsync(caller, id, input, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(supplier, etag: supplier.ETag);
        }).Accepts<SupplierInput>("application/json").Produces<SupplierDto>();

        suppliers.MapPost("/{id:guid}/deactivate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetSupplierActiveAsync(user, id, false, ct), s => s.ETag));
        suppliers.MapPost("/{id:guid}/activate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetSupplierActiveAsync(user, id, true, ct), s => s.ETag));

        // ---- warehouses ----
        var warehouses = api.MapGroup("/warehouses").WithTags("MasterData");

        warehouses.MapGet("", async (HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.ListWarehousesAsync(context.RequestAborted));
        }).Produces<IReadOnlyList<WarehouseDto>>();

        warehouses.MapPost("", async (HttpContext context, MasterDataService service, CommandExecutor executor) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<WarehouseInput>(context));
            var outcome = await executor.RunAsync(
                service.CreateWarehouse, input, context.Caller(), context.Idempotency("warehouses.create", input),
                w => new ResponseShape(StatusCodes.Status201Created, w, $"/api/warehouses/{w.Id}", w.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<WarehouseInput>("application/json").Produces<WarehouseDto>(StatusCodes.Status201Created);

        warehouses.MapGet("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service) =>
        {
            context.AllowQuery();
            var warehouse = await service.GetWarehouseAsync(id, context.RequestAborted);
            await context.WriteJsonAsync(warehouse, etag: warehouse.ETag);
        }).Produces<WarehouseDto>();

        warehouses.MapPut("/{id:guid}", async (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
        {
            var input = MasterDataService.Normalize(await JsonBody.ReadAsync<WarehouseInput>(context));
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var warehouse = await unitOfWork.ExecuteAsync(ct => service.UpdateWarehouseAsync(caller, id, input, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(warehouse, etag: warehouse.ETag);
        }).Accepts<WarehouseInput>("application/json").Produces<WarehouseDto>();

        warehouses.MapPost("/{id:guid}/deactivate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetWarehouseActiveAsync(user, id, false, ct), w => w.ETag));
        warehouses.MapPost("/{id:guid}/activate", (Guid id, HttpContext context, MasterDataService service, IUnitOfWork unitOfWork) =>
            Toggle(context, unitOfWork, (user, ct) => service.SetWarehouseActiveAsync(user, id, true, ct), w => w.ETag));
    }

    private static async Task Toggle<T>(
        HttpContext context,
        IUnitOfWork unitOfWork,
        Func<Domain.Identity.CurrentUser, CancellationToken, Task<T>> action,
        Func<T, string> etag)
    {
        var caller = context.Caller();
        var result = await unitOfWork.ExecuteAsync(ct => action(caller, ct), context.RequestAborted);
        await context.WriteJsonAsync(result, etag: etag(result));
    }
}
