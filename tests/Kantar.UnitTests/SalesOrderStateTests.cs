using Kantar.Domain.Common;
using Kantar.Domain.MasterData;
using Kantar.Domain.Sales;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>docs/02 §5: line arithmetic and the sales order state machine, without a database.</summary>
public sealed class SalesOrderStateTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    private static readonly Customer Customer = new()
    {
        Code = "C-1", Name = "Sentetik Müşteri", TaxNumber = "1234567890", Address = "Örnek Sk. No:1",
    };

    public enum Transition
    {
        ReplaceLines,
        Confirm,
        Ship,
        Cancel,
    }

    private static Product NewProduct(int index, decimal price = 10m) =>
        new() { Sku = $"SKU-{index}", Name = $"Ürün {index}", Unit = "ADET", ListPrice = price };

    private static SalesOrder Draft(params int[] quantities)
    {
        var order = new SalesOrder { Number = "SO-00000001" };
        order.ReplaceLines(quantities.Select((q, i) => (NewProduct(i), q)).ToArray(), Now);
        return order;
    }

    private static SalesOrder Confirmed(params int[] quantities)
    {
        var order = Draft(quantities);
        order.Confirm(Customer, Now);
        return order;
    }

    /// <summary>An order with two lines of 10 driven into the requested status through the public transitions only.</summary>
    private static SalesOrder InStatus(SalesOrderStatus status)
    {
        var order = Draft(10, 10);
        switch (status)
        {
            case SalesOrderStatus.Draft:
                break;
            case SalesOrderStatus.Confirmed:
                order.Confirm(Customer, Now);
                break;
            case SalesOrderStatus.PartiallyShipped:
                order.Confirm(Customer, Now);
                order.Ship([new LineQuantity(order.Lines[0].Id, 4)], Now);
                break;
            case SalesOrderStatus.Shipped:
                order.Confirm(Customer, Now);
                order.Ship(order.Lines.Select(l => new LineQuantity(l.Id, l.OrderedQty)).ToArray(), Now);
                break;
            case SalesOrderStatus.Cancelled:
                order.Cancel("Test iptali", Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        Assert.Equal(status, order.Status);
        return order;
    }

    private static void Apply(SalesOrder order, Transition action)
    {
        switch (action)
        {
            case Transition.ReplaceLines:
                order.ReplaceLines([(NewProduct(7), 3)], Now);
                break;
            case Transition.Confirm:
                order.Confirm(Customer, Now);
                break;
            case Transition.Ship:
                order.Ship([new LineQuantity(order.Lines[^1].Id, 1)], Now);
                break;
            case Transition.Cancel:
                order.Cancel("Müşteri vazgeçti", Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static (SalesOrderStatus Status, (int Ordered, int Shipped, int Cancelled)[] Lines) Snapshot(SalesOrder order) =>
        (order.Status, order.Lines.Select(l => (l.OrderedQty, l.ShippedQty, l.CancelledQty)).ToArray());

    private static void AssertArithmetic(SalesOrder order)
    {
        foreach (var line in order.Lines)
        {
            Assert.Equal(line.OrderedQty, line.ShippedQty + line.CancelledQty + line.OpenQty);
            Assert.True(line.ShippedQty >= 0 && line.CancelledQty >= 0 && line.OpenQty >= 0);
        }
    }

    // ---- transition table --------------------------------------------------------------------

    [Theory]
    [InlineData(SalesOrderStatus.Draft, Transition.ReplaceLines, SalesOrderStatus.Draft)]
    [InlineData(SalesOrderStatus.Draft, Transition.Confirm, SalesOrderStatus.Confirmed)]
    [InlineData(SalesOrderStatus.Draft, Transition.Cancel, SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.Confirmed, Transition.Ship, SalesOrderStatus.PartiallyShipped)]
    [InlineData(SalesOrderStatus.Confirmed, Transition.Cancel, SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.PartiallyShipped, Transition.Ship, SalesOrderStatus.PartiallyShipped)]
    [InlineData(SalesOrderStatus.PartiallyShipped, Transition.Cancel, SalesOrderStatus.Cancelled)]
    public void Allowed_transition_moves_to_the_documented_status(SalesOrderStatus from, Transition action, SalesOrderStatus expected)
    {
        var order = InStatus(from);

        Apply(order, action);

        Assert.Equal(expected, order.Status);
        AssertArithmetic(order);
    }

    [Theory]
    [InlineData(SalesOrderStatus.Draft, Transition.Ship)]
    [InlineData(SalesOrderStatus.Confirmed, Transition.ReplaceLines)]
    [InlineData(SalesOrderStatus.Confirmed, Transition.Confirm)]
    [InlineData(SalesOrderStatus.PartiallyShipped, Transition.ReplaceLines)]
    [InlineData(SalesOrderStatus.PartiallyShipped, Transition.Confirm)]
    [InlineData(SalesOrderStatus.Shipped, Transition.ReplaceLines)]
    [InlineData(SalesOrderStatus.Shipped, Transition.Confirm)]
    [InlineData(SalesOrderStatus.Shipped, Transition.Ship)]
    [InlineData(SalesOrderStatus.Shipped, Transition.Cancel)]
    [InlineData(SalesOrderStatus.Cancelled, Transition.ReplaceLines)]
    [InlineData(SalesOrderStatus.Cancelled, Transition.Confirm)]
    [InlineData(SalesOrderStatus.Cancelled, Transition.Ship)]
    [InlineData(SalesOrderStatus.Cancelled, Transition.Cancel)]
    public void Forbidden_transition_is_invalid_state_and_changes_nothing(SalesOrderStatus from, Transition action)
    {
        var order = InStatus(from);
        var before = Snapshot(order);

        var ex = Assert.Throws<DomainException>(() => Apply(order, action));

        Assert.Equal(ErrorKind.Conflict, ex.Kind);
        Assert.Equal("invalid_state", ex.Code);
        var after = Snapshot(order);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Lines, after.Lines);
    }

    [Fact]
    public void Every_status_and_action_pair_is_covered_by_the_two_tables()
    {
        // 5 statuses × 4 actions = 20 pairs: 7 allowed + 13 forbidden above.
        var statuses = Enum.GetValues<SalesOrderStatus>();
        var actions = Enum.GetValues<Transition>();
        var allowed = 0;
        var forbidden = 0;
        foreach (var status in statuses)
        {
            foreach (var action in actions)
            {
                var order = InStatus(status);
                try
                {
                    Apply(order, action);
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
    public void Draft_lines_take_price_and_names_from_the_product_and_number_lines_from_one()
    {
        var order = new SalesOrder { Number = "SO-00000002" };
        var (first, second) = (NewProduct(1, 12.50m), NewProduct(2, 3m));

        order.ReplaceLines([(first, 4), (second, 10)], Now);

        Assert.Equal(SalesOrderStatus.Draft, order.Status);
        Assert.Equal([1, 2], order.Lines.Select(l => l.LineNo));
        Assert.Equal([(first.Id, "SKU-1", 12.50m, 4), (second.Id, "SKU-2", 3m, 10)], order.Lines.Select(l => (l.ProductId, l.Sku, l.UnitPrice, l.OrderedQty)));
        Assert.Equal(80m, order.Total);
        Assert.All(order.Lines, l => Assert.Equal((0, 0, l.OrderedQty), (l.ShippedQty, l.CancelledQty, l.OpenQty)));
        Assert.False(order.HasShipments);
    }

    [Fact]
    public void Rewriting_a_draft_takes_the_current_list_price()
    {
        var product = NewProduct(1, 10m);
        var order = new SalesOrder { Number = "SO-00000003" };
        order.ReplaceLines([(product, 2)], Now);
        product.ListPrice = 25m;
        Assert.Equal(10m, order.Lines[0].UnitPrice);

        order.ReplaceLines([(product, 2)], Now);

        Assert.Equal(25m, order.Lines[0].UnitPrice);
        Assert.Single(order.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Draft_needs_between_one_and_fifty_lines(int count)
    {
        var lines = Enumerable.Range(0, count).Select(_ => (Guid.NewGuid(), 1)).ToArray();

        var ex = Assert.Throws<DomainException>(() => SalesOrder.ValidateDraftLines(lines));

        Assert.Equal("validation_failed", ex.Code);
        Assert.Contains(ex.Errors, e => e is { Field: "lines", Code: "line_count" });
    }

    [Fact]
    public void Fifty_lines_are_accepted()
    {
        SalesOrder.ValidateDraftLines(Enumerable.Range(0, 50).Select(_ => (Guid.NewGuid(), 1)).ToArray());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(100_000, true)]
    [InlineData(100_001, false)]
    public void Ordered_quantity_is_between_one_and_one_hundred_thousand(int quantity, bool valid)
    {
        void Validate() => SalesOrder.ValidateDraftLines([(Guid.NewGuid(), 5), (Guid.NewGuid(), quantity)]);

        if (valid)
        {
            Validate();
            return;
        }

        var ex = Assert.Throws<DomainException>(Validate);
        Assert.Equal(ErrorKind.Validation, ex.Kind);
        Assert.Contains(ex.Errors, e => e is { Field: "lines[1].quantity", Code: "out_of_range" });
    }

    [Fact]
    public void The_same_product_may_appear_only_once()
    {
        var product = Guid.NewGuid();

        var ex = Assert.Throws<DomainException>(() => SalesOrder.ValidateDraftLines([(product, 1), (Guid.NewGuid(), 1), (product, 2)]));

        Assert.Contains(ex.Errors, e => e is { Field: "lines[2].productId", Code: "duplicate_product" });
    }

    // ---- confirm -----------------------------------------------------------------------------

    [Fact]
    public void Confirm_returns_every_line_to_reserve_in_line_order_and_freezes_the_customer()
    {
        var order = Draft(5, 7, 1);

        var effects = order.Confirm(Customer, Now);

        Assert.Equal(SalesOrderStatus.Confirmed, order.Status);
        Assert.Equal(Now, order.ConfirmedAt);
        Assert.Equal(order.Lines.Select(l => new LineEffect(l.Id, l.ProductId, l.OrderedQty)), effects);
        Assert.Equal((Customer.Id, "C-1", "Sentetik Müşteri", "1234567890", "Örnek Sk. No:1"),
            (order.CustomerId, order.CustomerCode, order.CustomerName, order.CustomerTaxNumber, order.ShippingAddress));
    }

    [Fact]
    public void An_order_without_lines_cannot_be_confirmed()
    {
        var order = new SalesOrder { Number = "SO-00000004" };

        var ex = Assert.Throws<DomainException>(() => order.Confirm(Customer, Now));

        Assert.Equal("invalid_state", ex.Code);
        Assert.Equal(SalesOrderStatus.Draft, order.Status);
    }

    // ---- ship --------------------------------------------------------------------------------

    [Fact]
    public void Partial_shipments_add_up_to_shipped_and_the_order_closes_on_the_last_unit()
    {
        var order = Confirmed(10, 4);
        var (a, b) = (order.Lines[0], order.Lines[1]);

        var first = order.Ship([new LineQuantity(a.Id, 3)], Now);
        Assert.Equal([new LineEffect(a.Id, a.ProductId, 3)], first);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, order.Status);
        Assert.Equal((3, 0, 7), (a.ShippedQty, a.CancelledQty, a.OpenQty));
        Assert.Null(order.ClosedAt);
        Assert.True(order.HasShipments);

        order.Ship([new LineQuantity(b.Id, 4), new LineQuantity(a.Id, 6)], Now);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, order.Status);
        Assert.Equal((9, 1), (a.ShippedQty, a.OpenQty));
        Assert.Equal((4, 0), (b.ShippedQty, b.OpenQty));

        var last = order.Ship([new LineQuantity(a.Id, 1)], Now.AddMinutes(5));
        Assert.Equal([new LineEffect(a.Id, a.ProductId, 1)], last);
        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
        Assert.Equal(Now.AddMinutes(5), order.ClosedAt);
        AssertArithmetic(order);
        Assert.All(order.Lines, l => Assert.Equal((l.OrderedQty, 0, 0), (l.ShippedQty, l.CancelledQty, l.OpenQty)));
    }

    [Fact]
    public void Shipping_everything_at_once_goes_straight_to_shipped()
    {
        var order = Confirmed(2, 3);

        order.Ship(order.Lines.Select(l => new LineQuantity(l.Id, l.OrderedQty)).ToArray(), Now);

        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(100_000)]
    public void Shipping_more_than_open_is_exceeds_remaining_and_applies_no_line(int quantity)
    {
        var order = Confirmed(10, 10);
        var before = Snapshot(order);

        // The first line is valid on its own; the request is still refused as a whole.
        var ex = Assert.Throws<DomainException>(() => order.Ship(
            [new LineQuantity(order.Lines[0].Id, 5), new LineQuantity(order.Lines[1].Id, quantity)], Now));

        Assert.Equal(ErrorKind.Conflict, ex.Kind);
        Assert.Equal("exceeds_remaining", ex.Code);
        Assert.Equal(["lines[1].quantity"], ex.Errors.Select(e => e.Field));
        Assert.Equal(before.Lines, Snapshot(order).Lines);
        Assert.Equal(SalesOrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Open_quantity_after_a_partial_shipment_is_the_new_ceiling()
    {
        var order = Confirmed(10);
        order.Ship([new LineQuantity(order.Lines[0].Id, 6)], Now);

        var ex = Assert.Throws<DomainException>(() => order.Ship([new LineQuantity(order.Lines[0].Id, 5)], Now));

        Assert.Equal("exceeds_remaining", ex.Code);
        Assert.Equal(6, order.Lines[0].ShippedQty);
        order.Ship([new LineQuantity(order.Lines[0].Id, 4)], Now);
        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
    }

    [Fact]
    public void A_line_of_another_order_is_line_mismatch_and_applies_no_line()
    {
        var order = Confirmed(10, 10);
        var other = Confirmed(10);
        var before = Snapshot(order);

        var ex = Assert.Throws<DomainException>(() => order.Ship(
            [new LineQuantity(order.Lines[0].Id, 1), new LineQuantity(other.Lines[0].Id, 1)], Now));

        Assert.Equal(ErrorKind.Unprocessable, ex.Kind);
        Assert.Equal("line_mismatch", ex.Code);
        Assert.Equal(["lines[1].orderLineId"], ex.Errors.Select(e => e.Field));
        Assert.Equal(before.Lines, Snapshot(order).Lines);
        Assert.Equal(0, other.Lines[0].ShippedQty);
    }

    [Fact]
    public void The_same_line_twice_in_one_shipment_is_line_mismatch()
    {
        var order = Confirmed(10, 10);
        var line = order.Lines[0].Id;

        var ex = Assert.Throws<DomainException>(() => order.Ship([new LineQuantity(line, 2), new LineQuantity(line, 3)], Now));

        Assert.Equal("line_mismatch", ex.Code);
        Assert.Equal(0, order.Lines[0].ShippedQty);
        Assert.Equal(SalesOrderStatus.Confirmed, order.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Shipment_quantity_must_be_at_least_one(int quantity)
    {
        var order = Confirmed(10);

        var ex = Assert.Throws<DomainException>(() => order.Ship([new LineQuantity(order.Lines[0].Id, quantity)], Now));

        Assert.Equal(ErrorKind.Validation, ex.Kind);
        Assert.Equal(0, order.Lines[0].ShippedQty);
    }

    [Fact]
    public void A_shipment_needs_at_least_one_line_and_no_more_lines_than_the_order_has()
    {
        var order = Confirmed(10);

        var empty = Assert.Throws<DomainException>(() => order.Ship([], Now));
        var tooMany = Assert.Throws<DomainException>(() => order.Ship(
            [new LineQuantity(order.Lines[0].Id, 1), new LineQuantity(Guid.NewGuid(), 1)], Now));

        Assert.Equal(ErrorKind.Validation, empty.Kind);
        Assert.Equal(ErrorKind.Validation, tooMany.Kind);
        Assert.Equal(SalesOrderStatus.Confirmed, order.Status);
    }

    // ---- cancel ------------------------------------------------------------------------------

    [Fact]
    public void Cancelling_a_draft_releases_nothing()
    {
        var order = Draft(10, 5);

        var released = order.Cancel("Yanlış açıldı", Now);

        Assert.Empty(released);
        Assert.Equal(SalesOrderStatus.Cancelled, order.Status);
        Assert.Equal("Yanlış açıldı", order.CancelReason);
        Assert.Equal(Now, order.ClosedAt);
        Assert.All(order.Lines, l => Assert.Equal((0, 0), (l.ShippedQty, l.CancelledQty)));
    }

    [Fact]
    public void Cancelling_a_confirmed_order_releases_every_ordered_unit()
    {
        var order = Confirmed(10, 5);

        var released = order.Cancel("Müşteri vazgeçti", Now);

        Assert.Equal(order.Lines.Select(l => new LineEffect(l.Id, l.ProductId, l.OrderedQty)), released);
        Assert.All(order.Lines, l => Assert.Equal((0, l.OrderedQty, 0), (l.ShippedQty, l.CancelledQty, l.OpenQty)));
        AssertArithmetic(order);
    }

    [Fact]
    public void Cancelling_after_a_partial_shipment_keeps_what_shipped_and_releases_only_the_remainder()
    {
        var order = Confirmed(10, 5, 3);
        var (a, b, c) = (order.Lines[0], order.Lines[1], order.Lines[2]);
        order.Ship([new LineQuantity(a.Id, 4), new LineQuantity(c.Id, 3)], Now);

        var released = order.Cancel("Kalan istenmedi", Now);

        // The fully shipped line has nothing left to release and is not in the list.
        Assert.Equal([new LineEffect(a.Id, a.ProductId, 6), new LineEffect(b.Id, b.ProductId, 5)], released);
        Assert.Equal(SalesOrderStatus.Cancelled, order.Status);
        Assert.Equal((4, 6, 0), (a.ShippedQty, a.CancelledQty, a.OpenQty));
        Assert.Equal((0, 5, 0), (b.ShippedQty, b.CancelledQty, b.OpenQty));
        Assert.Equal((3, 0, 0), (c.ShippedQty, c.CancelledQty, c.OpenQty));
        Assert.True(order.HasShipments);
        AssertArithmetic(order);
    }
}
