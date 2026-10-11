using System.Security.Cryptography;
using System.Text;
using Kantar.Application.Identity;
using Kantar.Domain.Common;

namespace Kantar.Api.Http;

public sealed class WebSecurityOptions
{
    /// <summary>Base64 key (32 bytes or more) that signs pre-login CSRF tokens. Required outside tests.</summary>
    public string? CsrfKey { get; set; }

    /// <summary>Proxies whose X-Forwarded-For is trusted. Empty by default: the socket address is used.</summary>
    public string[] TrustedProxies { get; set; } = [];

    public int AnonymousRequestsPerMinute { get; set; } = 30;
    public int UserRequestsPerMinute { get; set; } = 600;
    public int AssistRequestsPerMinute { get; set; } = 6;
}

public static class SessionCookies
{
    public const string Session = "__Host-kantar-session";
    public const string PreLogin = "__Host-kantar-pre";
    public const string CsrfHeader = "X-CSRF-Token";

    /// <summary>__Host- prefix: Secure, Path=/, no Domain. The browser refuses the cookie otherwise.</summary>
    public static CookieOptions Options(DateTimeOffset? expires, SameSiteMode sameSite) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = sameSite,
        Path = "/",
        IsEssential = true,
        Expires = expires,
    };

    public static void SetSession(HttpContext context, string rawToken, DateTime expiresAtUtc) =>
        context.Response.Cookies.Append(Session, rawToken, Options(new DateTimeOffset(expiresAtUtc, TimeSpan.Zero), SameSiteMode.Lax));

    public static void Clear(HttpContext context, string name) =>
        context.Response.Cookies.Delete(name, Options(null, name == Session ? SameSiteMode.Lax : SameSiteMode.Strict));
}

/// <summary>
/// CSRF tokens. Authenticated: the per-session random secret stored server-side. Anonymous: an HMAC
/// over a random pre-login cookie, valid for the login request only.
/// </summary>
public sealed class CsrfTokens
{
    private readonly byte[] _key;

    public CsrfTokens(WebSecurityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CsrfKey))
        {
            throw new InvalidOperationException("Security:Web:CsrfKey is not configured.");
        }

        _key = Convert.FromBase64String(options.CsrfKey);
        if (_key.Length < 32)
        {
            throw new InvalidOperationException("Security:Web:CsrfKey must be at least 32 bytes.");
        }
    }

    public static string ForSession(byte[] csrfSecret) => Convert.ToHexString(csrfSecret);

    public string ForPreLogin(string preCookie) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes("pre:" + preCookie)));

    public static bool Matches(string expected, string? presented) =>
        presented is not null
        && presented.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(presented));
}

/// <summary>Resolves the session cookie to the caller's current database identity for every /api request.</summary>
public sealed class SessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuthService auth)
    {
        if (context.Request.Path.StartsWithSegments("/api")
            && context.Request.Cookies.TryGetValue(SessionCookies.Session, out var token)
            && !string.IsNullOrEmpty(token))
        {
            var resolved = await auth.ResolveAsync(token, context.RequestAborted);
            if (resolved is not null)
            {
                RequestContext.SetCaller(context, resolved.User, resolved.CsrfSecret);
            }
            else
            {
                // A revoked or expired cookie is removed so the browser stops presenting it.
                SessionCookies.Clear(context, SessionCookies.Session);
            }
        }

        await next(context);
    }
}

/// <summary>Marks the login endpoint: it accepts the pre-login token instead of a session token.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PreLoginCsrfAttribute : Attribute;

/// <summary>Marks endpoints that may be called without a session.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AnonymousAttribute : Attribute;

/// <summary>Marks endpoints still reachable while the caller must change a temporary password.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowedDuringPasswordChangeAttribute : Attribute;

/// <summary>
/// Runs after routing: authentication requirement, CSRF for unsafe methods and the forced
/// password change gate, in that order.
/// </summary>
public sealed class ApiGuardMiddleware(RequestDelegate next, CsrfTokens csrf)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        var caller = RequestContext.CallerOrNull(context);
        var anonymousAllowed = endpoint?.Metadata.GetMetadata<AnonymousAttribute>() is not null;

        if (endpoint is not null && caller is null && !anonymousAllowed)
        {
            throw new DomainException(ErrorKind.Unauthenticated, "unauthenticated");
        }

        var method = context.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (endpoint is not null && !safe)
        {
            var presented = context.Request.Headers[SessionCookies.CsrfHeader].ToString();
            bool valid;
            if (endpoint.Metadata.GetMetadata<PreLoginCsrfAttribute>() is not null)
            {
                valid = context.Request.Cookies.TryGetValue(SessionCookies.PreLogin, out var pre)
                    && !string.IsNullOrEmpty(pre)
                    && CsrfTokens.Matches(csrf.ForPreLogin(pre), presented);
            }
            else
            {
                valid = RequestContext.CsrfSecretOf(context) is { } secret && CsrfTokens.Matches(CsrfTokens.ForSession(secret), presented);
            }

            if (!valid)
            {
                throw Problems.Forbidden("csrf_failed");
            }
        }

        if (caller is { MustChangePassword: true }
            && endpoint is not null
            && endpoint.Metadata.GetMetadata<AllowedDuringPasswordChangeAttribute>() is null)
        {
            throw Problems.Forbidden("password_change_required");
        }

        await next(context);
    }
}

/// <summary>Response headers for every request; private API responses are never cacheable.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = Csp;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["X-Correlation-Id"] = RequestContext.CorrelationIdOf(context);

        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/health"))
        {
            headers.CacheControl = "no-store";
            headers.Pragma = "no-cache";
        }

        return next(context);
    }
}
