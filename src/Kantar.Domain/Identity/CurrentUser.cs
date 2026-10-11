namespace Kantar.Domain.Identity;

/// <summary>
/// The caller as read from the database for this request. It is never rebuilt from cookie
/// contents, so a role or grant removed a moment ago is already absent here.
/// </summary>
public sealed record CurrentUser(
    Guid Id,
    string UserName,
    string DisplayName,
    IReadOnlySet<string> Roles,
    IReadOnlyDictionary<Guid, WarehouseLevel> Grants,
    decimal PoApprovalLimit,
    Guid SessionId,
    bool MustChangePassword)
{
    public bool Is(string role) => Roles.Contains(role);

    public bool IsAny(params ReadOnlySpan<string> roles)
    {
        foreach (var role in roles)
        {
            if (Roles.Contains(role))
            {
                return true;
            }
        }

        return false;
    }

    public WarehouseLevel LevelFor(Guid warehouseId) =>
        Grants.TryGetValue(warehouseId, out var level) ? level : WarehouseLevel.None;

    public bool HasLevel(Guid warehouseId, WarehouseLevel required) => LevelFor(warehouseId) >= required;

    public bool IsSupportStaff => IsAny(Identity.Roles.SupportAgent, Identity.Roles.SupportManager);

    public Guid[] WarehousesWith(WarehouseLevel required) =>
        Grants.Where(g => g.Value >= required).Select(g => g.Key).ToArray();
}
