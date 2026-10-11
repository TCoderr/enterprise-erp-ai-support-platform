using System.Text.Json;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;

namespace Kantar.Api.Http;

/// <summary>Per-request values that application services need but must not take from the client.</summary>
public sealed class RequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    private const string CorrelationKey = "kantar.correlation";
    private const string CallerKey = "kantar.caller";
    private const string CsrfKey = "kantar.csrf";
    private readonly string _fallback = NewCorrelationId();

    public string CorrelationId => accessor.HttpContext is { } context ? CorrelationIdOf(context) : _fallback;

    public static string NewCorrelationId() => Guid.CreateVersion7().ToString("N");

    public static string CorrelationIdOf(HttpContext context)
    {
        if (context.Items[CorrelationKey] is string existing)
        {
            return existing;
        }

        // Always generated here; a client-supplied id would let callers forge log correlation.
        var created = NewCorrelationId();
        context.Items[CorrelationKey] = created;
        return created;
    }

    public static void SetCaller(HttpContext context, CurrentUser user, byte[] csrfSecret)
    {
        context.Items[CallerKey] = user;
        context.Items[CsrfKey] = csrfSecret;
    }

    public static CurrentUser? CallerOrNull(HttpContext context) => context.Items[CallerKey] as CurrentUser;

    public static byte[]? CsrfSecretOf(HttpContext context) => context.Items[CsrfKey] as byte[];
}

public static class HttpContextExtensions
{
    /// <summary>The authenticated caller. Endpoints that are not anonymous can rely on it being present.</summary>
    public static CurrentUser Caller(this HttpContext context) =>
        RequestContext.CallerOrNull(context) ?? throw new DomainException(ErrorKind.Unauthenticated, "unauthenticated");

    /// <summary>Rejects query parameters that the endpoint does not document.</summary>
    public static void AllowQuery(this HttpContext context, params ReadOnlySpan<string> allowed)
    {
        foreach (var key in context.Request.Query.Keys)
        {
            var known = false;
            foreach (var name in allowed)
            {
                if (string.Equals(name, key, StringComparison.Ordinal))
                {
                    known = true;
                    break;
                }
            }

            if (!known || context.Request.Query[key].Count != 1)
            {
                throw new DomainException(ErrorKind.Validation, "unknown_parameter", null, [new FieldError(Truncate(key), "unknown_parameter", "Bu parametre desteklenmiyor veya birden çok kez verildi.")]);
            }
        }
    }

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40];

    public static string? QueryString(this HttpContext context, string name)
    {
        var value = context.Request.Query[name].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static int? QueryInt(this HttpContext context, string name)
    {
        var raw = context.QueryString(name);
        if (raw is null)
        {
            return null;
        }

        return int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Problems.Validation(name, "invalid", "Tam sayı bekleniyor.");
    }

    public static bool? QueryBool(this HttpContext context, string name) => context.QueryString(name) switch
    {
        null => null,
        "true" => true,
        "false" => false,
        _ => throw Problems.Validation(name, "invalid", "true veya false bekleniyor."),
    };

    public static Guid? QueryGuid(this HttpContext context, string name)
    {
        var raw = context.QueryString(name);
        if (raw is null)
        {
            return null;
        }

        return Guid.TryParse(raw, out var value) ? value : throw Problems.Validation(name, "invalid", "Geçersiz kimlik.");
    }

    public static TEnum? QueryEnum<TEnum>(this HttpContext context, string name) where TEnum : struct, Enum
    {
        var raw = context.QueryString(name);
        if (raw is null)
        {
            return null;
        }

        return Enum.TryParse<TEnum>(raw, ignoreCase: false, out var value) && Enum.IsDefined(value) && !char.IsAsciiDigit(raw[0])
            ? value
            : throw Problems.Validation(name, "invalid", "Geçersiz değer.");
    }

    /// <summary>Strong ETag from If-Match, or null when the header is absent.</summary>
    public static byte[]? IfMatch(this HttpContext context)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        return ETags.TryParse(header, out var version) ? version : throw Problems.PreconditionFailed();
    }

    public static string RequireIdempotencyKey(this HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 16 or > 128 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new DomainException(ErrorKind.Validation, "idempotency_key_required", "Idempotency-Key 16–128 karakter olmalı; yalnız harf, rakam, - ve _ içerebilir.");
        }

        return key;
    }

    public static IdempotencyRequest Idempotency<TCommand>(this HttpContext context, string operation, TCommand command) =>
        new(operation, context.RequireIdempotencyKey(), ApiJson.HashCommand(operation, command));

    /// <summary>Writes the outcome exactly as produced or stored: same status, headers and body bytes.</summary>
    public static async Task WriteOutcomeAsync(this HttpContext context, CommandOutcome outcome)
    {
        context.Response.StatusCode = outcome.Status;
        if (outcome.Location is not null)
        {
            context.Response.Headers.Location = outcome.Location;
        }

        if (outcome.ETag is not null)
        {
            context.Response.Headers.ETag = outcome.ETag;
        }

        if (outcome.Replayed)
        {
            context.Response.Headers["Idempotent-Replay"] = "true";
        }

        if (outcome.Body.Length > 0)
        {
            context.Response.ContentType = outcome.ContentType;
            context.Response.ContentLength = outcome.Body.Length;
            await context.Response.Body.WriteAsync(outcome.Body, context.RequestAborted);
        }
    }

    public static Task WriteJsonAsync<T>(this HttpContext context, T value, int status = StatusCodes.Status200OK, string? etag = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = CommandExecutor.JsonContentType;
        if (etag is not null)
        {
            context.Response.Headers.ETag = etag;
        }

        return context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, ApiJson.Options), context.RequestAborted).AsTask();
    }
}
