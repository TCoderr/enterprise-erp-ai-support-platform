namespace Kantar.Domain.Identity;

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string UserName { get; set; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }

    /// <summary>Changes on password change/reset, role change and deactivation; every session carries a copy.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public decimal PoApprovalLimit { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<UserRole> Roles { get; set; } = [];
    public List<WarehouseGrant> Grants { get; set; } = [];

    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid();
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public required string Role { get; set; }
}

public sealed class WarehouseGrant
{
    public Guid UserId { get; set; }
    public Guid WarehouseId { get; set; }
    public WarehouseLevel Level { get; set; }
}

public sealed class Session
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required byte[] TokenHash { get; set; }
    public required byte[] CsrfSecret { get; set; }
    public Guid SecurityStamp { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
