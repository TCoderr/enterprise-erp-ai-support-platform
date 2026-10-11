using System.Security.Cryptography;
using Kantar.Api.Http;
using Kantar.Application.Abstractions;
using Kantar.Application.Identity;
using Kantar.Domain.Common;

namespace Kantar.Api.Endpoints;

public sealed record LoginRequest(string UserName, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CsrfResponse(string Token);

public sealed record LoginResponse(MeDto User, string CsrfToken);

public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapGet("/csrf", (HttpContext context, CsrfTokens csrf) =>
        {
            context.AllowQuery();
            if (RequestContext.CsrfSecretOf(context) is { } secret)
            {
                return context.WriteJsonAsync(new CsrfResponse(CsrfTokens.ForSession(secret)));
            }

            if (!context.Request.Cookies.TryGetValue(SessionCookies.PreLogin, out var pre) || pre is not { Length: 64 })
            {
                pre = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                context.Response.Cookies.Append(SessionCookies.PreLogin, pre, SessionCookies.Options(null, SameSiteMode.Strict));
            }

            return context.WriteJsonAsync(new CsrfResponse(csrf.ForPreLogin(pre)));
        }).WithMetadata(new AnonymousAttribute(), new AllowedDuringPasswordChangeAttribute()).Produces<CsrfResponse>();

        auth.MapPost("/login", async (HttpContext context, AuthService service, IUnitOfWork unitOfWork) =>
        {
            var request = await JsonBody.ReadAsync<LoginRequest>(context);
            if (request.UserName is not { Length: > 0 and <= 64 } || request.Password is not { Length: > 0 and <= 128 })
            {
                throw new DomainException(ErrorKind.Unauthenticated, "unauthenticated", "Kullanıcı adı veya parola hatalı.");
            }

            var result = await unitOfWork.ExecuteAsync(ct => service.LoginAsync(request.UserName, request.Password, ct), context.RequestAborted);
            if (result is null)
            {
                // One message for unknown user, wrong password, inactive and locked accounts.
                throw new DomainException(ErrorKind.Unauthenticated, "unauthenticated", "Kullanıcı adı veya parola hatalı.");
            }

            SessionCookies.SetSession(context, result.RawToken, result.ExpiresAt);
            SessionCookies.Clear(context, SessionCookies.PreLogin);
            var me = await service.MeAsync(result.User, context.RequestAborted);
            await context.WriteJsonAsync(new LoginResponse(me, CsrfTokens.ForSession(result.CsrfSecret)));
        }).WithMetadata(new AnonymousAttribute(), new PreLoginCsrfAttribute(), new AllowedDuringPasswordChangeAttribute())
            .Accepts<LoginRequest>("application/json").Produces<LoginResponse>();

        auth.MapPost("/logout", async (HttpContext context, AuthService service) =>
        {
            await service.LogoutAsync(context.Caller(), context.RequestAborted);
            SessionCookies.Clear(context, SessionCookies.Session);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }).WithMetadata(new AllowedDuringPasswordChangeAttribute());

        auth.MapGet("/me", async (HttpContext context, AuthService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.MeAsync(context.Caller(), context.RequestAborted));
        }).WithMetadata(new AllowedDuringPasswordChangeAttribute()).Produces<MeDto>();

        auth.MapPost("/change-password", async (HttpContext context, AuthService service, IUnitOfWork unitOfWork) =>
        {
            var request = await JsonBody.ReadAsync<ChangePasswordRequest>(context);
            if (request.CurrentPassword is null || request.NewPassword is null)
            {
                throw Problems.Validation("newPassword", "required", "Bu alan zorunludur.");
            }

            var caller = context.Caller();
            await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    await service.ChangePasswordAsync(caller, request.CurrentPassword, request.NewPassword, ct);
                    return true;
                },
                context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }).WithMetadata(new AllowedDuringPasswordChangeAttribute()).Accepts<ChangePasswordRequest>("application/json");
    }
}
