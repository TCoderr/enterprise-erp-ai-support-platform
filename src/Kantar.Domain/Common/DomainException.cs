namespace Kantar.Domain.Common;

public enum ErrorKind
{
    Validation,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    PreconditionFailed,
    PreconditionRequired,
    Unprocessable,
    RateLimited,
    Unavailable,
}

public sealed record FieldError(string Field, string Code, string Detail);

/// <summary>
/// A rejected request. <see cref="Code"/> is the stable machine-readable code from docs/05;
/// it is never built from user input.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(ErrorKind kind, string code, string? detail = null, IReadOnlyList<FieldError>? errors = null)
        : base(detail ?? code)
    {
        Kind = kind;
        Code = code;
        Detail = detail;
        Errors = errors ?? [];
    }

    public ErrorKind Kind { get; }
    public string Code { get; }
    public string? Detail { get; }
    public IReadOnlyList<FieldError> Errors { get; }
}

public static class Problems
{
    public static DomainException NotFound() => new(ErrorKind.NotFound, "not_found");

    public static DomainException Forbidden(string code = "forbidden", string? detail = null) =>
        new(ErrorKind.Forbidden, code, detail);

    public static DomainException Conflict(string code, string? detail = null, IReadOnlyList<FieldError>? errors = null) =>
        new(ErrorKind.Conflict, code, detail, errors);

    public static DomainException InvalidState(string detail) => new(ErrorKind.Conflict, "invalid_state", detail);

    public static DomainException Unprocessable(string code, string? detail = null, IReadOnlyList<FieldError>? errors = null) =>
        new(ErrorKind.Unprocessable, code, detail, errors);

    public static DomainException Validation(IReadOnlyList<FieldError> errors) =>
        new(ErrorKind.Validation, "validation_failed", null, errors);

    public static DomainException Validation(string field, string code, string detail) =>
        Validation([new FieldError(field, code, detail)]);

    public static DomainException PreconditionFailed() => new(ErrorKind.PreconditionFailed, "precondition_failed");

    public static DomainException PreconditionRequired() => new(ErrorKind.PreconditionRequired, "precondition_required");

    public static DomainException Unavailable(string code = "retry_later") => new(ErrorKind.Unavailable, code);
}
