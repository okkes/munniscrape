using Connector.Kit.Normalization;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// A receipt's items are a redundant statement of its total. Checking that
/// redundancy is what catches a misread decimal separator or a dropped
/// discount line - errors that otherwise produce plausible output no human
/// reviewer would spot.
/// </summary>
public sealed class ReconciliationTests
{
    [Fact]
    public void Items_and_discounts_sum_to_the_stated_total()
    {
        // The worked example from connector-api-spec.md §4: two cartons at
        // 1.19 with a "2e halve prijs" discount, plus one more line.
        var receipt = Make.Rcp(1_208, Make.Item("Melk halfvol 1L", 238, discountMinorUnits: -30), Make.Item("Kaas", 1_000));

        Assert.True(Reconciliation.ItemsReconcile(receipt));
    }

    [Fact]
    public void A_discount_that_was_not_negated_breaks_the_sum()
    {
        // The exact bug the invariant exists to catch: a provider states its
        // discounts positive and an adapter forgets to negate them.
        var receipt = Make.Rcp(1_208, Make.Item("Melk halfvol 1L", 238, discountMinorUnits: 30), Make.Item("Kaas", 1_000));

        Assert.False(Reconciliation.ItemsReconcile(receipt));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]        // the provider's own rounding
    [InlineData(-1, true)]
    [InlineData(2, false)]
    [InlineData(-2, false)]
    public void Tolerates_exactly_one_cent(long drift, bool reconciles)
    {
        var receipt = Make.Rcp(1_000 + drift, Make.Item("Kaas", 1_000));

        Assert.Equal(reconciles, Reconciliation.ItemsReconcile(receipt));
    }

    [Fact]
    public void The_tolerance_is_one_cent_and_stated_as_such()
    {
        Assert.Equal(1L, Reconciliation.ToleranceMinorUnits);
    }

    [Fact]
    public void A_receipt_with_no_items_has_nothing_to_check_against()
    {
        // A list-only fetch has no line items yet. Flagging it would mark
        // every summary record suspect for a reason that is not a defect.
        Assert.True(Reconciliation.ItemsReconcile(Make.Rcp(4_231)));
    }

    [Fact]
    public void A_receipt_that_does_not_reconcile_is_flagged_and_not_dropped()
    {
        var receipt = Make.Rcp(9_999, Make.Item("Kaas", 1_000));

        var stamped = receipt.WithReconciliation();

        // Dropping it would hide a real purchase; trusting it silently
        // would hand over a total we know contradicts its own contents.
        Assert.False(stamped.Reconciled);
        Assert.Equal(receipt.ExternalId, stamped.ExternalId);
        Assert.Equal(receipt.Total, stamped.Total);
        Assert.Equal(receipt.Items.Count, stamped.Items.Count);
    }

    [Fact]
    public void A_receipt_that_reconciles_is_stamped_true()
    {
        var stamped = Make.Rcp(1_000, Make.Item("Kaas", 1_000)).WithReconciliation();

        Assert.True(stamped.Reconciled);
    }

    [Fact]
    public void Reconciled_defaults_to_true_so_a_summary_record_is_not_born_suspect()
    {
        Assert.True(Make.Rcp(4_231).Reconciled);
    }

    [Fact]
    public void Rejects_a_null_receipt_rather_than_reporting_success()
    {
        Assert.Throws<ArgumentNullException>(() => Reconciliation.ItemsReconcile(null!));
    }

    [Fact]
    public void A_negative_total_still_reconciles_against_negative_items()
    {
        // A refund receipt. The check is arithmetic, not a sign convention,
        // so nothing here special-cases the direction the money moved.
        var refund = Make.Rcp(-1_238, Make.Item("Retour kaas", -1_000), Make.Item("Retour melk", -238));

        Assert.True(Reconciliation.ItemsReconcile(refund));
    }

    [Fact]
    public void Every_item_discount_participates()
    {
        // Two discount lines, so a loop that only reads the first is caught.
        var receipt = Make.Rcp(940, Make.Item("A", 500, -30), Make.Item("B", 500, -30));

        Assert.True(Reconciliation.ItemsReconcile(receipt));
    }
}
