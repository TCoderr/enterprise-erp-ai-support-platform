using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Kantar.Domain.Support;

namespace Kantar.Domain.Identity;

public enum DocumentView
{
    /// <summary>Caller must not learn that the document exists.</summary>
    None,

    /// <summary>Quantities and fulfilment data only; no prices, totals or commercial notes.</summary>
    Fulfilment,
    Full,
}

/// <summary>
/// The authorization matrix of docs/02 §2.4 as pure functions. Role alone never grants a
/// warehouse-bound action, and SystemAdmin gets no implicit commercial or stock access.
/// </summary>
public static class AccessPolicy
{
    // ---- master data ----
    public static bool CanManageMasterData(CurrentUser u) => u.Is(Roles.SystemAdmin);

    public static bool CanReadPrices(CurrentUser u) => u.IsAny(Roles.SalesAgent, Roles.SalesManager, Roles.SystemAdmin);

    public static bool CanReadCustomers(CurrentUser u) => u.IsAny(Roles.SalesAgent, Roles.SalesManager, Roles.SystemAdmin);

    public static bool CanReadSuppliers(CurrentUser u) => u.IsAny(Roles.PurchasingAgent, Roles.SystemAdmin);

    // ---- inventory ----
    private static bool HasStockRole(CurrentUser u) =>
        u.IsAny(Roles.SalesAgent, Roles.SalesManager, Roles.PurchasingAgent, Roles.WarehouseOperator);

    public static bool CanReadStock(CurrentUser u, Guid warehouseId) =>
        HasStockRole(u) && u.HasLevel(warehouseId, WarehouseLevel.View);

    public static bool CanTransfer(CurrentUser u, Guid fromWarehouseId, Guid toWarehouseId) =>
        u.Is(Roles.WarehouseOperator)
        && u.HasLevel(fromWarehouseId, WarehouseLevel.Operate)
        && u.HasLevel(toWarehouseId, WarehouseLevel.Operate);

    public static bool CanAdjust(CurrentUser u, Guid warehouseId) =>
        u.Is(Roles.WarehouseOperator) && u.HasLevel(warehouseId, WarehouseLevel.Supervise);

    // ---- sales ----
    public static bool HasSalesRole(CurrentUser u) => u.IsAny(Roles.SalesAgent, Roles.SalesManager);

    public static bool CanCreateSalesOrder(CurrentUser u, Guid warehouseId) =>
        HasSalesRole(u) && u.HasLevel(warehouseId, WarehouseLevel.View);

    public static DocumentView ViewOf(CurrentUser u, SalesOrder order)
    {
        if (u.HasLevel(order.WarehouseId, WarehouseLevel.View))
        {
            if (u.Is(Roles.SalesManager) || (u.Is(Roles.SalesAgent) && order.OwnerUserId == u.Id))
            {
                return DocumentView.Full;
            }
        }

        if (u.Is(Roles.WarehouseOperator)
            && u.HasLevel(order.WarehouseId, WarehouseLevel.Operate)
            && WasReleasedToWarehouse(order))
        {
            return DocumentView.Fulfilment;
        }

        return DocumentView.None;
    }

    /// <summary>
    /// The warehouse sees a sales order only once it has been confirmed. A draft that was cancelled
    /// without ever being confirmed never became warehouse work and stays invisible to it.
    /// </summary>
    public static bool WasReleasedToWarehouse(SalesOrder order) =>
        order.Status is SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyShipped or SalesOrderStatus.Shipped
        || (order.Status == SalesOrderStatus.Cancelled && order.ConfirmedAt is not null);

    /// <summary>Same rule for purchase orders: approved at some point, or it never concerned the warehouse.</summary>
    public static bool WasReleasedToWarehouse(PurchaseOrder order) =>
        order.Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received
        || (order.Status == PurchaseOrderStatus.Cancelled && order.ApprovedAt is not null);

    /// <summary>Edit, confirm and cancel are commercial decisions: owner or sales manager, never the warehouse.</summary>
    public static bool CanManageSalesOrder(CurrentUser u, SalesOrder order) => ViewOf(u, order) == DocumentView.Full;

    public static bool CanShip(CurrentUser u, Guid warehouseId) =>
        u.Is(Roles.WarehouseOperator) && u.HasLevel(warehouseId, WarehouseLevel.Operate);

    // ---- purchasing ----
    public static bool CanCreatePurchaseOrder(CurrentUser u, Guid warehouseId) =>
        u.Is(Roles.PurchasingAgent) && u.HasLevel(warehouseId, WarehouseLevel.View);

    public static DocumentView ViewOf(CurrentUser u, PurchaseOrder order)
    {
        if (u.Is(Roles.PurchasingAgent) && u.HasLevel(order.WarehouseId, WarehouseLevel.View))
        {
            return DocumentView.Full;
        }

        if (u.Is(Roles.WarehouseOperator)
            && u.HasLevel(order.WarehouseId, WarehouseLevel.Operate)
            && WasReleasedToWarehouse(order))
        {
            return DocumentView.Fulfilment;
        }

        return DocumentView.None;
    }

    public static bool CanEditPurchaseOrder(CurrentUser u, PurchaseOrder order) =>
        ViewOf(u, order) == DocumentView.Full && order.OwnerUserId == u.Id;

    /// <summary>Approval needs the role and scope here; the amount check lives in the state transition.</summary>
    public static bool CanAttemptApproval(CurrentUser u, PurchaseOrder order) => ViewOf(u, order) == DocumentView.Full;

    public static bool CanCancelPurchaseOrder(CurrentUser u, PurchaseOrder order) =>
        ViewOf(u, order) == DocumentView.Full && (order.OwnerUserId == u.Id || u.PoApprovalLimit > 0);

    public static bool CanReceive(CurrentUser u, Guid warehouseId) =>
        u.Is(Roles.WarehouseOperator) && u.HasLevel(warehouseId, WarehouseLevel.Operate);

    // ---- support ----
    public static bool CanSeeTicket(CurrentUser u, Ticket ticket) => u.IsSupportStaff || ticket.RequesterUserId == u.Id;

    public static bool CanAssignTicket(CurrentUser u, Guid assigneeUserId) =>
        u.Is(Roles.SupportManager) || (u.Is(Roles.SupportAgent) && assigneeUserId == u.Id);

    public static bool CanPublishKb(CurrentUser u) => u.Is(Roles.SupportManager);

    // ---- knowledge base read rule (docs/02 §9.3) ----
    public static IReadOnlyList<KbVisibility> ReadableVisibilities(CurrentUser u)
    {
        var list = new List<KbVisibility> { KbVisibility.General };
        if (u.IsSupportStaff)
        {
            list.Add(KbVisibility.SupportOnly);
        }

        if (u.IsAny(Roles.SupportManager, Roles.SalesManager))
        {
            list.Add(KbVisibility.ManagerOnly);
        }

        return list;
    }

    public static IReadOnlyList<AppModule> ReadableModules(CurrentUser u)
    {
        if (u.IsSupportStaff)
        {
            return [AppModule.General, AppModule.Sales, AppModule.Purchasing, AppModule.Inventory, AppModule.Support];
        }

        var list = new List<AppModule> { AppModule.General, AppModule.Support };
        if (HasSalesRole(u))
        {
            list.Add(AppModule.Sales);
        }

        if (u.Is(Roles.PurchasingAgent))
        {
            list.Add(AppModule.Purchasing);
        }

        if (HasStockRole(u))
        {
            list.Add(AppModule.Inventory);
        }

        return list;
    }

    /// <summary>Support staff read warehouse-scoped documents everywhere; others need at least View on that warehouse.</summary>
    public static bool BypassesKbWarehouseScope(CurrentUser u) => u.IsSupportStaff;

    public static bool CanReadKb(CurrentUser u, KbDocument doc) =>
        doc.Status == KbStatus.Published
        && ReadableVisibilities(u).Contains(doc.Visibility)
        && ReadableModules(u).Contains(doc.Module)
        && (doc.WarehouseId is null || BypassesKbWarehouseScope(u) || u.HasLevel(doc.WarehouseId.Value, WarehouseLevel.View));
}
