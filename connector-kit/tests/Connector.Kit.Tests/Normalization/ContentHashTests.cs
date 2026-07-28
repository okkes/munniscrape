using Connector.Kit.Normalization;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// Two properties, both load-bearing: the hash must not move when a
/// provider reorders or adds something we ignore, and it must move when any
/// meaningful value changes. That is what makes re-fetch overlap free,
/// which is what lets every adapter widen its window to catch late-settling
/// rows without duplicating them.
/// </summary>
public sealed class ContentHashTests
{
    private static Transaction Tx() => Make.Tx("t-1", -4_231, resultingBalance: 10_000) with
    {
        Description = "Betaalautomaat 19-07-2026 17:42",
        Counterparty = new Counterparty { Name = "JUMBO 1234 UTRECHT", Iban = "NL91INGB0417164300" },
        ValueAt = new DateOnly(2026, 7, 19),
        Kind = TransactionKind.CardPayment,
    };

    [Theory]
    [InlineData("transaction")]
    [InlineData("receipt")]
    [InlineData("account")]
    public void Has_the_documented_shape(string kind)
    {
        var hash = kind switch
        {
            "transaction" => ContentHash.Of(Tx()),
            "receipt" => ContentHash.Of(Make.Rcp(1_000, Make.Item("Kaas", 1_000))),
            _ => ContentHash.Of(Make.Acc("NL91INGB0417164300", balance: 1_250_045)),
        };

        Assert.Matches("^sha256:[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Is_deterministic_across_calls()
    {
        // Not a tautology: a hash that folded in a timestamp, a GUID or a
        // dictionary iteration order would fail here and nowhere else.
        Assert.Equal(ContentHash.Of(Tx()), ContentHash.Of(Tx()));
        Assert.Equal(
            ContentHash.Of(Make.Rcp(1_000, Make.Item("Kaas", 1_000))),
            ContentHash.Of(Make.Rcp(1_000, Make.Item("Kaas", 1_000))));
    }

    [Fact]
    public void Ignores_the_fields_that_are_not_the_content()
    {
        var baseline = ContentHash.Of(Tx());

        // The platform-minted id, the value date, the classification and the
        // running balance all move without the transaction changing. Folding
        // them in would churn the hash on every re-fetch and defeat dedupe.
        Assert.Equal(baseline, ContentHash.Of(Tx() with { Id = Ids.New(Ids.Transaction) }));
        Assert.Equal(baseline, ContentHash.Of(Tx() with { ValueAt = new DateOnly(2026, 8, 1) }));
        Assert.Equal(baseline, ContentHash.Of(Tx() with { ValueAt = null }));
        Assert.Equal(baseline, ContentHash.Of(Tx() with { Kind = TransactionKind.Transfer }));
        Assert.Equal(baseline, ContentHash.Of(Tx() with { ResultingBalance = null }));
        Assert.Equal(baseline, ContentHash.Of(Tx() with { ContentHash = "sha256:whatever" }));
    }

    [Fact]
    public void Changes_when_the_amount_changes()
    {
        Assert.NotEqual(ContentHash.Of(Tx()), ContentHash.Of(Tx() with { Amount = Money.Eur(-4_232) }));
    }

    [Fact]
    public void Changes_when_the_currency_changes()
    {
        // -4231 EUR and -4231 USD are not the same fact, and a hash that
        // said they were would silently dedupe one away.
        Assert.NotEqual(ContentHash.Of(Tx()), ContentHash.Of(Tx() with { Amount = new Money(-4_231, "USD") }));
    }

    [Theory]
    [InlineData("external")]
    [InlineData("account")]
    [InlineData("booked")]
    [InlineData("description")]
    [InlineData("counterparty-name")]
    [InlineData("counterparty-iban")]
    public void Changes_when_a_meaningful_value_changes(string field)
    {
        var changed = field switch
        {
            "external" => Tx() with { ExternalId = "t-2" },
            "account" => Tx() with { AccountId = "acc_other" },
            "booked" => Tx() with { BookedAt = new DateOnly(2026, 7, 20) },
            "description" => Tx() with { Description = "Betaalautomaat 19-07-2026 17:43" },
            "counterparty-name" => Tx() with
            {
                Counterparty = Tx().Counterparty! with { Name = "JUMBO 5678 AMSTERDAM" },
            },
            _ => Tx() with { Counterparty = Tx().Counterparty! with { Iban = "NL02ABNA0123456789" } },
        };

        Assert.NotEqual(ContentHash.Of(Tx()), ContentHash.Of(changed));
    }

    [Fact]
    public void Field_boundaries_cannot_be_rearranged_into_a_collision()
    {
        // The case named in the kit's own comment.
        var ab_c = Make.Tx("ab", 100) with { AccountId = "c" };
        var a_bc = Make.Tx("a", 100) with { AccountId = "bc" };

        Assert.NotEqual(ContentHash.Of(ab_c), ContentHash.Of(a_bc));
    }

    [Fact]
    public void A_separator_inside_a_value_cannot_forge_a_field_boundary()
    {
        // The case a separator alone does not survive: concatenating
        // "ab<sep>c" + sep + "d" and "ab" + sep + "c<sep>d" produces
        // identical bytes. Only the length prefix separates them, so this is
        // the test that actually exercises it.
        // Numeric escape, as the kit writes it: source carrying an invisible
        // control character is source nobody can review.
        const char separator = (char)0x1F;

        var left = Make.Tx($"ab{separator}c", 100) with { AccountId = "d" };
        var right = Make.Tx("ab", 100) with { AccountId = $"c{separator}d" };

        Assert.NotEqual(ContentHash.Of(left), ContentHash.Of(right));
    }

    [Fact]
    public void A_receipt_hash_moves_when_a_detail_fetch_fills_in_its_items()
    {
        var summary = Make.Rcp(1_000);
        var detailed = Make.Rcp(1_000, Make.Item("Kaas", 1_000));

        // The summary and the detailed record are genuinely different
        // content, so the second fetch must not be deduped away as a repeat.
        Assert.NotEqual(ContentHash.Of(summary), ContentHash.Of(detailed));
    }

    [Fact]
    public void A_receipt_hash_moves_when_an_item_changes()
    {
        var baseline = ContentHash.Of(Make.Rcp(1_000, Make.Item("Kaas", 1_000)));

        Assert.NotEqual(baseline, ContentHash.Of(Make.Rcp(1_000, Make.Item("Melk", 1_000))));
        Assert.NotEqual(baseline, ContentHash.Of(Make.Rcp(1_000, Make.Item("Kaas", 999))));
        Assert.NotEqual(
            baseline,
            ContentHash.Of(Make.Rcp(1_000, Make.Item("Kaas", 1_000) with { Quantity = 2m })));
    }

    [Fact]
    public void A_receipt_hash_moves_when_items_are_reordered()
    {
        // Item order is content: two lines of the same product at different
        // prices are not interchangeable on a printed receipt either.
        var one = Make.Rcp(1_000, Make.Item("A", 400), Make.Item("B", 600));
        var other = Make.Rcp(1_000, Make.Item("B", 600), Make.Item("A", 400));

        Assert.NotEqual(ContentHash.Of(one), ContentHash.Of(other));
    }

    [Fact]
    public void A_receipt_hash_is_offset_independent()
    {
        // The same instant written +02:00 or in UTC is the same purchase.
        // A provider that switches representation must not re-emit
        // everything it ever sold us.
        var local = Make.Rcp(1_000, Make.Item("Kaas", 1_000));
        var utc = local with { PurchasedAt = local.PurchasedAt.ToUniversalTime() };

        Assert.NotEqual(local.PurchasedAt.Offset, utc.PurchasedAt.Offset);
        Assert.Equal(ContentHash.Of(local), ContentHash.Of(utc));
    }

    [Fact]
    public void A_receipt_hash_moves_when_the_purchase_instant_moves()
    {
        var baseline = Make.Rcp(1_000, Make.Item("Kaas", 1_000));

        Assert.NotEqual(
            ContentHash.Of(baseline),
            ContentHash.Of(baseline with { PurchasedAt = baseline.PurchasedAt.AddMinutes(1) }));
    }

    [Fact]
    public void An_account_hash_ignores_the_balance_timestamp()
    {
        var account = Make.Acc("NL91INGB0417164300", balance: 1_250_045);
        var later = account with
        {
            Balance = account.Balance! with { AsOf = TestTime.Anchor.AddHours(6) },
        };

        // as_of moves on every single fetch. Folding it in would make every
        // account row look new, forever.
        Assert.Equal(ContentHash.Of(account), ContentHash.Of(later));
    }

    [Fact]
    public void An_account_hash_moves_when_the_balance_or_type_moves()
    {
        var account = Make.Acc("NL91INGB0417164300", balance: 1_250_045);

        Assert.NotEqual(
            ContentHash.Of(account),
            ContentHash.Of(account with { Balance = account.Balance! with { Amount = Money.Eur(1_250_046) } }));
        Assert.NotEqual(ContentHash.Of(account), ContentHash.Of(account with { Type = AccountType.Current }));
        Assert.NotEqual(ContentHash.Of(account), ContentHash.Of(account with { DisplayName = "Renamed" }));
        Assert.NotEqual(ContentHash.Of(account), ContentHash.Of(account with { Iban = "NL02ABNA0123456789" }));
        Assert.NotEqual(ContentHash.Of(account), ContentHash.Of(account with { Currency = "USD" }));
        Assert.NotEqual(ContentHash.Of(account), ContentHash.Of(account with { Balance = null }));
    }

    [Fact]
    public void Rejects_a_null_record_rather_than_hashing_nothing()
    {
        Assert.Throws<ArgumentNullException>(() => ContentHash.Of((Transaction)null!));
        Assert.Throws<ArgumentNullException>(() => ContentHash.Of((Receipt)null!));
        Assert.Throws<ArgumentNullException>(() => ContentHash.Of((Account)null!));
    }

    [Fact]
    public void An_absent_optional_reads_the_same_as_an_empty_one()
    {
        // Providers differ on null versus "" for a counterparty, and a
        // provider that switches between them has not changed the fact.
        var absent = Tx() with { Counterparty = null, Description = null };
        var empty = Tx() with
        {
            Counterparty = new Counterparty { Name = null, Iban = null },
            Description = string.Empty,
        };

        Assert.Equal(ContentHash.Of(absent), ContentHash.Of(empty));
    }
}
