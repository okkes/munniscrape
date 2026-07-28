using System.Text.Json.Serialization;

namespace Connector.Kit.Normalization;

/// <summary>
/// One schema for every provider. Provider-shaped output is not an option:
/// the whole value of a connector is that a caller never learns which
/// provider a record came from.
/// </summary>
public abstract record NormalizedRecord
{
    /// <summary>Opaque, prefixed, minted by the control plane.</summary>
    public required string Id { get; init; }

    /// <summary>The provider's own id. Unique per session; drives dedupe.</summary>
    [JsonPropertyName("external_id")]
    public required string ExternalId { get; init; }

    /// <summary>
    /// Stable hash of the meaningful content. With
    /// <c>(session_id, external_id)</c> uniqueness this gives idempotency
    /// for free: a re-run never duplicates and a caller may safely retry.
    /// </summary>
    [JsonPropertyName("content_hash")]
    public string ContentHash { get; init; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter<AccountType>))]
public enum AccountType
{
    Current,
    Savings,
    CreditCard,
    Loan,
    Unknown,
}

public sealed record Account : NormalizedRecord
{
    public required AccountType Type { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    public string? Iban { get; init; }

    [JsonPropertyName("masked_number")]
    public string? MaskedNumber { get; init; }

    public required string Currency { get; init; }

    public Balance? Balance { get; init; }
}

public sealed record Balance
{
    public required Money Amount { get; init; }

    [JsonPropertyName("as_of")]
    public required DateTimeOffset AsOf { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionKind>))]
public enum TransactionKind
{
    CardPayment,
    Transfer,
    DirectDebit,
    Interest,
    Fee,
    Other,
}

public sealed record Transaction : NormalizedRecord
{
    [JsonPropertyName("account_id")]
    public required string AccountId { get; init; }

    [JsonPropertyName("booked_at")]
    public required DateOnly BookedAt { get; init; }

    [JsonPropertyName("value_at")]
    public DateOnly? ValueAt { get; init; }

    public required Money Amount { get; init; }

    public Counterparty? Counterparty { get; init; }

    public string? Description { get; init; }

    public TransactionKind Kind { get; init; } = TransactionKind.Other;

    /// <summary>
    /// The account balance after this transaction, where the provider states
    /// one. A redundant fact, and therefore a free integrity check - see
    /// <see cref="BalanceChain"/>.
    /// </summary>
    [JsonPropertyName("resulting_balance")]
    public Money? ResultingBalance { get; init; }
}

public sealed record Counterparty
{
    public string? Name { get; init; }

    public string? Iban { get; init; }
}

public sealed record Receipt : NormalizedRecord
{
    public required Merchant Merchant { get; init; }

    /// <summary>
    /// With a real offset, never a bare date: a near-midnight purchase
    /// otherwise matches the wrong day on the consumer's side.
    /// </summary>
    [JsonPropertyName("purchased_at")]
    public required DateTimeOffset PurchasedAt { get; init; }

    public required Money Total { get; init; }

    public ReceiptPayment? Payment { get; init; }

    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];

    /// <summary>
    /// False when the items and discounts do not sum to the stated total.
    /// The record is still emitted - with a warning rather than silently
    /// dropped - so the consumer can decide what to do with it.
    /// </summary>
    public bool Reconciled { get; init; } = true;
}

public sealed record Merchant
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    [JsonPropertyName("store_name")]
    public string? StoreName { get; init; }
}

public sealed record ReceiptPayment
{
    /// <summary><c>card</c>, <c>cash</c>, <c>ideal</c>, <c>other</c>.</summary>
    public string? Method { get; init; }

    /// <summary>
    /// Matching on amount and date alone is ambiguous - two identical
    /// purchases on one day are common. The payment tail is what
    /// disambiguates them, so null must be explicit rather than omitted:
    /// the consumer needs to know matching will be weaker.
    /// </summary>
    [JsonPropertyName("card_last4")]
    public string? CardLast4 { get; init; }

    [JsonPropertyName("iban_tail")]
    public string? IbanTail { get; init; }
}

/// <summary>
/// What a line on a receipt actually is.
///
/// A receipt is not a list of products: it is products, plus the charges and
/// credits a merchant adds around them - delivery, deposit, a coupon. The
/// consumer needs to tell those apart to show a sensible breakdown, and the
/// platform needs to count them all to reconcile against the stated total.
///
/// The total itself is unaffected by any of this. It stays the merchant's own
/// stated figure, because that is the number a bank transaction is matched
/// against, and a total we recomputed from lines would drift from the one the
/// user was actually charged.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReceiptLineKind>))]
public enum ReceiptLineKind
{
    /// <summary>Something bought. The default, and the only kind most lines are.</summary>
    Product,

    /// <summary>Delivery, service, bag, packaging - a charge that is not a good.</summary>
    Fee,

    /// <summary>Statiegeld and friends: refundable, and a real part of what was paid.</summary>
    Deposit,

    /// <summary>A negative line: coupon, loyalty discount, staff discount.</summary>
    Discount,

    /// <summary>Rounding, a correction, a merchant adjustment.</summary>
    Adjustment,

    /// <summary>
    /// DERIVED, not merchant data: the gap between the stated total and
    /// everything itemised. Emitted only when a merchant charges something it
    /// does not itemise, so a breakdown still adds up to what was paid.
    ///
    /// Labelled rather than folded into a <see cref="Fee"/> on purpose - we do
    /// not know what it was, and guessing a name for money is how a receipt
    /// stops being evidence.
    /// </summary>
    Unattributed,
}

public sealed record ReceiptItem
{
    public required string Name { get; init; }

    /// <summary>
    /// Defaults to <see cref="ReceiptLineKind.Product"/>, so an adapter that
    /// says nothing produces the same receipts it always did.
    /// </summary>
    public ReceiptLineKind Kind { get; init; } = ReceiptLineKind.Product;

    public decimal? Quantity { get; init; }

    [JsonPropertyName("unit_price")]
    public Money? UnitPrice { get; init; }

    public required Money Total { get; init; }

    /// <summary>
    /// Negative. Without discount lines a receipt's items do not sum to its
    /// total, which breaks reconciliation and misleads the consumer.
    /// </summary>
    public ReceiptDiscount? Discount { get; init; }
}

public sealed record ReceiptDiscount
{
    public required Money Amount { get; init; }

    public string? Label { get; init; }
}
