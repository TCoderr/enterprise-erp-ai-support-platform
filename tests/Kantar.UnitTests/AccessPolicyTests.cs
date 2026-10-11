using Kantar.Domain.Identity;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Kantar.Domain.Support;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>
/// The authorization matrix of docs/02 §2.4, written out as data. The expectations below are the
/// document's table, not a copy of the implementation: each row names the roles that may act and the
/// lowest warehouse level they need.
/// </summary>
public sealed class AccessPolicyTests
{
    private static readonly Guid Warehouse = Guid.NewGuid();
    private static readonly Guid OtherWarehouse = Guid.NewGuid();
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly Guid SomeoneElse = Guid.NewGuid();

    private static readonly WarehouseLevel[] Levels = [WarehouseLevel.None, WarehouseLevel.View, WarehouseLevel.Operate, WarehouseLevel.Supervise];

    private static CurrentUser User(string[] roles, WarehouseLevel level = WarehouseLevel.None, decimal poLimit = 0, WarehouseLevel otherLevel = WarehouseLevel.None)
    {
        var grants = new Dictionary<Guid, WarehouseLevel>();
        if (level != WarehouseLevel.None)
        {
            grants[Warehouse] = level;
        }

        if (otherLevel != WarehouseLevel.None)
        {
            grants[OtherWarehouse] = otherLevel;
        }

        return new CurrentUser(Caller, "test.kullanici", "Test", new HashSet<string>(roles, StringComparer.Ordinal), grants, poLimit, Guid.NewGuid(), false);
    }

    // ---- warehouse-bound actions: role × level -------------------------------------------------

    /// <summary>docs/02 §2.4, rows with a "D ≥ …" cell: action → role → lowest sufficient level.</summary>
    private static readonly Dictionary<string, Dictionary<string, WarehouseLevel>> WarehouseActions = new(StringComparer.Ordinal)
    {
        ["ReadStock"] = new()
        {
            [Roles.SalesAgent] = WarehouseLevel.View, [Roles.SalesManager] = WarehouseLevel.View,
            [Roles.PurchasingAgent] = WarehouseLevel.View, [Roles.WarehouseOperator] = WarehouseLevel.View,
        },
        ["CreateSalesOrder"] = new() { [Roles.SalesAgent] = WarehouseLevel.View, [Roles.SalesManager] = WarehouseLevel.View },
        ["Ship"] = new() { [Roles.WarehouseOperator] = WarehouseLevel.Operate },
        ["CreatePurchaseOrder"] = new() { [Roles.PurchasingAgent] = WarehouseLevel.View },
        ["Receive"] = new() { [Roles.WarehouseOperator] = WarehouseLevel.Operate },
        ["Adjust"] = new() { [Roles.WarehouseOperator] = WarehouseLevel.Supervise },
    };

    private static bool Evaluate(string action, CurrentUser user) => action switch
    {
        "ReadStock" => AccessPolicy.CanReadStock(user, Warehouse),
        "CreateSalesOrder" => AccessPolicy.CanCreateSalesOrder(user, Warehouse),
        "Ship" => AccessPolicy.CanShip(user, Warehouse),
        "CreatePurchaseOrder" => AccessPolicy.CanCreatePurchaseOrder(user, Warehouse),
        "Receive" => AccessPolicy.CanReceive(user, Warehouse),
        "Adjust" => AccessPolicy.CanAdjust(user, Warehouse),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static TheoryData<string, string, WarehouseLevel, bool> WarehouseActionMatrix()
    {
        var data = new TheoryData<string, string, WarehouseLevel, bool>();
        foreach (var (action, allowed) in WarehouseActions)
        {
            foreach (var role in Roles.All)
            {
                foreach (var level in Levels)
                {
                    data.Add(action, role, level, allowed.TryGetValue(role, out var minimum) && level >= minimum);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WarehouseActionMatrix))]
    public void Warehouse_bound_action_needs_the_role_and_the_level_together(string action, string role, WarehouseLevel level, bool expected)
    {
        Assert.Equal(expected, Evaluate(action, User([role], level)));
    }

    [Theory]
    [MemberData(nameof(WarehouseActionMatrix))]
    public void A_grant_on_another_warehouse_never_counts(string action, string role, WarehouseLevel level, bool expected)
    {
        // Same role, same level, but the grant is for a different warehouse than the one acted on.
        _ = expected;
        Assert.False(Evaluate(action, User([role], WarehouseLevel.None, otherLevel: level)));
    }

    [Fact]
    public void The_matrix_covers_every_role_level_and_action()
    {
        Assert.Equal(WarehouseActions.Count * Roles.All.Count * Levels.Length, WarehouseActionMatrix().Count);
        Assert.Equal(7, Roles.All.Count);
    }

    // ---- transfer: both sides ------------------------------------------------------------------

    public static TheoryData<string, WarehouseLevel, WarehouseLevel, bool> TransferMatrix()
    {
        var data = new TheoryData<string, WarehouseLevel, WarehouseLevel, bool>();
        foreach (var role in Roles.All)
        {
            foreach (var from in Levels)
            {
                foreach (var to in Levels)
                {
                    data.Add(role, from, to, role == Roles.WarehouseOperator && from >= WarehouseLevel.Operate && to >= WarehouseLevel.Operate);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TransferMatrix))]
    public void Transfer_needs_operate_on_both_the_source_and_the_target(string role, WarehouseLevel from, WarehouseLevel to, bool expected)
    {
        var user = User([role], from, otherLevel: to);

        Assert.Equal(expected, AccessPolicy.CanTransfer(user, Warehouse, OtherWarehouse));
        Assert.Equal(expected, AccessPolicy.CanTransfer(User([role], to, otherLevel: from), OtherWarehouse, Warehouse));
    }

    // ---- master data ---------------------------------------------------------------------------

    public static TheoryData<string, bool, bool, bool, bool> MasterDataMatrix() => new()
    {
        // role, prices, customers, suppliers, manage
        { Roles.SalesAgent, true, true, false, false },
        { Roles.SalesManager, true, true, false, false },
        { Roles.PurchasingAgent, false, false, true, false },
        { Roles.WarehouseOperator, false, false, false, false },
        { Roles.SupportAgent, false, false, false, false },
        { Roles.SupportManager, false, false, false, false },
        { Roles.SystemAdmin, true, true, true, true },
    };

    [Theory]
    [MemberData(nameof(MasterDataMatrix))]
    public void Master_data_reads_and_management_follow_the_role_alone(string role, bool prices, bool customers, bool suppliers, bool manage)
    {
        // A warehouse grant, even the highest, adds nothing here.
        foreach (var level in Levels)
        {
            var user = User([role], level);
            Assert.Equal(prices, AccessPolicy.CanReadPrices(user));
            Assert.Equal(customers, AccessPolicy.CanReadCustomers(user));
            Assert.Equal(suppliers, AccessPolicy.CanReadSuppliers(user));
            Assert.Equal(manage, AccessPolicy.CanManageMasterData(user));
        }
    }

    // ---- sales order visibility ----------------------------------------------------------------

    private static readonly DateTime Released = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>An order in the given status that went through confirmation (every non-draft status here did).</summary>
    private static SalesOrder SalesOrderOf(Guid owner, SalesOrderStatus status) =>
        new() { Number = "SO-00000001", WarehouseId = Warehouse, OwnerUserId = owner, Status = status, ConfirmedAt = status == SalesOrderStatus.Draft ? null : Released };

    /// <summary>docs/02 §2.4 "Satış siparişi oku".</summary>
    private static DocumentView ExpectedSalesView(string role, WarehouseLevel level, bool owner, SalesOrderStatus status) => role switch
    {
        Roles.SalesAgent => level >= WarehouseLevel.View && owner ? DocumentView.Full : DocumentView.None,
        Roles.SalesManager => level >= WarehouseLevel.View ? DocumentView.Full : DocumentView.None,
        Roles.WarehouseOperator => level >= WarehouseLevel.Operate && status != SalesOrderStatus.Draft ? DocumentView.Fulfilment : DocumentView.None,
        _ => DocumentView.None,
    };

    public static TheoryData<string, WarehouseLevel, bool, SalesOrderStatus> SalesViewCases()
    {
        var data = new TheoryData<string, WarehouseLevel, bool, SalesOrderStatus>();
        foreach (var role in Roles.All)
        {
            foreach (var level in Levels)
            {
                foreach (var owner in new[] { true, false })
                {
                    foreach (var status in Enum.GetValues<SalesOrderStatus>())
                    {
                        data.Add(role, level, owner, status);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SalesViewCases))]
    public void Sales_order_view_follows_role_level_ownership_and_status(string role, WarehouseLevel level, bool owner, SalesOrderStatus status)
    {
        var user = User([role], level);
        var order = SalesOrderOf(owner ? Caller : SomeoneElse, status);
        var expected = ExpectedSalesView(role, level, owner, status);

        Assert.Equal(expected, AccessPolicy.ViewOf(user, order));

        // Edit, confirm and cancel are commercial decisions: exactly the callers with the full view.
        Assert.Equal(expected == DocumentView.Full, AccessPolicy.CanManageSalesOrder(user, order));
    }

    [Fact]
    public void Warehouse_operator_never_gets_the_full_view_and_never_sees_a_draft()
    {
        var supervisor = User([Roles.WarehouseOperator], WarehouseLevel.Supervise);

        Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(supervisor, SalesOrderOf(Caller, SalesOrderStatus.Draft)));
        foreach (var status in new[] { SalesOrderStatus.Confirmed, SalesOrderStatus.PartiallyShipped, SalesOrderStatus.Shipped, SalesOrderStatus.Cancelled })
        {
            Assert.Equal(DocumentView.Fulfilment, AccessPolicy.ViewOf(supervisor, SalesOrderOf(Caller, status)));
            Assert.False(AccessPolicy.CanManageSalesOrder(supervisor, SalesOrderOf(Caller, status)));
        }
    }

    [Fact]
    public void An_order_of_another_warehouse_is_invisible_whatever_the_role()
    {
        var order = new SalesOrder { Number = "SO-00000002", WarehouseId = OtherWarehouse, OwnerUserId = Caller, Status = SalesOrderStatus.Confirmed };

        foreach (var role in Roles.All)
        {
            Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(User([role], WarehouseLevel.Supervise), order));
        }
    }

    [Fact]
    public void Roles_combine_a_sales_agent_who_also_operates_the_warehouse()
    {
        var user = User([Roles.SalesAgent, Roles.WarehouseOperator], WarehouseLevel.Operate);

        Assert.Equal(DocumentView.Full, AccessPolicy.ViewOf(user, SalesOrderOf(Caller, SalesOrderStatus.Draft)));
        Assert.Equal(DocumentView.Fulfilment, AccessPolicy.ViewOf(user, SalesOrderOf(SomeoneElse, SalesOrderStatus.Confirmed)));
        Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(user, SalesOrderOf(SomeoneElse, SalesOrderStatus.Draft)));
        Assert.False(AccessPolicy.CanManageSalesOrder(user, SalesOrderOf(SomeoneElse, SalesOrderStatus.Confirmed)));
    }

    [Fact]
    public void A_document_cancelled_before_it_was_ever_released_stays_invisible_to_the_warehouse()
    {
        var supervisor = User([Roles.WarehouseOperator], WarehouseLevel.Supervise);
        var draftCancelledSale = new SalesOrder { Number = "SO-00000003", WarehouseId = Warehouse, OwnerUserId = Caller, Status = SalesOrderStatus.Cancelled, ConfirmedAt = null };
        var draftCancelledPurchase = new PurchaseOrder { Number = "PO-00000003", WarehouseId = Warehouse, OwnerUserId = Caller, Status = PurchaseOrderStatus.Cancelled, ApprovedAt = null };

        Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(supervisor, draftCancelledSale));
        Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(supervisor, draftCancelledPurchase));

        // The commercial owner still sees their own cancelled draft.
        Assert.Equal(DocumentView.Full, AccessPolicy.ViewOf(User([Roles.SalesAgent], WarehouseLevel.View), draftCancelledSale));
        Assert.Equal(DocumentView.Full, AccessPolicy.ViewOf(User([Roles.PurchasingAgent], WarehouseLevel.View), draftCancelledPurchase));
    }

    // ---- purchase order visibility -------------------------------------------------------------

    private static PurchaseOrder PurchaseOrderOf(Guid owner, PurchaseOrderStatus status) =>
        new() { Number = "PO-00000001", WarehouseId = Warehouse, OwnerUserId = owner, Status = status, ApprovedAt = status == PurchaseOrderStatus.Draft ? null : Released };

    /// <summary>docs/02 §2.4 "PO oku".</summary>
    private static DocumentView ExpectedPurchaseView(string role, WarehouseLevel level, PurchaseOrderStatus status) => role switch
    {
        Roles.PurchasingAgent => level >= WarehouseLevel.View ? DocumentView.Full : DocumentView.None,
        Roles.WarehouseOperator => level >= WarehouseLevel.Operate && status != PurchaseOrderStatus.Draft ? DocumentView.Fulfilment : DocumentView.None,
        _ => DocumentView.None,
    };

    public static TheoryData<string, WarehouseLevel, bool, PurchaseOrderStatus> PurchaseViewCases()
    {
        var data = new TheoryData<string, WarehouseLevel, bool, PurchaseOrderStatus>();
        foreach (var role in Roles.All)
        {
            foreach (var level in Levels)
            {
                foreach (var owner in new[] { true, false })
                {
                    foreach (var status in Enum.GetValues<PurchaseOrderStatus>())
                    {
                        data.Add(role, level, owner, status);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PurchaseViewCases))]
    public void Purchase_order_view_edit_approval_and_cancel_follow_the_matrix(string role, WarehouseLevel level, bool owner, PurchaseOrderStatus status)
    {
        var order = PurchaseOrderOf(owner ? Caller : SomeoneElse, status);
        var withoutLimit = User([role], level, poLimit: 0);
        var withLimit = User([role], level, poLimit: 5_000m);
        var expected = ExpectedPurchaseView(role, level, status);
        var full = expected == DocumentView.Full;

        Assert.Equal(expected, AccessPolicy.ViewOf(withoutLimit, order));
        Assert.Equal(expected, AccessPolicy.ViewOf(withLimit, order));

        // Draft editing is the owner's; any purchasing agent in scope may attempt approval (the
        // amount is checked by the transition); cancelling needs ownership or an approval limit.
        Assert.Equal(full && owner, AccessPolicy.CanEditPurchaseOrder(withoutLimit, order));
        Assert.Equal(full && owner, AccessPolicy.CanEditPurchaseOrder(withLimit, order));
        Assert.Equal(full, AccessPolicy.CanAttemptApproval(withoutLimit, order));
        Assert.Equal(full && owner, AccessPolicy.CanCancelPurchaseOrder(withoutLimit, order));
        Assert.Equal(full, AccessPolicy.CanCancelPurchaseOrder(withLimit, order));
    }

    // ---- SystemAdmin has no implicit commercial or stock access --------------------------------

    [Fact]
    public void System_admin_gets_no_document_view_and_no_stock_read_even_with_the_highest_grant()
    {
        foreach (var level in Levels)
        {
            var admin = User([Roles.SystemAdmin], level, poLimit: 1_000_000m, otherLevel: level);

            foreach (var status in Enum.GetValues<SalesOrderStatus>())
            {
                Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(admin, SalesOrderOf(Caller, status)));
                Assert.False(AccessPolicy.CanManageSalesOrder(admin, SalesOrderOf(Caller, status)));
            }

            foreach (var status in Enum.GetValues<PurchaseOrderStatus>())
            {
                Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(admin, PurchaseOrderOf(Caller, status)));
                Assert.False(AccessPolicy.CanAttemptApproval(admin, PurchaseOrderOf(Caller, status)));
                Assert.False(AccessPolicy.CanCancelPurchaseOrder(admin, PurchaseOrderOf(Caller, status)));
            }

            Assert.False(AccessPolicy.CanReadStock(admin, Warehouse));
            Assert.False(AccessPolicy.CanCreateSalesOrder(admin, Warehouse));
            Assert.False(AccessPolicy.CanCreatePurchaseOrder(admin, Warehouse));
            Assert.False(AccessPolicy.CanShip(admin, Warehouse));
            Assert.False(AccessPolicy.CanReceive(admin, Warehouse));
            Assert.False(AccessPolicy.CanAdjust(admin, Warehouse));
            Assert.False(AccessPolicy.CanTransfer(admin, Warehouse, OtherWarehouse));
        }
    }

    [Fact]
    public void A_user_without_any_role_can_do_nothing_with_a_supervise_grant()
    {
        var user = User([], WarehouseLevel.Supervise, otherLevel: WarehouseLevel.Supervise);

        Assert.False(AccessPolicy.CanReadStock(user, Warehouse));
        Assert.False(AccessPolicy.CanAdjust(user, Warehouse));
        Assert.False(AccessPolicy.CanTransfer(user, Warehouse, OtherWarehouse));
        Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(user, SalesOrderOf(Caller, SalesOrderStatus.Confirmed)));
        Assert.Equal(DocumentView.None, AccessPolicy.ViewOf(user, PurchaseOrderOf(Caller, PurchaseOrderStatus.Approved)));
    }

    // ---- support -------------------------------------------------------------------------------

    [Theory]
    [InlineData(Roles.SalesAgent, false)]
    [InlineData(Roles.SalesManager, false)]
    [InlineData(Roles.PurchasingAgent, false)]
    [InlineData(Roles.WarehouseOperator, false)]
    [InlineData(Roles.SupportAgent, true)]
    [InlineData(Roles.SupportManager, true)]
    [InlineData(Roles.SystemAdmin, false)]
    public void Tickets_are_visible_to_the_requester_and_to_support_staff(string role, bool seesOthers)
    {
        var user = User([role]);
        Ticket TicketOf(Guid requester) => new() { Number = "TK-00000001", Title = "Başlık", Description = "Açıklama", RequesterUserId = requester };

        Assert.True(AccessPolicy.CanSeeTicket(user, TicketOf(Caller)));
        Assert.Equal(seesOthers, AccessPolicy.CanSeeTicket(user, TicketOf(SomeoneElse)));
    }

    [Theory]
    [InlineData(Roles.SalesAgent, false, false, false)]
    [InlineData(Roles.SupportAgent, true, false, false)]
    [InlineData(Roles.SupportManager, true, true, true)]
    [InlineData(Roles.SystemAdmin, false, false, false)]
    public void Assignment_and_publishing_are_support_decisions(string role, bool assignSelf, bool assignOther, bool publish)
    {
        var user = User([role]);

        Assert.Equal(assignSelf, AccessPolicy.CanAssignTicket(user, Caller));
        Assert.Equal(assignOther, AccessPolicy.CanAssignTicket(user, SomeoneElse));
        Assert.Equal(publish, AccessPolicy.CanPublishKb(user));
    }

    // ---- knowledge base read rule (docs/02 §9.3) -----------------------------------------------

    private static bool IsSupport(string role) => role is Roles.SupportAgent or Roles.SupportManager;

    private static bool ExpectedVisibility(string role, KbVisibility visibility) => visibility switch
    {
        KbVisibility.General => true,
        KbVisibility.SupportOnly => IsSupport(role),
        KbVisibility.ManagerOnly => role is Roles.SupportManager or Roles.SalesManager,
        _ => false,
    };

    private static bool ExpectedModule(string role, AppModule module) => IsSupport(role) || module switch
    {
        AppModule.General or AppModule.Support => true,
        AppModule.Sales => role is Roles.SalesAgent or Roles.SalesManager,
        AppModule.Purchasing => role is Roles.PurchasingAgent,
        AppModule.Inventory => role is Roles.WarehouseOperator or Roles.SalesAgent or Roles.SalesManager or Roles.PurchasingAgent,
        _ => false,
    };

    public enum Scope
    {
        NoWarehouse,
        WarehouseWithoutGrant,
        WarehouseWithView,
    }

    public static TheoryData<string, KbVisibility, AppModule, Scope> KbCases()
    {
        var data = new TheoryData<string, KbVisibility, AppModule, Scope>();
        foreach (var role in Roles.All)
        {
            foreach (var visibility in Enum.GetValues<KbVisibility>())
            {
                foreach (var module in Enum.GetValues<AppModule>())
                {
                    foreach (var scope in Enum.GetValues<Scope>())
                    {
                        data.Add(role, visibility, module, scope);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KbCases))]
    public void Published_kb_document_is_readable_only_when_visibility_module_and_warehouse_all_allow(string role, KbVisibility visibility, AppModule module, Scope scope)
    {
        var user = User([role], scope == Scope.WarehouseWithView ? WarehouseLevel.View : WarehouseLevel.None);
        var document = new KbDocument
        {
            Slug = "ornek-belge", Visibility = visibility, Module = module, Status = KbStatus.Published, PublishedVersionId = Guid.NewGuid(),
            WarehouseId = scope == Scope.NoWarehouse ? null : Warehouse,
        };
        var warehouseAllows = scope != Scope.WarehouseWithoutGrant || IsSupport(role);
        var expected = ExpectedVisibility(role, visibility) && ExpectedModule(role, module) && warehouseAllows;

        Assert.Equal(expected, AccessPolicy.CanReadKb(user, document));
    }

    [Theory]
    [InlineData(KbStatus.Draft)]
    [InlineData(KbStatus.Retired)]
    public void Draft_and_retired_kb_documents_are_readable_by_nobody(KbStatus status)
    {
        var document = new KbDocument { Slug = "taslak", Visibility = KbVisibility.General, Module = AppModule.General, Status = status };

        foreach (var role in Roles.All)
        {
            Assert.False(AccessPolicy.CanReadKb(User([role], WarehouseLevel.Supervise), document));
        }
    }

    [Fact]
    public void Kb_examples_from_the_document()
    {
        KbDocument Doc(KbVisibility visibility, AppModule module, Guid? warehouse = null) =>
            new() { Slug = "ornek", Visibility = visibility, Module = module, WarehouseId = warehouse, Status = KbStatus.Published };

        // SystemAdmin does not read support-only or manager-only documents.
        Assert.True(AccessPolicy.CanReadKb(User([Roles.SystemAdmin]), Doc(KbVisibility.General, AppModule.General)));
        Assert.False(AccessPolicy.CanReadKb(User([Roles.SystemAdmin]), Doc(KbVisibility.SupportOnly, AppModule.General)));
        Assert.False(AccessPolicy.CanReadKb(User([Roles.SystemAdmin]), Doc(KbVisibility.ManagerOnly, AppModule.General)));

        // A sales manager reads manager-only sales documents, but the module rule still applies.
        Assert.True(AccessPolicy.CanReadKb(User([Roles.SalesManager]), Doc(KbVisibility.ManagerOnly, AppModule.Sales)));
        Assert.False(AccessPolicy.CanReadKb(User([Roles.SalesManager]), Doc(KbVisibility.ManagerOnly, AppModule.Purchasing)));

        // Warehouse scope: a grant on a different warehouse does not open the document.
        var operatorElsewhere = User([Roles.WarehouseOperator], WarehouseLevel.None, otherLevel: WarehouseLevel.Supervise);
        Assert.False(AccessPolicy.CanReadKb(operatorElsewhere, Doc(KbVisibility.General, AppModule.Inventory, Warehouse)));
        Assert.True(AccessPolicy.CanReadKb(operatorElsewhere, Doc(KbVisibility.General, AppModule.Inventory, OtherWarehouse)));

        // Support staff read warehouse-scoped documents without a grant.
        Assert.True(AccessPolicy.CanReadKb(User([Roles.SupportAgent]), Doc(KbVisibility.SupportOnly, AppModule.Inventory, Warehouse)));
        Assert.False(AccessPolicy.CanReadKb(User([Roles.SupportAgent]), Doc(KbVisibility.ManagerOnly, AppModule.Inventory, Warehouse)));
    }
}
