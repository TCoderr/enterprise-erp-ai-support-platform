namespace Kantar.Application.Assistance;

public enum AnswerStatus
{
    /// <summary>An answer whose citations all point at passages the server retrieved for this caller.</summary>
    Answered,

    /// <summary>No readable published source matched; the provider was not called, or it declined to answer from the sources.</summary>
    NoSource,
    ProviderUnavailable,

    /// <summary>The provider's output was unusable: unparsable, uncited, or citing something that was not retrieved.</summary>
    RejectedUnsupported,

    /// <summary>The caller's session, roles or access to a cited source changed while the answer was being generated.</summary>
    AccessChanged,
}

public sealed record CitationDto(string DocumentSlug, string Title, int VersionNo, int SectionNo, string Heading);

/// <summary>Citation as stored with an answer: the public fields plus the chunk and version identity used for re-authorization.</summary>
public sealed record StoredCitation(Guid ChunkId, Guid VersionId, string DocumentSlug, string Title, int VersionNo, int SectionNo, string Heading)
{
    public CitationDto ToDto() => new(DocumentSlug, Title, VersionNo, SectionNo, Heading);
}

public sealed record ProviderInfoDto(string Kind, string? Model, bool IsDemo);

public sealed record SourceDto(string DocumentSlug, string Title, int VersionNo, int SectionNo, string Heading, string Snippet);

public sealed record AnswerDto(
    Guid? Id,
    string Status,
    string? Answer,
    IReadOnlyList<CitationDto> Citations,
    IReadOnlyList<SourceDto> Sources,
    string? Limitations,
    ProviderInfoDto Provider,
    DateTime? ExpiresAt);

// ---- provider port ----

/// <summary>One retrieved passage as the provider sees it. Id is a per-request label (S1, S2, …), not a database key.</summary>
public sealed record SourcePassage(string Id, string Title, string Heading, string Text);

public sealed record AnswerRequest(string Question, IReadOnlyList<SourcePassage> Sources);

public enum GenerationOutcome
{
    Answered,
    Insufficient,
    Unavailable,
    Malformed,
}

public sealed record GeneratedAnswer(GenerationOutcome Outcome, string? Text, IReadOnlyList<string> CitedIds, string? Limitations)
{
    public static GeneratedAnswer Unavailable { get; } = new(GenerationOutcome.Unavailable, null, [], null);
    public static GeneratedAnswer Malformed { get; } = new(GenerationOutcome.Malformed, null, [], null);
    public static GeneratedAnswer Insufficient(string? limitations = null) => new(GenerationOutcome.Insufficient, null, [], limitations);
}

/// <summary>
/// Turns a question and the passages retrieved for this caller into an answer. Implementations get
/// nothing else: no user identity, no database, no tools. They cannot widen what the caller may read.
/// </summary>
public interface IAnswerGenerator
{
    ProviderInfoDto Info { get; }

    Task<GeneratedAnswer> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken);
}
