using System.Text.Json;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Api.Http;

public sealed record ProblemBody(
    string Type,
    string Title,
    int Status,
    string Code,
    string CorrelationId,
    string? UserCode,
    string? Detail,
    IReadOnlyList<FieldError>? Errors);

/// <summary>RFC 9457 problem responses with a stable machine code. Auth failures are JSON too, never redirects.</summary>
public static class ProblemResults
{
    public const string ContentType = "application/problem+json; charset=utf-8";

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["invalid_json"] = "İstek gövdesi geçerli JSON değil",
        ["unknown_field"] = "İstek tanınmayan bir alan içeriyor",
        ["duplicate_field"] = "İstek aynı alanı birden çok kez içeriyor",
        ["validation_failed"] = "İstek doğrulanamadı",
        ["unknown_parameter"] = "Tanınmayan sorgu parametresi",
        ["idempotency_key_required"] = "Idempotency-Key başlığı gerekli",
        ["unauthenticated"] = "Oturum açmanız gerekiyor",
        ["forbidden"] = "Bu işlem için yetkiniz yok",
        ["csrf_failed"] = "İstek doğrulama belirteci geçersiz",
        ["approval_limit_exceeded"] = "Onay limiti yetersiz",
        ["password_change_required"] = "Devam etmeden önce parolanızı değiştirmelisiniz",
        ["not_found"] = "Kayıt bulunamadı",
        ["invalid_state"] = "Belge bu işlem için uygun durumda değil",
        ["insufficient_available"] = "Yeterli kullanılabilir stok yok",
        ["exceeds_remaining"] = "Miktar kalan miktarı aşıyor",
        ["duplicate_business_reference"] = "Bu iş referansı daha önce kullanıldı",
        ["idempotency_key_reused"] = "Idempotency-Key farklı bir istekle kullanılmış",
        ["master_data_inactive"] = "Pasif ana veri kullanılamaz",
        ["source_no_longer_available"] = "Kaynak artık kullanılamıyor",
        ["conflict"] = "İstek mevcut durumla çakışıyor",
        ["integrity_violation"] = "Veri bütünlüğü kuralı ihlal edildi",
        ["precondition_failed"] = "Kayıt siz görüntüledikten sonra değişti",
        ["precondition_required"] = "If-Match başlığı gerekli",
        ["payload_too_large"] = "İstek gövdesi çok büyük",
        ["unsupported_media_type"] = "İçerik türü desteklenmiyor",
        ["line_mismatch"] = "Satır belgeyle eşleşmiyor",
        ["rate_limited"] = "Çok fazla istek gönderildi",
        ["retry_later"] = "İşlem şu anda tamamlanamadı; daha sonra yeniden deneyin",
        ["database_unavailable"] = "Veritabanına ulaşılamıyor",
        ["internal_error"] = "Beklenmeyen bir hata oluştu",
    };

    /// <summary>
    /// Short codes shown to users and used as knowledge-base keys ("STK-409 ne demek?"). Only errors a
    /// user can act on have one.
    /// </summary>
    private static readonly Dictionary<string, string> UserCodes = new(StringComparer.Ordinal)
    {
        ["insufficient_available"] = "STK-409",
        ["precondition_failed"] = "SIP-412",
        ["exceeds_remaining"] = "MKB-409",
        ["duplicate_business_reference"] = "REF-409",
        ["approval_limit_exceeded"] = "YTK-403",
    };

    public static int StatusOf(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorKind.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static Task WriteAsync(HttpContext context, int status, string code, string? detail = null, IReadOnlyList<FieldError>? errors = null)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var title = Titles.GetValueOrDefault(code, Titles["conflict"]);
        var body = new ProblemBody(
            $"https://kantar.invalid/problems/{code}",
            title,
            status,
            code,
            RequestContext.CorrelationIdOf(context),
            UserCodes.GetValueOrDefault(code),
            detail,
            errors is { Count: > 0 } ? errors : null);

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(body, ApiJson.Options)).AsTask();
    }

    public static Task WriteAsync(HttpContext context, DomainException exception)
    {
        var status = exception.Code switch
        {
            "payload_too_large" => StatusCodes.Status413PayloadTooLarge,
            "unsupported_media_type" => StatusCodes.Status415UnsupportedMediaType,
            _ => StatusOf(exception.Kind),
        };
        return WriteAsync(context, status, exception.Code, exception.Detail, exception.Errors);
    }
}

/// <summary>Translates every failure into a problem response. Exception text and SQL messages never reach the client.</summary>
public sealed partial class ProblemMiddleware(RequestDelegate next, ILogger<ProblemMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException ex)
        {
            await ProblemResults.WriteAsync(context, ex);
        }
        catch (UniqueConstraintException)
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status409Conflict, "conflict", "Aynı anahtara sahip bir kayıt zaten var.");
        }
        catch (DbUpdateConcurrencyException)
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status412PreconditionFailed, "precondition_failed");
        }
        catch (BadHttpRequestException ex)
        {
            var (status, code) = ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? (StatusCodes.Status413PayloadTooLarge, "payload_too_large")
                : (StatusCodes.Status400BadRequest, "invalid_json");
            await ProblemResults.WriteAsync(context, status, code);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
        }
        catch (Exception ex)
        {
            var sql = Infrastructure.Persistence.SqlErrors.Classify(ex);
            if (sql == Infrastructure.Persistence.SqlErrorClass.UniqueViolation)
            {
                await ProblemResults.WriteAsync(context, StatusCodes.Status409Conflict, "conflict", "Aynı anahtara sahip bir kayıt zaten var.");
                return;
            }

            if (sql == Infrastructure.Persistence.SqlErrorClass.Unavailable)
            {
                LogUnavailable(logger, ex.GetType().Name);
                await ProblemResults.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "database_unavailable");
                return;
            }

            // Only the exception type is logged at this level: messages can carry row values.
            LogUnhandled(logger, ex.GetType().FullName ?? "unknown", ex.StackTrace ?? string.Empty);
            await ProblemResults.WriteAsync(context, StatusCodes.Status500InternalServerError, "internal_error");
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Unhandled {ExceptionType} at {StackTrace}")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType, string stackTrace);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Database unavailable ({ExceptionType})")]
    private static partial void LogUnavailable(ILogger logger, string exceptionType);
}
