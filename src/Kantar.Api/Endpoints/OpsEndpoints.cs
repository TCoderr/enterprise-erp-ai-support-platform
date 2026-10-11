using Kantar.Api.Http;
using Kantar.Application.Common;
using Kantar.Application.Operations;

namespace Kantar.Api.Endpoints;

public static class OpsEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var ops = api.MapGroup("/ops").WithTags("Operations");

        ops.MapGet("/outbox", async (HttpContext context, OperationsService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.OutboxSummaryAsync(context.Caller(), context.RequestAborted));
        }).Produces<OutboxSummaryDto>();

        ops.MapGet("/reconciliation/latest", async (HttpContext context, OperationsService service) =>
        {
            context.AllowQuery();
            var run = await service.LatestReconciliationAsync(context.Caller(), context.RequestAborted);
            if (run is null)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await context.WriteJsonAsync(run);
        }).Produces<ReconciliationRunDto>();

        // Read-only with respect to stock: it writes a report, never a correction.
        ops.MapPost("/reconciliation/run", async (HttpContext context, OperationsService service, CommandExecutor executor) =>
        {
            var outcome = await executor.RunAsync(
                service.RunReconciliation, true, context.Caller(), context.Idempotency("ops.reconciliation.run", true),
                run => new ResponseShape(StatusCodes.Status200OK, run), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Produces<ReconciliationRunDto>();

        ops.MapGet("/metrics", async (HttpContext context, OperationsService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.MetricsAsync(context.Caller(), context.RequestAborted));
        }).Produces<MetricsDto>();

        var notifications = api.MapGroup("/notifications").WithTags("Notifications");

        notifications.MapGet("", async (HttpContext context, OperationsService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.NotificationsAsync(context.Caller(), context.RequestAborted));
        }).Produces<IReadOnlyList<NotificationDto>>();

        notifications.MapPost("/{id:guid}/read", async (Guid id, HttpContext context, OperationsService service) =>
        {
            await service.MarkNotificationReadAsync(context.Caller(), id, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
    }
}
