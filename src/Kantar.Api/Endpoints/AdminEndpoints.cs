using Kantar.Api.Http;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.Identity;

namespace Kantar.Api.Endpoints;

public sealed record CreateUserRequest(string UserName, string DisplayName, IReadOnlyList<string> Roles, decimal PoApprovalLimit = 0);

public sealed record SetRolesRequest(IReadOnlyList<string> Roles, decimal? PoApprovalLimit = null);

public sealed record SetGrantsRequest(IReadOnlyList<GrantDto> Grants);

public static class AdminEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin");

        admin.MapGet("/users", async (HttpContext context, AdminService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.ListAsync(context.Caller(), context.RequestAborted));
        }).Produces<IReadOnlyList<AdminUserDto>>();

        admin.MapGet("/users/{id:guid}", async (Guid id, HttpContext context, AdminService service) =>
        {
            context.AllowQuery();
            var user = await service.GetAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(user, etag: user.ETag);
        }).Produces<AdminUserDto>();

        // Not idempotency-keyed on purpose: the response carries a temporary password and must not be stored.
        admin.MapPost("/users", async (HttpContext context, AdminService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<CreateUserRequest>(context);
            var command = AdminService.Normalize(new CreateUserCommand(body.UserName, body.DisplayName, body.Roles, body.PoApprovalLimit));
            var outcome = await executor.RunAsync(
                service.CreateUser,
                command,
                context.Caller(),
                idempotency: null,
                created => new ResponseShape(StatusCodes.Status201Created, created, $"/api/admin/users/{created.User.Id}", created.User.ETag),
                context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CreateUserRequest>("application/json").Produces<TemporaryPasswordDto>(StatusCodes.Status201Created);

        admin.MapPut("/users/{id:guid}/roles", async (Guid id, HttpContext context, AdminService service, IUnitOfWork unitOfWork) =>
        {
            var body = await JsonBody.ReadAsync<SetRolesRequest>(context);
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var user = await unitOfWork.ExecuteAsync(ct => service.SetRolesAsync(caller, id, body.Roles, body.PoApprovalLimit, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(user, etag: user.ETag);
        }).Accepts<SetRolesRequest>("application/json").Produces<AdminUserDto>();

        admin.MapPut("/users/{id:guid}/warehouse-grants", async (Guid id, HttpContext context, AdminService service, IUnitOfWork unitOfWork) =>
        {
            var body = await JsonBody.ReadAsync<SetGrantsRequest>(context);
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var user = await unitOfWork.ExecuteAsync(ct => service.SetGrantsAsync(caller, id, body.Grants, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(user, etag: user.ETag);
        }).Accepts<SetGrantsRequest>("application/json").Produces<AdminUserDto>();

        admin.MapPost("/users/{id:guid}/reset-password", async (Guid id, HttpContext context, AdminService service, IUnitOfWork unitOfWork) =>
        {
            var caller = context.Caller();
            var result = await unitOfWork.ExecuteAsync(ct => service.ResetPasswordAsync(caller, id, ct), context.RequestAborted);
            await context.WriteJsonAsync(result);
        }).Produces<TemporaryPasswordDto>();

        admin.MapPost("/users/{id:guid}/deactivate", (Guid id, HttpContext context, AdminService service, IUnitOfWork unitOfWork) =>
            SetActiveAsync(id, active: false, context, service, unitOfWork)).Produces<AdminUserDto>();

        admin.MapPost("/users/{id:guid}/activate", (Guid id, HttpContext context, AdminService service, IUnitOfWork unitOfWork) =>
            SetActiveAsync(id, active: true, context, service, unitOfWork)).Produces<AdminUserDto>();

        admin.MapGet("/audit", async (HttpContext context, AdminService service) =>
        {
            context.AllowQuery("action", "entityType", "limit", "cursor");
            var page = await service.AuditAsync(
                context.Caller(),
                context.QueryString("action"),
                context.QueryString("entityType"),
                context.QueryInt("limit"),
                context.QueryString("cursor"),
                context.RequestAborted);
            await context.WriteJsonAsync(page);
        }).Produces<Page<AuditEntryDto>>();
    }

    private static async Task SetActiveAsync(Guid id, bool active, HttpContext context, AdminService service, IUnitOfWork unitOfWork)
    {
        var caller = context.Caller();
        var user = await unitOfWork.ExecuteAsync(ct => service.SetActiveAsync(caller, id, active, ct), context.RequestAborted);
        await context.WriteJsonAsync(user, etag: user.ETag);
    }
}
