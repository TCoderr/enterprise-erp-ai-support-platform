using System.Text.RegularExpressions;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Support;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Support;

public sealed record KbArticleListItemDto(string Slug, string Title, string Module, string Visibility, string? ErrorCode, Guid? WarehouseId, int VersionNo, DateTime UpdatedAt);

public sealed record KbArticleDto(string Slug, string Title, string BodyMarkdown, string Module, string Visibility, string? ErrorCode, Guid? WarehouseId, int VersionNo, DateTime PublishedVersionCreatedAt);

public sealed record KbVersionDto(int VersionNo, string Title, string BodyMarkdown, Guid AuthorUserId, DateTime CreatedAt, bool IsPublished);

public sealed record KbDocumentDto(
    Guid Id, string Slug, string Status, string Visibility, string Module, Guid? WarehouseId, string? ErrorCode,
    int? PublishedVersionNo, int LatestVersionNo, DateTime UpdatedAt, IReadOnlyList<KbVersionDto> Versions, string ETag);

public sealed record CreateKbDocumentCommand(string Slug, string Visibility, string Module, Guid? WarehouseId, string? ErrorCode, string Title, string BodyMarkdown);

public sealed record AddKbVersionCommand(Guid DocumentId, string Title, string BodyMarkdown, string? IfMatch);

public sealed record KbPublishCommand(Guid DocumentId, int? VersionNo, string? IfMatch);

/// <summary>A published chunk the caller is allowed to read, with the identity that a citation needs.</summary>
public sealed class ReadableChunk
{
    public Guid ChunkId { get; init; }
    public Guid DocumentId { get; init; }
    public Guid VersionId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int VersionNo { get; init; }
    public int SectionNo { get; init; }
    public string Heading { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public AppModule Module { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed partial class KnowledgeService(IKantarDb db, IDocumentLocks locks, TimeProvider clock, AuditWriter audit)
{
    public const int MaxBodyLength = 20_000;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Z]{3}-[0-9]{3}$", RegexOptions.CultureInvariant)]
    public static partial Regex ErrorCodePattern();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ the read rule, as a query

    /// <summary>
    /// The only source of knowledge text for readers, search and answer generation. The access rule of
    /// docs/02 §9.3 is part of the SQL WHERE clause: rows the caller may not read are never loaded,
    /// so they cannot reach a prompt, a cache or a response by mistake. Draft, retired and superseded
    /// versions are excluded by the same predicate (PublishedVersionId), not by a separate index.
    /// </summary>
    public IQueryable<ReadableChunk> ReadableChunks(CurrentUser user)
    {
        var visibilities = AccessPolicy.ReadableVisibilities(user).ToArray();
        var modules = AccessPolicy.ReadableModules(user).ToArray();
        var bypassWarehouse = AccessPolicy.BypassesKbWarehouseScope(user);
        var warehouses = user.WarehousesWith(WarehouseLevel.View);

        return
            from d in db.KbDocuments.AsNoTracking()
            join v in db.KbDocumentVersions.AsNoTracking() on d.PublishedVersionId equals v.Id
            join c in db.KbChunks.AsNoTracking() on v.Id equals c.VersionId
            where d.Status == KbStatus.Published
                && visibilities.Contains(d.Visibility)
                && modules.Contains(d.Module)
                && (d.WarehouseId == null || bypassWarehouse || warehouses.Contains(d.WarehouseId.Value))
            select new ReadableChunk
            {
                ChunkId = c.Id, DocumentId = d.Id, VersionId = v.Id, Slug = d.Slug, Title = v.Title, VersionNo = v.VersionNo,
                SectionNo = c.SectionNo, Heading = c.Heading, Text = c.Text, Module = d.Module, ErrorCode = d.ErrorCode,
            };
    }

    private IQueryable<KbDocument> ReadableDocuments(CurrentUser user)
    {
        var visibilities = AccessPolicy.ReadableVisibilities(user).ToArray();
        var modules = AccessPolicy.ReadableModules(user).ToArray();
        var bypassWarehouse = AccessPolicy.BypassesKbWarehouseScope(user);
        var warehouses = user.WarehousesWith(WarehouseLevel.View);
        return db.KbDocuments.AsNoTracking().Where(d =>
            d.Status == KbStatus.Published
            && visibilities.Contains(d.Visibility)
            && modules.Contains(d.Module)
            && (d.WarehouseId == null || bypassWarehouse || warehouses.Contains(d.WarehouseId.Value)));
    }

    // ------------------------------------------------------------------ reader

    public async Task<IReadOnlyList<KbArticleListItemDto>> ListArticlesAsync(CurrentUser user, string? search, AppModule? module, string? errorCode, CancellationToken ct)
    {
        var query =
            from d in ReadableDocuments(user)
            join v in db.KbDocumentVersions.AsNoTracking() on d.PublishedVersionId equals v.Id
            select new { d, v };

        if (module is { } m)
        {
            query = query.Where(x => x.d.Module == m);
        }

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            var code = errorCode.Trim().ToUpperInvariant();
            query = query.Where(x => x.d.ErrorCode == code);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (term.Length > 64)
            {
                throw Problems.Validation("search", "length", "Arama en fazla 64 karakter olabilir.");
            }

            query = query.Where(x => x.v.Title.Contains(term) || x.v.BodyMarkdown.Contains(term));
        }

        var rows = await query.OrderBy(x => x.v.Title).Take(200).ToListAsync(ct);
        return rows.Select(x => new KbArticleListItemDto(
            x.d.Slug, x.v.Title, x.d.Module.ToString(), x.d.Visibility.ToString(), x.d.ErrorCode, x.d.WarehouseId, x.v.VersionNo, x.d.UpdatedAt)).ToArray();
    }

    public async Task<KbArticleDto> GetArticleAsync(CurrentUser user, string slug, CancellationToken ct)
    {
        var row = await (
            from d in ReadableDocuments(user)
            join v in db.KbDocumentVersions.AsNoTracking() on d.PublishedVersionId equals v.Id
            where d.Slug == slug
            select new { d, v }).FirstOrDefaultAsync(ct) ?? throw Problems.NotFound();

        return new KbArticleDto(
            row.d.Slug, row.v.Title, row.v.BodyMarkdown, row.d.Module.ToString(), row.d.Visibility.ToString(),
            row.d.ErrorCode, row.d.WarehouseId, row.v.VersionNo, row.v.CreatedAt);
    }

    // ------------------------------------------------------------------ management (support staff)

    private static Task RequireStaff(CurrentUser user) =>
        user.IsSupportStaff ? Task.CompletedTask : throw Problems.Forbidden();

    public async Task<IReadOnlyList<KbDocumentDto>> ListDocumentsAsync(CurrentUser user, CancellationToken ct)
    {
        await RequireStaff(user);
        var documents = await db.KbDocuments.AsNoTracking().Include(d => d.Versions).OrderBy(d => d.Slug).Take(500).ToListAsync(ct);
        return documents.Select(d => ToDto(d, includeBodies: false)).ToArray();
    }

    public async Task<KbDocumentDto> GetDocumentAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        await RequireStaff(user);
        var document = await db.KbDocuments.AsNoTracking().Include(d => d.Versions).FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw Problems.NotFound();
        return ToDto(document, includeBodies: true);
    }

    public static CreateKbDocumentCommand Normalize(CreateKbDocumentCommand command)
    {
        var v = new Validator();
        var slug = (command.Slug ?? string.Empty).Trim();
        if (slug.Length is < 3 or > 80 || !SlugPattern().IsMatch(slug))
        {
            v.Add("slug", "invalid", "3–80 karakter; küçük harf, rakam ve tire.");
        }

        if (!Enum.TryParse<KbVisibility>(command.Visibility, out var visibility) || !Enum.IsDefined(visibility) || char.IsAsciiDigit((command.Visibility ?? "0")[0]))
        {
            v.Add("visibility", "invalid", "General, SupportOnly veya ManagerOnly.");
        }

        if (!Enum.TryParse<AppModule>(command.Module, out var module) || !Enum.IsDefined(module) || char.IsAsciiDigit((command.Module ?? "0")[0]))
        {
            v.Add("module", "invalid", "General, Sales, Purchasing, Inventory veya Support.");
        }

        string? errorCode = null;
        if (!string.IsNullOrWhiteSpace(command.ErrorCode))
        {
            errorCode = command.ErrorCode.Trim().ToUpperInvariant();
            if (!ErrorCodePattern().IsMatch(errorCode))
            {
                v.Add("errorCode", "invalid", "Hata kodu AAA-000 biçiminde olmalıdır.");
            }
        }

        var (title, body) = ValidateContent(v, command.Title, command.BodyMarkdown);
        v.ThrowIfInvalid();
        return command with { Slug = slug, ErrorCode = errorCode, Title = title, BodyMarkdown = body };
    }

    public static AddKbVersionCommand Normalize(AddKbVersionCommand command)
    {
        var v = new Validator();
        var (title, body) = ValidateContent(v, command.Title, command.BodyMarkdown);
        v.ThrowIfInvalid();
        return command with { Title = title, BodyMarkdown = body };
    }

    private static (string Title, string Body) ValidateContent(Validator v, string? title, string? body) =>
        (v.Text("title", title, 3, 160), v.Text("bodyMarkdown", body, 20, MaxBodyLength, multiline: true));

    public ICommandHandler<CreateKbDocumentCommand, KbDocumentDto> CreateDocument => new DelegateHandler<CreateKbDocumentCommand, KbDocumentDto>(
        (_, user, _) => RequireStaff(user),
        async (command, user, ct) =>
        {
            if (await db.KbDocuments.AnyAsync(d => d.Slug == command.Slug, ct))
            {
                throw Problems.Conflict("code_taken", "Bu kısa ad kullanılıyor.");
            }

            if (command.WarehouseId is { } wid && !await db.Warehouses.AnyAsync(w => w.Id == wid, ct))
            {
                throw Problems.Validation("warehouseId", "unknown", "Bilinmeyen depo.");
            }

            var now = Now;
            var document = new KbDocument
            {
                Slug = command.Slug,
                Visibility = Enum.Parse<KbVisibility>(command.Visibility),
                Module = Enum.Parse<AppModule>(command.Module),
                WarehouseId = command.WarehouseId,
                ErrorCode = command.ErrorCode,
                LatestVersionNo = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            document.Versions.Add(new KbDocumentVersion
            {
                DocumentId = document.Id, VersionNo = 1, Title = command.Title, BodyMarkdown = command.BodyMarkdown, AuthorUserId = user.Id, CreatedAt = now,
            });
            db.KbDocuments.Add(document);
            audit.Write(user, "kb.document_created", "KbDocument", document.Id);
            await db.SaveChangesAsync(ct);
            return ToDto(document, includeBodies: true);
        });

    public ICommandHandler<AddKbVersionCommand, KbDocumentDto> AddVersion => new DelegateHandler<AddKbVersionCommand, KbDocumentDto>(
        (_, user, _) => RequireStaff(user),
        async (command, user, ct) =>
        {
            var document = await LockWithVersionsAsync(command.DocumentId, command.IfMatch, ct);
            if (document.Status == KbStatus.Retired)
            {
                throw Problems.InvalidState("Emekli edilmiş belgeye sürüm eklenemez.");
            }

            var now = Now;
            document.LatestVersionNo++;
            document.UpdatedAt = now;

            // Added through the set, not through the tracked document's collection: the key is generated
            // here, and reached only through the collection EF Core would take the version for an existing
            // row and send an UPDATE (which this append-only table refuses) instead of an INSERT.
            db.KbDocumentVersions.Add(new KbDocumentVersion
            {
                DocumentId = document.Id, VersionNo = document.LatestVersionNo, Title = command.Title, BodyMarkdown = command.BodyMarkdown,
                AuthorUserId = user.Id, CreatedAt = now,
            });
            audit.Write(user, "kb.version_added", "KbDocument", document.Id, detail: new { versionNo = document.LatestVersionNo });
            await db.SaveChangesAsync(ct);
            return ToDto(document, includeBodies: true);
        });

    /// <summary>
    /// Publishes one version. The served chunks change in this transaction: from the next query on,
    /// retrieval joins on the new PublishedVersionId and the previous version's chunks stop matching.
    /// </summary>
    public ICommandHandler<KbPublishCommand, KbDocumentDto> Publish => new DelegateHandler<KbPublishCommand, KbDocumentDto>(
        (_, user, _) => AccessPolicy.CanPublishKb(user) ? Task.CompletedTask : throw Problems.Forbidden(),
        async (command, user, ct) =>
        {
            var document = await LockWithVersionsAsync(command.DocumentId, command.IfMatch, ct);
            if (document.Status == KbStatus.Retired)
            {
                throw Problems.InvalidState("Emekli edilmiş belge yeniden yayınlanamaz; yeni belge açın.");
            }

            var version = document.Versions.FirstOrDefault(v => v.VersionNo == (command.VersionNo ?? document.LatestVersionNo))
                ?? throw Problems.Validation("versionNo", "unknown", "Bu sürüm yok.");

            if (!await db.KbChunks.AnyAsync(c => c.VersionId == version.Id, ct))
            {
                foreach (var section in KbChunker.Split(version.Title, version.BodyMarkdown))
                {
                    db.KbChunks.Add(new KbChunk
                    {
                        DocumentId = document.Id, VersionId = version.Id, SectionNo = section.SectionNo, Heading = section.Heading, Text = section.Text,
                    });
                }
            }

            document.PublishedVersionId = version.Id;
            document.Status = KbStatus.Published;
            document.UpdatedAt = Now;
            audit.Write(user, "kb.published", "KbDocument", document.Id, detail: new { versionNo = version.VersionNo });
            await db.SaveChangesAsync(ct);
            return ToDto(document, includeBodies: true);
        });

    public ICommandHandler<KbPublishCommand, KbDocumentDto> Retire => new DelegateHandler<KbPublishCommand, KbDocumentDto>(
        (_, user, _) => AccessPolicy.CanPublishKb(user) ? Task.CompletedTask : throw Problems.Forbidden(),
        async (command, user, ct) =>
        {
            var document = await LockWithVersionsAsync(command.DocumentId, command.IfMatch, ct);
            if (document.Status != KbStatus.Published)
            {
                throw Problems.InvalidState("Yalnız yayındaki belge emekli edilebilir.");
            }

            document.Status = KbStatus.Retired;
            document.UpdatedAt = Now;
            audit.Write(user, "kb.retired", "KbDocument", document.Id);
            await db.SaveChangesAsync(ct);
            return ToDto(document, includeBodies: true);
        });

    private async Task<KbDocument> LockWithVersionsAsync(Guid id, string? ifMatch, CancellationToken ct)
    {
        var document = await locks.LockKbDocumentAsync(id, ct) ?? throw Problems.NotFound();
        Guard.Precondition(document.RowVersion, ifMatch is null ? null : ETags.TryParse(ifMatch, out var version) ? version : throw Problems.PreconditionFailed());
        await db.KbDocumentVersions.Where(v => v.DocumentId == id).LoadAsync(ct);
        return document;
    }

    private static KbDocumentDto ToDto(KbDocument document, bool includeBodies)
    {
        var published = document.Versions.FirstOrDefault(v => v.Id == document.PublishedVersionId);
        return new KbDocumentDto(
            document.Id, document.Slug, document.Status.ToString(), document.Visibility.ToString(), document.Module.ToString(),
            document.WarehouseId, document.ErrorCode, published?.VersionNo, document.LatestVersionNo, document.UpdatedAt,
            document.Versions.OrderByDescending(v => v.VersionNo).Select(v => new KbVersionDto(
                v.VersionNo, v.Title, includeBodies ? v.BodyMarkdown : string.Empty, v.AuthorUserId, v.CreatedAt, v.Id == document.PublishedVersionId)).ToArray(),
            ETags.From(document.RowVersion));
    }
}
