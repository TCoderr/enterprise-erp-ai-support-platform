using Kantar.Api.Http;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.Purchasing;
using Kantar.Application.Sales;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;

namespace Kantar.Api.Endpoints;

// Request bodies list exactly what a caller may send. Prices, snapshots, status, owner and
// numbers are not here, so sending them is an unknown-field error rather than a silent override.

public sealed record SaveSalesOrderRequest(Guid CustomerId, Guid WarehouseId, IReadOnlyList<SalesLineInput> Lines, string? Note = null);

public sealed record CancelRequest(string Reason);

public sealed record ShipRequest(string DispatchReference, IReadOnlyList<FulfilmentLineInput> Lines);

public sealed record SavePurchaseOrderRequest(Guid SupplierId, Guid WarehouseId, IReadOnlyList<PurchaseLineInput> Lines, string? Note = null);

public sealed record ReceiveRequest(string ExternalReference, IReadOnlyList<FulfilmentLineInput> Lines);

public static class DocumentEndpoints
{
    private static string? IfMatchHeader(HttpContext context)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        return string.IsNullOrEmpty(header) ? null : header;
    }

    public static void MapSales(RouteGroupBuilder api)
    {
        var sales = api.MapGroup("/sales/orders").WithTags("Sales");

        sales.MapGet("", async (HttpContext context, SalesService service) =>
        {
            context.AllowQuery("warehouseId", "status", "mine", "limit", "cursor");
            await context.WriteJsonAsync(await service.ListAsync(
                context.Caller(), context.QueryGuid("warehouseId"), context.QueryEnum<SalesOrderStatus>("status"), context.QueryBool("mine") ?? false,
                context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted));
        }).Produces<Page<SalesOrderListItemDto>>();

        sales.MapPost("", async (HttpContext context, SalesService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<SaveSalesOrderRequest>(context);
            var command = SalesService.Normalize(new SaveSalesOrderCommand(null, body.CustomerId, body.WarehouseId, body.Note, body.Lines, null));
            var outcome = await executor.RunAsync(
                service.Create, command, context.Caller(), context.Idempotency("sales.order.create", command),
                o => new ResponseShape(StatusCodes.Status201Created, o, $"/api/sales/orders/{o.Id}", o.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<SaveSalesOrderRequest>("application/json").Produces<SalesOrderDto>(StatusCodes.Status201Created);

        sales.MapGet("/{id:guid}", async (Guid id, HttpContext context, SalesService service) =>
        {
            context.AllowQuery();
            var order = await service.GetAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(order, etag: order.ETag);
        }).Produces<SalesOrderDto>();

        sales.MapPut("/{id:guid}", async (Guid id, HttpContext context, SalesService service, IUnitOfWork unitOfWork) =>
        {
            var body = await JsonBody.ReadAsync<SaveSalesOrderRequest>(context);
            var command = SalesService.Normalize(new SaveSalesOrderCommand(id, body.CustomerId, body.WarehouseId, body.Note, body.Lines, null));
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var order = await unitOfWork.ExecuteAsync(ct => service.UpdateDraftAsync(command, caller, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(order, etag: order.ETag);
        }).Accepts<SaveSalesOrderRequest>("application/json").Produces<SalesOrderDto>();

        sales.MapPost("/{id:guid}/confirm", async (Guid id, HttpContext context, SalesService service, CommandExecutor executor) =>
        {
            var command = new SalesOrderActionCommand(id, null, IfMatchHeader(context));
            var outcome = await executor.RunAsync(
                service.Confirm, command, context.Caller(), context.Idempotency("sales.order.confirm", command),
                o => new ResponseShape(StatusCodes.Status200OK, o, ETag: o.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Produces<SalesOrderDto>();

        sales.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext context, SalesService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<CancelRequest>(context);
            var command = SalesService.NormalizeCancel(new SalesOrderActionCommand(id, body.Reason, IfMatchHeader(context)));
            var outcome = await executor.RunAsync(
                service.Cancel, command, context.Caller(), context.Idempotency("sales.order.cancel", command),
                o => new ResponseShape(StatusCodes.Status200OK, o, ETag: o.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CancelRequest>("application/json").Produces<SalesOrderDto>();

        sales.MapPost("/{id:guid}/shipments", async (Guid id, HttpContext context, SalesService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<ShipRequest>(context);
            var command = SalesService.Normalize(new ShipCommand(id, body.DispatchReference, body.Lines, IfMatchHeader(context)));
            var outcome = await executor.RunAsync(
                service.Ship, command, context.Caller(), context.Idempotency("sales.order.ship", command),
                s => new ResponseShape(StatusCodes.Status201Created, s, $"/api/sales/orders/{id}/shipments/{s.Id}", s.SalesOrderETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<ShipRequest>("application/json").Produces<ShipmentDto>(StatusCodes.Status201Created);

        sales.MapGet("/{id:guid}/shipments/{shipmentId:guid}", async (Guid id, Guid shipmentId, HttpContext context, SalesService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.GetShipmentAsync(context.Caller(), id, shipmentId, context.RequestAborted));
        }).Produces<ShipmentDto>();

        sales.MapGet("/{id:guid}/summary", async (Guid id, HttpContext context, SalesService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.SummaryAsync(context.Caller(), id, context.RequestAborted));
        }).Produces<SalesOrderSummaryDto>();
    }

    public static void MapPurchasing(RouteGroupBuilder api)
    {
        var purchasing = api.MapGroup("/purchasing/orders").WithTags("Purchasing");

        purchasing.MapGet("", async (HttpContext context, PurchasingService service) =>
        {
            context.AllowQuery("warehouseId", "status", "mine", "limit", "cursor");
            await context.WriteJsonAsync(await service.ListAsync(
                context.Caller(), context.QueryGuid("warehouseId"), context.QueryEnum<PurchaseOrderStatus>("status"), context.QueryBool("mine") ?? false,
                context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted));
        }).Produces<Page<PurchaseOrderListItemDto>>();

        purchasing.MapPost("", async (HttpContext context, PurchasingService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<SavePurchaseOrderRequest>(context);
            var command = PurchasingService.Normalize(new SavePurchaseOrderCommand(null, body.SupplierId, body.WarehouseId, body.Note, body.Lines, null));
            var outcome = await executor.RunAsync(
                service.Create, command, context.Caller(), context.Idempotency("purchasing.order.create", command),
                o => new ResponseShape(StatusCodes.Status201Created, o, $"/api/purchasing/orders/{o.Id}", o.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<SavePurchaseOrderRequest>("application/json").Produces<PurchaseOrderDto>(StatusCodes.Status201Created);

        purchasing.MapGet("/{id:guid}", async (Guid id, HttpContext context, PurchasingService service) =>
        {
            context.AllowQuery();
            var order = await service.GetAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(order, etag: order.ETag);
        }).Produces<PurchaseOrderDto>();

        purchasing.MapPut("/{id:guid}", async (Guid id, HttpContext context, PurchasingService service, IUnitOfWork unitOfWork) =>
        {
            var body = await JsonBody.ReadAsync<SavePurchaseOrderRequest>(context);
            var command = PurchasingService.Normalize(new SavePurchaseOrderCommand(id, body.SupplierId, body.WarehouseId, body.Note, body.Lines, null));
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var order = await unitOfWork.ExecuteAsync(ct => service.UpdateDraftAsync(command, caller, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(order, etag: order.ETag);
        }).Accepts<SavePurchaseOrderRequest>("application/json").Produces<PurchaseOrderDto>();

        purchasing.MapPost("/{id:guid}/approve", async (Guid id, HttpContext context, PurchasingService service, CommandExecutor executor) =>
        {
            var command = new PurchaseOrderActionCommand(id, null, IfMatchHeader(context));
            var outcome = await executor.RunAsync(
                service.Approve, command, context.Caller(), context.Idempotency("purchasing.order.approve", command),
                o => new ResponseShape(StatusCodes.Status200OK, o, ETag: o.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Produces<PurchaseOrderDto>();

        purchasing.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext context, PurchasingService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<CancelRequest>(context);
            var command = PurchasingService.NormalizeCancel(new PurchaseOrderActionCommand(id, body.Reason, IfMatchHeader(context)));
            var outcome = await executor.RunAsync(
                service.Cancel, command, context.Caller(), context.Idempotency("purchasing.order.cancel", command),
                o => new ResponseShape(StatusCodes.Status200OK, o, ETag: o.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CancelRequest>("application/json").Produces<PurchaseOrderDto>();

        purchasing.MapPost("/{id:guid}/receipts", async (Guid id, HttpContext context, PurchasingService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<ReceiveRequest>(context);
            var command = PurchasingService.Normalize(new ReceiveCommand(id, body.ExternalReference, body.Lines, IfMatchHeader(context)));
            var outcome = await executor.RunAsync(
                service.Receive, command, context.Caller(), context.Idempotency("purchasing.order.receive", command),
                r => new ResponseShape(StatusCodes.Status201Created, r, $"/api/purchasing/orders/{id}/receipts/{r.Id}", r.PurchaseOrderETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<ReceiveRequest>("application/json").Produces<ReceiptDto>(StatusCodes.Status201Created);

        purchasing.MapGet("/{id:guid}/receipts/{receiptId:guid}", async (Guid id, Guid receiptId, HttpContext context, PurchasingService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.GetReceiptAsync(context.Caller(), id, receiptId, context.RequestAborted));
        }).Produces<ReceiptDto>();
    }
}
