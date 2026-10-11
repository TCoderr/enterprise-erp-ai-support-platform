using System.Net;
using System.Threading.RateLimiting;
using Kantar.Api.Endpoints;
using Kantar.Api.Http;
using Kantar.Application.Abstractions;
using Kantar.Domain.Common;
using Kantar.Infrastructure;
using Kantar.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Api.Hosting;

public static class WebHost
{
    public const string SecretsDirectory = "/run/secrets";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        AddSecretFiles(builder.Configuration);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = JsonBody.MaxBytes * 2;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(console => console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ");

        var services = builder.Services;
        services.AddKantar(builder.Configuration);
        services.AddHttpContextAccessor();
        services.AddScoped<IRequestContext, RequestContext>();

        var web = builder.Configuration.GetSection("Security:Web").Get<WebSecurityOptions>() ?? new WebSecurityOptions();
        services.AddSingleton(web);
        services.AddSingleton<CsrfTokens>();
        services.AddOpenApi();
        AddRateLimiting(services, web);

        if (web.TrustedProxies.Length > 0)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                foreach (var proxy in web.TrustedProxies)
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            });
        }

        var app = builder.Build();

        if (web.TrustedProxies.Length > 0)
        {
            app.UseForwardedHeaders();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<ProblemMiddleware>();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                // Hashed build assets are immutable; the HTML shell is always revalidated.
                var immutable = context.Context.Request.Path.StartsWithSegments("/assets");
                context.Context.Response.Headers.CacheControl = immutable ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });

        app.UseMiddleware<SessionMiddleware>();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseMiddleware<ApiGuardMiddleware>();

        MapHealth(app);

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        var api = app.MapGroup("/api");
        AuthEndpoints.Map(api);
        AdminEndpoints.Map(api);
        MasterDataEndpoints.Map(api);
        InventoryEndpoints.Map(api);
        DocumentEndpoints.MapSales(api);
        DocumentEndpoints.MapPurchasing(api);
        SupportEndpoints.Map(api);
        OpsEndpoints.Map(api);
        api.MapFallback((HttpContext context) =>
        {
            // Body endpoints declare "application/json" for the API description, so routing does not
            // offer them to a request with another content type and that request lands here. The API
            // accepts JSON bodies only (docs/05 §1.1): such a body is 415, not "no such route".
            var request = context.Request;
            var declaresBody = request.ContentLength > 0 || !string.IsNullOrEmpty(request.ContentType);
            if (declaresBody && !JsonBody.IsJson(request.ContentType))
            {
                throw new DomainException(ErrorKind.Validation, "unsupported_media_type");
            }

            throw Problems.NotFound();
        });

        if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
        {
            app.MapFallbackToFile("index.html");
        }

        return app;
    }

    public static void AddSecretFiles(IConfigurationBuilder configuration)
    {
        // Docker secrets: one file per key, file name is the configuration key with "__" as separator.
        if (Directory.Exists(SecretsDirectory))
        {
            configuration.AddKeyPerFile(SecretsDirectory, optional: true);
        }
    }

    private static void MapHealth(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Json(new { status = "ok" }));

        // Deliberately detail-free: reachable without a session, so it says only ready or not.
        app.MapGet("/health/ready", async (KantarDbContext db, CancellationToken cancellationToken) =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var pending = await db.Database.GetPendingMigrationsAsync(timeout.Token);
                return pending.Any()
                    ? Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Json(new { status = "ready" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static void AddRateLimiting(IServiceCollection services, WebSecurityOptions web)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await ProblemResults.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, "rate_limited");
            };

            // Signed-in traffic is limited per user; anonymous traffic (CSRF bootstrap, login) per source
            // address. The address is the socket peer unless a proxy is explicitly trusted above.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!context.Request.Path.StartsWithSegments("/api"))
                {
                    return RateLimitPartition.GetNoLimiter("non-api");
                }

                if (RequestContext.CallerOrNull(context) is { } caller)
                {
                    return RateLimitPartition.GetFixedWindowLimiter($"u:{caller.Id:N}", _ => Window(web.UserRequestsPerMinute));
                }

                var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"ip:{source}", _ => Window(web.AnonymousRequestsPerMinute));
            });

            options.AddPolicy("assist", context =>
            {
                var key = RequestContext.CallerOrNull(context)?.Id.ToString("N") ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter($"assist:{key}", _ => Window(web.AssistRequestsPerMinute));
            });
        });

        static FixedWindowRateLimiterOptions Window(int permits) => new()
        {
            PermitLimit = Math.Max(1, permits),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        };
    }
}
