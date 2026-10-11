using Kantar.Domain.Common;
using Kantar.Domain.MasterData;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>docs/02 §2.3 and §6: approval limit, receipt arithmetic and the purchase order state machine.</summary>
public sealed class PurchaseOrderStateTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly Supplier Supplier = new() { Code = "S-1", Name = "Sentetik Tedarikçi" };

    public enum Transition
    {
        ReplaceLines,
        Approve,
        Receive,
        Cancel,
    }

    private static Product NewProduct(int index) =>
        new() { Sku = $"SKU-{index}", Name = $"Ürün {index}", Unit = "ADET", ListPrice = 99m };

    /// <summary>Unit cost 10.00 per line, so the total is ten times the ordered quantity.</summary>
    private static PurchaseOrder Draft(params int[] quantities)
    {
        var order = new PurchaseOrder { Number = "PO-00000001" };
        order.ReplaceLines(quantities.Select((q, i) => (NewProduct(i), q, 10.00m)).ToArray(), Now);
        return order;
    }

    private static PurchaseOrder Approved(params int[] quantities)
    {
        var order = Draft(quantities);
        order.Approve(Supplier, Approver, 1_000_000m, Now);
        return order;
    }

    private static PurchaseOrder InStatus(PurchaseOrderStatus status)
    {
        var order = Draft(10, 10);
        switch (status)
        {
            case PurchaseOrderStatus.Draft:
                break;
            case PurchaseOrderStatus.Approved:
                order.Approve(Supplier, Approver, 1_000_000m, Now);
                break;
            case PurchaseOrderStatus.PartiallyReceived:
                order.Approve(Supplier, Approver, 1_000_000m, Now);
                order.Receive([new LineQuantity(order.Lines[0].Id, 4)], Now);
                break;
            case PurchaseOrderStatus.Received:
                order.Approve(Supplier, Approver, 1_000_000m, Now);
                order.Receive(order.Lines.Select(l => new LineQuantity(l.Id, l.OrderedQty)).ToArray(), Now);
                break;
            case PurchaseOrderStatus.Cancelled:
                order.Cancel("Test iptali", Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        Assert.Equal(status, order.Status);
        return order;
    }

    private static void Apply(PurchaseOrder order, Transition transition)
    {
        switch (transition)
        {
            case Transition.ReplaceLines:
                order.ReplaceLines([(NewProduct(7), 3, 1.00m)], Now);
                break;
            case Transition.Approve:
                order.Approve(Supplier, Approver, 1_000_000m, Now);
                break;
            case Transition.Receive:
                order.Receive([new LineQuantity(order.Lines[^1].Id, 1)], Now);
                break;
            case Transition.Cancel:
                order.Cancel("Tedarikçi teslim edemiyor", Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition));
        }
    }

    private static (PurchaseOrderStatus Status, (int Ordered, int Received, int Cancelled)[] Lines) Snapshot(PurchaseOrder order) =>
        (order.Status, order.Lines.Select(l => (l.OrderedQty, l.ReceivedQty, l.CancelledQty)).ToArray());

    private static void AssertArithmetic(PurchaseOrder order)
    {
        foreach (var line in order.Lines)
        {
            Assert.Equal(line.OrderedQty, line.ReceivedQty + line.CancelledQty + line.OpenQty);
            Assert.True(line.ReceivedQty >= 0 && line.CancelledQty >= 0 && line.OpenQty >= 0);
        }
    }

    // ---- transition table --------------------------------------------------------------------

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, Transition.ReplaceLines, PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Draft, Transition.Approve, PurchaseOrderStatus.Approved)]
    [InlineData(PurchaseOrderStatus.Draft, Transition.Cancel, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Approved, Transition.Receive, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.Approved, Transition.Cancel, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, Transition.Receive, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, Transition.Cancel, PurchaseOrderStatus.Cancelled)]
    public void Allowed_transition_moves_to_the_documented_status(PurchaseOrderStatus from, Transition transition, PurchaseOrderStatus expected)
    {
        var order = InStatus(from);

        Apply(order, transition);

        Assert.Equal(expected, order.Status);
        AssertArithmetic(order);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, Transition.Receive)]
    [InlineData(PurchaseOrderStatus.Approved, Transition.ReplaceLines)]
    [InlineData(PurchaseOrderStatus.Approved, Transition.Approve)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, Transition.ReplaceLines)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, Transition.Approve)]
    [InlineData(PurchaseOrderStatus.Received, Transition.ReplaceLines)]
    [InlineData(PurchaseOrderStatus.Received, Transition.Approve)]
    [InlineData(PurchaseOrderStatus.Received, Transition.Receive)]
    [InlineData(PurchaseOrderStatus.Received, Transition.Cancel)]
    [InlineData(PurchaseOrderStatus.Cancelled, Transition.ReplaceLines)]
    [InlineData(PurchaseOrderStatus.Cancelled, Transition.Approve)]
    [InlineData(PurchaseOrderStatus.Cancelled, Transition.Receive)]
    [InlineData(PurchaseOrderStatus.Cancelled, Transition.Cancel)]
    public void Forbidden_transition_is_invalid_state_and_changes_nothing(PurchaseOrderStatus from, Transition transition)
    {
        var order = InStatus(from);
        var before = Snapshot(order);

        var ex = Assert.Throws<DomainException>(() => Apply(order, transition));

        Assert.Equal(ErrorKind.Conflict, ex.Kind);
        Assert.Equal("invalid_state", ex.Code);
        var after = Snapshot(order);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Lines, after.Lines);
    }

    [Fact]
    public void Every_status_and_step_pair_is_covered_by_the_two_tables()
    {
        var allowed = 0;
        var forbidden = 0;
        foreach (var status in Enum.GetValues<PurchaseOrderStatus>())
        {
            foreach (var transition in Enum.GetValues<Transition>())
            {
                var order = InStatus(status);
                try
                {
                    Apply(order, transition);
                    allowed++;
                }
                catch (DomainException ex) when (ex.Code == "invalid_state")
                {
                    forbidden++;
                }
            }
        }

        Assert.Equal((7, 13), (allowed, forbidden));
    }

    // ---- draft -------------------------------------------------------------------------------

    [Fact]
    public void Draft_lines_copy_the_product_snapshot_and_keep_the_entered_cost()
    {
        var order = new PurchaseOrder { Number = "PO-00000002" };
        var (first, second) = (NewProduct(1), NewProduct(2));

        order.ReplaceLines([(first, 4, 12.50m), (second, 10, 0m)], Now);

        Assert.Equal([1, 2], order.Lines.Select(l => l.LineNo));
        Assert.Equal([(first.Id, "SKU-1", 12.50m, 4), (second.Id, "SKU-2", 0m, 10)], order.Lines.Select(l => (l.ProductId, l.Sku, l.UnitCost, l.OrderedQty)));
        Assert.Equal(50m, order.Total);
        Assert.False(order.HasReceipts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Draft_needs_between_one_and_fifty_lines(int count)
    {
        var lines = Enumerable.Range(0, count).Select(_ => (Guid.NewGuid(), 1, 1m)).ToArray();

        var ex = Assert.Throws<DomainException>(() => PurchaseOrder.ValidateDraftLines(lines));

        Assert.Contains(ex.Errors, e => e is { Field: "lines", Code: "line_count" });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_001)]
    public void Ordered_quantity_out_of_range_is_rejected(int quantity)
    {
        var ex = Assert.Throws<DomainException>(() => PurchaseOrder.ValidateDraftLines([(Guid.NewGuid(), quantity, 1m)]));

        Assert.Contains(ex.Errors, e => e is { Field: "lines[0].quantity", Code: "out_of_range" });
    }

    [Theory]
    [InlineData("-0.01", false)]
    [InlineData("0", true)]
    [InlineData("10.5", true)]
    [InlineData("10.55", true)]
    [InlineData("10.555", false)]
    public void Unit_cost_is_not_negative_and_has_at_most_two_decimals(string cost, bool valid)
    {
        var value = decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture);
        void Validate() => PurchaseOrder.ValidateDraftLines([(Guid.NewGuid(), 1, value)]);

        if (valid)
        {
            Validate();
            return;
        }

        var ex = Assert.Throws<DomainException>(Validate);
        Assert.Contains(ex.Errors, e => e is { Field: "lines[0].unitCost", Code: "invalid_amount" });
    }

    [Fact]
    public void The_same_product_may_appear_only_once()
    {
        var product = Guid.NewGuid();

        var ex = Assert.Throws<DomainException>(() => PurchaseOrder.ValidateDraftLines([(product, 1, 1m), (product, 2, 1m)]));

        Assert.Contains(ex.Errors, e => e is { Field: "lines[1].productId", Code: "duplicate_product" });
    }

    // ---- approval limit ----------------------------------------------------------------------

    [Fact]
    public void A_limit_of_zero_approves_nothing_not_even_a_free_order()
    {
        var priced = Draft(1);
        var free = new PurchaseOrder { Number = "PO-00000003" };
        free.ReplaceLines([(NewProduct(1), 5, 0m)], Now);

        foreach (var order in new[] { priced, free })
        {
            var ex = Assert.Throws<DomainException>(() => order.Approve(Supplier, Approver, 0m, Now));

            Assert.Equal(ErrorKind.Forbidden, ex.Kind);
            Assert.Equal("approval_limit_exceeded", ex.Code);
            Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
            Assert.Null(order.ApprovedBy);
        }
    }

    [Fact]
    public void A_limit_below_the_total_is_refused()
    {
        var order = Draft(10, 5); // total 150.00

        var ex = Assert.Throws<DomainException>(() => order.Approve(Supplier, Approver, 149.99m, Now));

        Assert.Equal(ErrorKind.Forbidden, ex.Kind);
        Assert.Equal("approval_limit_exceeded", ex.Code);
        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Null(order.ApprovedAt);
    }

    [Fact]
    public void A_limit_equal_to_the_total_approves_and_freezes_the_supplier()
    {
        var order = Draft(10, 5); // total 150.00

        order.Approve(Supplier, Approver, 150.00m, Now);

        Assert.Equal(PurchaseOrderStatus.Approved, order.Status);
        Assert.Equal((Approver, Now), (order.ApprovedBy, order.ApprovedAt));
        Assert.Equal((Supplier.Id, "S-1", "Sentetik Tedarikçi"), (order.SupplierId, order.SupplierCode, order.SupplierName));
    }

    [Fact]
    public void An_order_without_lines_cannot_be_approved()
    {
        var order = new PurchaseOrder { Number = "PO-00000004" };

        var ex = Assert.Throws<DomainException>(() => order.Approve(Supplier, Approver, 1_000m, Now));

        Assert.Equal("invalid_state", ex.Code);
    }

    // ---- receive -----------------------------------------------------------------------------

    [Fact]
    public void Partial_receipts_add_up_and_the_order_closes_on_the_last_unit()
    {
        var order = Approved(10, 4);
        var (a, b) = (order.Lines[0], order.Lines[1]);

        var first = order.Receive([new LineQuantity(a.Id, 3)], Now);
        Assert.Equal([new LineEffect(a.Id, a.ProductId, 3)], first);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, order.Status);
        Assert.Equal((3, 0, 7), (a.ReceivedQty, a.CancelledQty, a.OpenQty));
        Assert.True(order.HasReceipts);

        order.Receive([new LineQuantity(b.Id, 4), new LineQuantity(a.Id, 6)], Now);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, order.Status);

        order.Receive([new LineQuantity(a.Id, 1)], Now.AddMinutes(1));
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        Assert.Equal(Now.AddMinutes(1), order.ClosedAt);
        AssertArithmetic(order);
        Assert.All(order.Lines, l => Assert.Equal((l.OrderedQty, 0, 0), (l.ReceivedQty, l.CancelledQty, l.OpenQty)));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(100_000)]
    public void Receiving_more_than_open_is_exceeds_remaining_and_applies_no_line(int quantity)
    {
        var order = Approved(10, 10);
        var before = Snapshot(order);

        var ex = Assert.Throws<DomainException>(() => order.Receive(
            [new LineQuantity(order.Lines[0].Id, 5), new LineQuantity(order.Lines[1].Id, quantity)], Now));

        Assert.Equal(ErrorKind.Conflict, ex.Kind);
        Assert.Equal("exceeds_remaining", ex.Code);
        Assert.Equal(["lines[1].quantity"], ex.Errors.Select(e => e.Field));
        Assert.Equal(before.Lines, Snapshot(order).Lines);
        Assert.Equal(PurchaseOrderStatus.Approved, order.Status);
    }

    [Fact]
    public void There_is_no_over_receipt_after_a_partial_receipt_either()
    {
        var order = Approved(10);
        order.Receive([new LineQuantity(order.Lines[0].Id, 7)], Now);

        var ex = Assert.Throws<DomainException>(() => order.Receive([new LineQuantity(order.Lines[0].Id, 4)], Now));

        Assert.Equal("exceeds_remaining", ex.Code);
        Assert.Equal(7, order.Lines[0].ReceivedQty);
    }

    [Fact]
    public void A_line_of_another_order_is_line_mismatch_and_applies_no_line()
    {
        var order = Approved(10, 10);
        var other = Approved(10);
        var before = Snapshot(order);

        var ex = Assert.Throws<DomainException>(() => order.Receive(
            [new LineQuantity(order.Lines[0].Id, 1), new LineQuantity(other.Lines[0].Id, 1)], Now));

        Assert.Equal(ErrorKind.Unprocessable, ex.Kind);
        Assert.Equal("line_mismatch", ex.Code);
        Assert.Equal(["lines[1].orderLineId"], ex.Errors.Select(e => e.Field));
        Assert.Equal(before.Lines, Snapshot(order).Lines);
        Assert.Equal(0, other.Lines[0].ReceivedQty);
    }

    [Fact]
    public void The_same_line_twice_in_one_receipt_is_line_mismatch()
    {
        var order = Approved(10, 10);
        var line = order.Lines[0].Id;

        var ex = Assert.Throws<DomainException>(() => order.Receive([new LineQuantity(line, 2), new LineQuantity(line, 3)], Now));

        Assert.Equal("line_mismatch", ex.Code);
        Assert.Equal(0, order.Lines[0].ReceivedQty);
    }

    [Fact]
    public void Receipt_quantity_must_be_at_least_one_and_a_receipt_needs_a_line()
    {
        var order = Approved(10);

        var zero = Assert.Throws<DomainException>(() => order.Receive([new LineQuantity(order.Lines[0].Id, 0)], Now));
        var empty = Assert.Throws<DomainException>(() => order.Receive([], Now));

        Assert.Equal(ErrorKind.Validation, zero.Kind);
        Assert.Equal(ErrorKind.Validation, empty.Kind);
        Assert.Equal(PurchaseOrderStatus.Approved, order.Status);
    }

    // ---- cancel ------------------------------------------------------------------------------

    [Fact]
    public void Cancelling_a_draft_closes_it_without_touching_quantities()
    {
        var order = Draft(10);

        order.Cancel("Yanlış açıldı", Now);

        Assert.Equal(PurchaseOrderStatus.Cancelled, order.Status);
        Assert.Equal("Yanlış açıldı", order.CancelReason);
        Assert.Equal((0, 0), (order.Lines[0].ReceivedQty, order.Lines[0].CancelledQty));
    }

    [Fact]
    public void Cancelling_an_approved_order_closes_every_ordered_unit()
    {
        var order = Approved(10, 5);

        order.Cancel("Tedarikçi teslim edemiyor", Now);

        Assert.All(order.Lines, l => Assert.Equal((0, l.OrderedQty, 0), (l.ReceivedQty, l.CancelledQty, l.OpenQty)));
        AssertArithmetic(order);
    }

    [Fact]
    public void Cancelling_after_a_partial_receipt_keeps_what_was_received_and_closes_only_the_remainder()
    {
        var order = Approved(10, 5, 3);
        var (a, b, c) = (order.Lines[0], order.Lines[1], order.Lines[2]);
        order.Receive([new LineQuantity(a.Id, 4), new LineQuantity(c.Id, 3)], Now);

        order.Cancel("Kalan gelmeyecek", Now.AddHours(1));

        Assert.Equal(PurchaseOrderStatus.Cancelled, order.Status);
        Assert.Equal((4, 6, 0), (a.ReceivedQty, a.CancelledQty, a.OpenQty));
        Assert.Equal((0, 5, 0), (b.ReceivedQty, b.CancelledQty, b.OpenQty));
        Assert.Equal((3, 0, 0), (c.ReceivedQty, c.CancelledQty, c.OpenQty));
        Assert.Equal(Now.AddHours(1), order.ClosedAt);
        Assert.True(order.HasReceipts);
        AssertArithmetic(order);
    }
}
