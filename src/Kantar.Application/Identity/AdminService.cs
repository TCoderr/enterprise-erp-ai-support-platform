using System.Security.Cryptography;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Identity;

public sealed record GrantDto(Guid WarehouseId, string Level);

public sealed record AdminUserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    bool IsActive,
    bool MustChangePassword,
    decimal PoApprovalLimit,
    IReadOnlyList<string> Roles,
    IReadOnlyList<GrantDto> Grants,
    string ETag);

/// <summary>Returned once, on create and reset. The password is not stored or logged in clear text anywhere.</summary>
public sealed record TemporaryPasswordDto(AdminUserDto User, string TemporaryPassword);

public sealed record CreateUserCommand(string UserName, string DisplayName, IReadOnlyList<string> Roles, decimal PoApprovalLimit);

public sealed record AuditEntryDto(long Id, DateTime OccurredAt, Guid ActorUserId, string Action, string EntityType, Guid EntityId, Guid? WarehouseId, string CorrelationId);

public sealed class AdminService(IKantarDb db, IPasswordHasher hasher, TimeProvider clock, AuditWriter audit)
{
    private static void RequireAdmin(CurrentUser user)
    {
        if (!user.Is(Roles.SystemAdmin))
        {
            throw Problems.Forbidden();
        }
    }

    public ICommandHandler<CreateUserCommand, TemporaryPasswordDto> CreateUser => new DelegateHandler<CreateUserCommand, TemporaryPasswordDto>(
        (_, user, _) =>
        {
            RequireAdmin(user);
            return Task.CompletedTask;
        },
        CreateUserAsync);

    public static CreateUserCommand Normalize(CreateUserCommand command)
    {
        var validator = new Validator();
        var userName = (command.UserName ?? string.Empty).Trim().ToLowerInvariant();
        if (userName.Length is < 3 or > 64 || userName.Any(c => !(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_')))
        {
            validator.Add("userName", "invalid", "3–64 karakter; yalnız a–z, 0–9 ve . - _ kullanılabilir.");
        }

        var displayName = validator.Text("displayName", command.DisplayName, 2, 100);
        var roles = NormalizeRoles(command.Roles, validator);
        validator.Money("poApprovalLimit", command.PoApprovalLimit);
        validator.ThrowIfInvalid();
        return new CreateUserCommand(userName, displayName, roles, command.PoApprovalLimit);
    }

    private static string[] NormalizeRoles(IReadOnlyList<string>? roles, Validator validator)
    {
        var distinct = (roles ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (distinct.Any(r => !Roles.IsKnown(r)))
        {
            validator.Add("roles", "unknown_role", "Bilinmeyen rol.");
        }

        return distinct;
    }

    private async Task<TemporaryPasswordDto> CreateUserAsync(CreateUserCommand command, CurrentUser actor, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(u => u.UserName == command.UserName, cancellationToken))
        {
            throw Problems.Conflict("user_name_taken", "Bu kullanıcı adı kullanılıyor.");
        }

        var password = GenerateTemporaryPassword();
        var user = new User
        {
            UserName = command.UserName,
            DisplayName = command.DisplayName,
            PasswordHash = hasher.Hash(password),
            MustChangePassword = true,
            PoApprovalLimit = command.PoApprovalLimit,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        user.Roles.AddRange(command.Roles.Select(r => new UserRole { UserId = user.Id, Role = r }));
        db.Users.Add(user);
        audit.Write(actor, "admin.user_created", "User", user.Id, detail: new { roles = command.Roles });
        await db.SaveChangesAsync(cancellationToken);
        return new TemporaryPasswordDto(ToDto(user), password);
    }

    public async Task<IReadOnlyList<AdminUserDto>> ListAsync(CurrentUser actor, CancellationToken cancellationToken)
    {
        RequireAdmin(actor);
        var users = await db.Users.AsNoTracking().Include(u => u.Roles).Include(u => u.Grants)
            .OrderBy(u => u.UserName).ToListAsync(cancellationToken);
        return users.Select(ToDto).ToArray();
    }

    public async Task<AdminUserDto> GetAsync(CurrentUser actor, Guid id, CancellationToken cancellationToken)
    {
        RequireAdmin(actor);
        return ToDto(await LoadAsync(id, tracking: false, cancellationToken));
    }

    /// <summary>Replaces the role set and rotates the security stamp, which ends every session of that user.</summary>
    public async Task<AdminUserDto> SetRolesAsync(CurrentUser actor, Guid id, IReadOnlyList<string> roles, decimal? poApprovalLimit, byte[]? expected, CancellationToken cancellationToken)
    {
        RequireAdmin(actor);
        var validator = new Validator();
        var normalized = NormalizeRoles(roles, validator);
        if (poApprovalLimit is { } limit)
        {
            validator.Money("poApprovalLimit", limit);
        }

        validator.ThrowIfInvalid();

        var user = await LoadAsync(id, tracking: true, cancellationToken);
        Guard.Precondition(user.RowVersion, expected);
        if (user.Id == actor.Id && !normalized.Contains(Roles.SystemAdmin))
        {
            throw Problems.Conflict("cannot_demote_self", "Kendi yönetici rolünüzü kaldıramazsınız.");
        }

        user.Roles.RemoveAll(r => !normalized.Contains(r.Role));
        var existing = user.Roles.Select(r => r.Role).ToHashSet(StringComparer.Ordinal);
        user.Roles.AddRange(normalized.Where(r => !existing.Contains(r)).Select(r => new UserRole { UserId = user.Id, Role = r }));
        user.PoApprovalLimit = poApprovalLimit ?? user.PoApprovalLimit;
        user.RotateSecurityStamp();
        audit.Write(actor, "admin.roles_changed", "User", user.Id, detail: new { roles = normalized });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    public async Task<AdminUserDto> SetGrantsAsync(CurrentUser actor, Guid id, IReadOnlyList<GrantDto> grants, byte[]? expected, CancellationToken cancellationToken)
    {
        RequireAdmin(actor);
        var validator = new Validator();
        var parsed = new Dictionary<Guid, WarehouseLevel>();
        for (var i = 0; i < (grants?.Count ?? 0); i++)
        {
            var grant = grants![i];
            // Names only: Enum.TryParse would also accept "1", "2", "3".
            if (grant is null || string.IsNullOrEmpty(grant.Level) || !char.IsAsciiLetter(grant.Level[0])
                || !Enum.TryParse<WarehouseLevel>(grant.Level, ignoreCase: false, out var level) || level == WarehouseLevel.None || !Enum.IsDefined(level))
            {
                validator.Add($"grants[{i}].level", "invalid", "Seviye View, Operate veya Supervise olmalıdır.");
            }
            else if (!parsed.TryAdd(grant.WarehouseId, level))
            {
                validator.Add($"grants[{i}].warehouseId", "duplicate", "Aynı depo iki kez verilemez.");
            }
        }

        validator.ThrowIfInvalid();

        var ids = parsed.Keys.ToArray();
        var known = await db.Warehouses.AsNoTracking().CountAsync(w => ids.Contains(w.Id), cancellationToken);
        if (known != ids.Length)
        {
            throw Problems.Validation("grants", "unknown_warehouse", "Bilinmeyen depo.");
        }

        var user = await LoadAsync(id, tracking: true, cancellationToken);
        Guard.Precondition(user.RowVersion, expected);
        user.Grants.RemoveAll(g => !parsed.ContainsKey(g.WarehouseId));
        foreach (var (warehouseId, level) in parsed)
        {
            if (user.Grants.FirstOrDefault(g => g.WarehouseId == warehouseId) is { } grant)
            {
                grant.Level = level;
            }
            else
            {
                user.Grants.Add(new WarehouseGrant { UserId = user.Id, WarehouseId = warehouseId, Level = level });
            }
        }

        // Grants are read from the database on every request, so the change is effective immediately.
        // Rotating the stamp additionally ends the user's sessions and moves the row version (ETag).
        user.RotateSecurityStamp();
        audit.Write(actor, "admin.grants_changed", "User", user.Id, detail: new { count = parsed.Count });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    public async Task<TemporaryPasswordDto> ResetPasswordAsync(CurrentUser actor, Guid id, CancellationToken cancellationToken)
    {
        RequireAdmin(actor);
        var user = await LoadAsync(id, tracking: true, cancellationToken);
        var password = GenerateTemporaryPassword();
        user.PasswordHash = hasher.Hash(password);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.RotateSecurityStamp();
        audit.Write(actor, "admin.password_reset", "User", user.Id);
        await db.SaveChangesAsync(cancellationToken);
        await RevokeSessionsAsync(user.Id, cancellationToken);
        return new TemporaryPasswordDto(ToDto(user), password);
    }

    public async Task<AdminUserDto> SetActiveAsync(CurrentUser actor, Guid id, bool active, CancellationToken cancellationToken)
    {
        RequireAdmin(actor);
        var user = await LoadAsync(id, tracking: true, cancellationToken);
        if (user.Id == actor.Id && !active)
        {
            throw Problems.Conflict("cannot_deactivate_self", "Kendi hesabınızı pasifleştiremezsiniz.");
        }

        user.IsActive = active;
        user.RotateSecurityStamp();
        audit.Write(actor, active ? "admin.user_activated" : "admin.user_deactivated", "User", user.Id);
        await db.SaveChangesAsync(cancellationToken);
        if (!active)
        {
            await RevokeSessionsAsync(user.Id, cancellationToken);
        }

        return ToDto(user);
    }

    public async Task<Page<AuditEntryDto>> AuditAsync(CurrentUser actor, string? action, string? entityType, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        RequireAdmin(actor);
        var take = Guard.Limit(limit);
        IQueryable<AuditEntry> query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrEmpty(action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrEmpty(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out long before))
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            query = query.Where(a => a.Id < before);
        }

        var rows = await query.OrderByDescending(a => a.Id).Take(take + 1)
            .Select(a => new AuditEntryDto(a.Id, a.OccurredAt, a.ActorUserId, a.Action, a.EntityType, a.EntityId, a.WarehouseId, a.CorrelationId))
            .ToListAsync(cancellationToken);
        var next = rows.Count > take ? Cursor.Encode(rows[take - 1].Id) : null;
        return new Page<AuditEntryDto>(rows.Take(take).ToArray(), next);
    }

    private async Task RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), cancellationToken);
    }

    private async Task<User> LoadAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<User> query = db.Users.Include(u => u.Roles).Include(u => u.Grants);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(u => u.Id == id, cancellationToken) ?? throw Problems.NotFound();
    }

    private static AdminUserDto ToDto(User user) => new(
        user.Id,
        user.UserName,
        user.DisplayName,
        user.IsActive,
        user.MustChangePassword,
        user.PoApprovalLimit,
        user.Roles.Select(r => r.Role).Order(StringComparer.Ordinal).ToArray(),
        user.Grants.Select(g => new GrantDto(g.WarehouseId, g.Level.ToString())).OrderBy(g => g.WarehouseId).ToArray(),
        ETags.From(user.RowVersion));

    /// <summary>20 characters from an unambiguous alphabet plus fixed classes, so it always meets the policy.</summary>
    public static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        return "Kt!" + RandomNumberGenerator.GetString(alphabet, 20) + "aA9";
    }
}
