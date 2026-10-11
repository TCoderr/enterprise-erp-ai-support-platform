using System.Security.Cryptography;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Identity;

public sealed class SecurityOptions
{
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(8);
    public int LockoutThreshold { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public int PasswordMinLength { get; set; } = 12;
}

public sealed record WarehouseAccessDto(Guid Id, string Code, string Name, string Level);

public sealed record MeDto(
    Guid Id,
    string UserName,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<WarehouseAccessDto> Warehouses,
    bool MustChangePassword,
    decimal PoApprovalLimit);

public sealed record ResolvedSession(CurrentUser User, byte[] CsrfSecret);

public sealed record LoginResult(string RawToken, DateTime ExpiresAt, CurrentUser User, byte[] CsrfSecret);

/// <summary>
/// A hash that is verified when the user name does not exist, so the response time does not reveal
/// which names are real. Computed once per process.
/// </summary>
public sealed class LoginDecoy(IPasswordHasher hasher)
{
    public string Hash { get; } = hasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
}

public sealed class AuthService(IKantarDb db, IPasswordHasher hasher, LoginDecoy decoy, TimeProvider clock, SecurityOptions options, AuditWriter audit)
{
    private readonly string _decoyHash = decoy.Hash;

    public static byte[] HashToken(string rawToken) => SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(rawToken));

    public async Task<LoginResult?> LoginAsync(string userName, string password, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var normalized = userName.Trim().ToLowerInvariant();
        var user = normalized.Length is > 0 and <= 64
            ? await db.Users.FirstOrDefaultAsync(u => u.UserName == normalized, cancellationToken)
            : null;

        if (user is null || !user.IsActive || (user.LockedUntil is { } until && until > now))
        {
            hasher.Verify(_decoyHash, password);
            return null;
        }

        if (!hasher.Verify(user.PasswordHash, password))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= options.LockoutThreshold)
            {
                user.LockedUntil = now + options.LockoutDuration;
                user.FailedLoginCount = 0;
                audit.Write(user.Id, "auth.locked", "User", user.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new Session
        {
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            CsrfSecret = RandomNumberGenerator.GetBytes(32),
            SecurityStamp = user.SecurityStamp,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.AbsoluteLifetime,
        };
        db.Sessions.Add(session);
        audit.Write(user.Id, "auth.login", "User", user.Id);
        await db.SaveChangesAsync(cancellationToken);

        var current = await BuildCurrentUserAsync(user.Id, session.Id, cancellationToken)
            ?? throw Problems.Unavailable();
        return new LoginResult(rawToken, session.ExpiresAt, current, session.CsrfSecret);
    }

    /// <summary>
    /// Resolves a session cookie to the caller's current identity. Roles and grants are read here,
    /// on every request; nothing about them is trusted from the cookie.
    /// </summary>
    public async Task<ResolvedSession?> ResolveAsync(string rawToken, CancellationToken cancellationToken)
    {
        if (rawToken.Length != 64)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = HashToken(rawToken);
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
        if (session is null
            || session.RevokedAt is not null
            || session.ExpiresAt <= now
            || session.LastSeenAt + options.IdleTimeout <= now)
        {
            return null;
        }

        var user = await BuildCurrentUserAsync(session.UserId, session.Id, cancellationToken, session.SecurityStamp);
        if (user is null)
        {
            return null;
        }

        if (now - session.LastSeenAt > TimeSpan.FromSeconds(60))
        {
            await db.Sessions.Where(s => s.Id == session.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now), cancellationToken);
        }

        return new ResolvedSession(user, session.CsrfSecret);
    }

    /// <summary>
    /// Re-reads the caller's identity for the same session: null when the session was revoked, expired
    /// or its user changed; otherwise the roles and grants as they are now. Used before delivering a
    /// result that took long enough to produce for access to have changed meanwhile.
    /// </summary>
    public async Task<CurrentUser?> RefreshAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == user.SessionId, cancellationToken);
        if (session is null
            || session.RevokedAt is not null
            || session.ExpiresAt <= now
            || session.LastSeenAt + options.IdleTimeout <= now)
        {
            return null;
        }

        return await BuildCurrentUserAsync(session.UserId, session.Id, cancellationToken, session.SecurityStamp);
    }

    private async Task<CurrentUser?> BuildCurrentUserAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken, Guid? expectedStamp = null)
    {
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles)
            .Include(u => u.Grants)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || !user.IsActive || (expectedStamp is { } stamp && stamp != user.SecurityStamp))
        {
            return null;
        }

        return new CurrentUser(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.Roles.Select(r => r.Role).ToHashSet(StringComparer.Ordinal),
            user.Grants.ToDictionary(g => g.WarehouseId, g => g.Level),
            user.PoApprovalLimit,
            sessionId,
            user.MustChangePassword);
    }

    public async Task LogoutAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Sessions.Where(s => s.Id == user.SessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), cancellationToken);
    }

    public async Task<MeDto> MeAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        var ids = user.Grants.Keys.ToArray();
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .OrderBy(w => w.Code)
            .Select(w => new { w.Id, w.Code, w.Name })
            .ToListAsync(cancellationToken);

        return new MeDto(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.Roles.Order(StringComparer.Ordinal).ToArray(),
            warehouses.Select(w => new WarehouseAccessDto(w.Id, w.Code, w.Name, user.LevelFor(w.Id).ToString())).ToArray(),
            user.MustChangePassword,
            user.PoApprovalLimit);
    }

    /// <summary>Changes the password, signs out every other session and keeps the current one valid.</summary>
    public async Task ChangePasswordAsync(CurrentUser current, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == current.Id, cancellationToken);
        if (!hasher.Verify(user.PasswordHash, currentPassword))
        {
            throw Problems.Validation("currentPassword", "invalid", "Mevcut parola doğru değil.");
        }

        ValidateNewPassword(newPassword, user.UserName, options.PasswordMinLength);
        if (hasher.Verify(user.PasswordHash, newPassword))
        {
            throw Problems.Validation("newPassword", "same_as_current", "Yeni parola mevcut paroladan farklı olmalıdır.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        user.PasswordHash = hasher.Hash(newPassword);
        user.MustChangePassword = false;
        user.RotateSecurityStamp();
        audit.Write(current, "auth.password_changed", "User", user.Id);
        await db.SaveChangesAsync(cancellationToken);

        await db.Sessions.Where(s => s.UserId == user.Id && s.Id != current.SessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), cancellationToken);
        await db.Sessions.Where(s => s.Id == current.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, user.SecurityStamp), cancellationToken);
    }

    public static void ValidateNewPassword(string password, string userName, int minLength)
    {
        if (password.Length < minLength || password.Length > 128)
        {
            throw Problems.Validation("newPassword", "length", $"Parola {minLength}–128 karakter olmalıdır.");
        }

        if (password.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            throw Problems.Validation("newPassword", "contains_user_name", "Parola kullanıcı adını içeremez.");
        }

        var classes = (password.Any(char.IsLower) ? 1 : 0) + (password.Any(char.IsUpper) ? 1 : 0)
            + (password.Any(char.IsDigit) ? 1 : 0) + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (classes < 3)
        {
            throw Problems.Validation("newPassword", "weak", "Parola küçük harf, büyük harf, rakam ve simgeden en az üçünü içermelidir.");
        }
    }
}
