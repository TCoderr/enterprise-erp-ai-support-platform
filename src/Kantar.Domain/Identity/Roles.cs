namespace Kantar.Domain.Identity;

public static class Roles
{
    public const string SalesAgent = "SalesAgent";
    public const string SalesManager = "SalesManager";
    public const string PurchasingAgent = "PurchasingAgent";
    public const string WarehouseOperator = "WarehouseOperator";
    public const string SupportAgent = "SupportAgent";
    public const string SupportManager = "SupportManager";
    public const string SystemAdmin = "SystemAdmin";

    public static readonly IReadOnlyList<string> All =
    [
        SalesAgent, SalesManager, PurchasingAgent, WarehouseOperator, SupportAgent, SupportManager, SystemAdmin,
    ];

    public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);
}

public enum WarehouseLevel : byte
{
    None = 0,
    View = 1,
    Operate = 2,
    Supervise = 3,
}
