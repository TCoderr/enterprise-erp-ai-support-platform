using Kantar.Api.Http;
using Kantar.Application.Common;
using Kantar.Application.Inventory;
using Kantar.Domain.Common;
using Kantar.Domain.Inventory;

namespace Kantar.Api.Endpoints;

public sealed record CreateTransferRequest(string Reference, Guid FromWarehouseId, Guid ToWarehouseId, IReadOnlyList<TransferLineInput> Lines, string? Note = null);

public sealed record CreateAdjustmentRequest(string Reference, Guid WarehouseId, string ReasonCode, string Note, IReadOnlyList<AdjustmentLineInput> Lines);

public static class InventoryEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var inventory = api.MapGroup("/inventory").WithTags("Inventory");

        inventory.MapGet("/balances", async (HttpContext context, InventoryService service) =>
        {
            context.AllowQuery("warehouseId", "search", "lowStockOnly", "limit", "cursor");
            var page = await service.BalancesAsync(
                context.Caller(), RequireWarehouse(context), context.QueryString("search"), context.QueryBool("lowStockOnly") ?? false,
                context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted);
            await context.WriteJsonAsync(page);
        }).Produces<Page<BalanceDto>>();

        inventory.MapGet("/movements", async (HttpContext context, InventoryService service) =>
        {
            context.AllowQuery("warehouseId", "productId", "type", "from", "to", "limit", "cursor");
            var page = await service.MovementsAsync(
                context.Caller(), RequireWarehouse(context), context.QueryGuid("productId"), context.QueryEnum<MovementType>("type"),
                QueryUtc(context, "from"), QueryUtc(context, "to"), context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted);
            await context.WriteJsonAsync(page);
        }).Produces<Page<MovementDto>>();

        inventory.MapGet("/reservations", async (HttpContext context, InventoryService service) =>
        {
            context.AllowQuery("warehouseId", "productId", "limit", "cursor");
            var page = await service.ReservationsAsync(
                context.Caller(), RequireWarehouse(context), context.QueryGuid("productId"),
                context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted);
            await context.WriteJsonAsync(page);
        }).Produces<Page<ReservationDto>>();

        inventory.MapPost("/transfers", async (HttpContext context, InventoryService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<CreateTransferRequest>(context);
            var command = InventoryService.Normalize(new CreateTransferCommand(body.Reference, body.FromWarehouseId, body.ToWarehouseId, body.Note, body.Lines));
            var outcome = await executor.RunAsync(
                service.CreateTransfer, command, context.Caller(), context.Idempotency("inventory.transfer", command),
                t => new ResponseShape(StatusCodes.Status201Created, t, $"/api/inventory/transfers/{t.Id}"), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CreateTransferRequest>("application/json").Produces<TransferDto>(StatusCodes.Status201Created);

        inventory.MapGet("/transfers", async (HttpContext context, InventoryService service) =>
        {
            context.AllowQuery("warehouseId", "limit", "cursor");
            await context.WriteJsonAsync(await service.ListTransfersAsync(
                context.Caller(), RequireWarehouse(context), context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted));
        }).Produces<Page<TransferDto>>();

        inventory.MapGet("/transfers/{id:guid}", async (Guid id, HttpContext context, InventoryService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.GetTransferAsync(context.Caller(), id, context.RequestAborted));
        }).Produces<TransferDto>();

        inventory.MapPost("/adjustments", async (HttpContext context, InventoryService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<CreateAdjustmentRequest>(context);
            var command = InventoryService.Normalize(new CreateAdjustmentCommand(body.Reference, body.WarehouseId, body.ReasonCode, body.Note, body.Lines));
            var outcome = await executor.RunAsync(
                service.CreateAdjustment, command, context.Caller(), context.Idempotency("inventory.adjustment", command),
                a => new ResponseShape(StatusCodes.Status201Created, a, $"/api/inventory/adjustments/{a.Id}"), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CreateAdjustmentRequest>("application/json").Produces<AdjustmentDto>(StatusCodes.Status201Created);

        inventory.MapGet("/adjustments", async (HttpContext context, InventoryService service) =>
        {
            context.AllowQuery("warehouseId", "limit", "cursor");
            await context.WriteJsonAsync(await service.ListAdjustmentsAsync(
                context.Caller(), RequireWarehouse(context), context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted));
        }).Produces<Page<AdjustmentDto>>();

        inventory.MapGet("/adjustments/{id:guid}", async (Guid id, HttpContext context, InventoryService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.GetAdjustmentAsync(context.Caller(), id, context.RequestAborted));
        }).Produces<AdjustmentDto>();
    }

    /// <summary>Stock is always read for one named warehouse; there is no "all warehouses" query.</summary>
    private static Guid RequireWarehouse(HttpContext context) =>
        context.QueryGuid("warehouseId") ?? throw Problems.Validation("warehouseId", "required", "warehouseId zorunludur.");

    private static DateTime? QueryUtc(HttpContext context, string name)
    {
        var raw = context.QueryString(name);
        if (raw is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : throw Problems.Validation(name, "invalid", "ISO 8601 tarih bekleniyor.");
    }
}
