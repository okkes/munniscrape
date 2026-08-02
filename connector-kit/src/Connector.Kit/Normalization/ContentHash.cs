using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Connector.Kit.Normalization;

/// <summary>
/// A stable hash of the facts that make a record what it is.
///
/// Two properties matter and both are load-bearing: it must not change when
/// a provider reorders its JSON or adds a field we ignore, and it must
/// change when any meaningful value does. That is what makes re-fetch
/// overlap free, which in turn is what lets every adapter widen its window
/// to catch late-settling rows.
/// </summary>
public static class ContentHash
{
    /// <summary>
    /// ASCII unit separator. Written as a numeric escape rather than a
    /// literal so the source carries no invisible control characters.
    /// </summary>
    private const char Separator = (char)0x1F;

    public static string Of(Transaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        return Compute(
            tx.ExternalId,
            tx.AccountId,
            tx.BookedAt.ToString("O", CultureInfo.InvariantCulture),
            tx.Amount.Value.ToString(CultureInfo.InvariantCulture),
            tx.Amount.Currency,
            tx.Description ?? string.Empty,
            tx.Counterparty?.Name ?? string.Empty,
            tx.Counterparty?.Iban ?? string.Empty);
    }

    public static string Of(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var parts = new List<string>
        {
            receipt.ExternalId,
            receipt.Merchant.Id,
            receipt.PurchasedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            receipt.Total.Value.ToString(CultureInfo.InvariantCulture),
            receipt.Total.Currency,
            receipt.Items.Count.ToString(CultureInfo.InvariantCulture),
        };

        // Items participate so a detail fetch that later fills them in
        // produces a different hash - the summary and the detailed record
        // are genuinely different content.
        foreach (var item in receipt.Items)
        {
            parts.Add(item.Name);
            parts.Add(item.Total.Value.ToString(CultureInfo.InvariantCulture));
            parts.Add((item.Quantity ?? 0m).ToString(CultureInfo.InvariantCulture));
        }

        return Compute([.. parts]);
    }

    /// <summary>
    /// The facts a registry states about one credit.
    ///
    /// Status participates, unlike a receipt's reconciliation verdict: a
    /// credit moving from running to ended is the registry saying something
    /// new about it, not us reaching a different opinion about the same
    /// content. A consumer must see that as a changed record.
    /// </summary>
    public static string Of(CreditRegistration credit)
    {
        ArgumentNullException.ThrowIfNull(credit);
        return Compute(
            credit.ExternalId,
            credit.Creditor,
            credit.Kind.ToString(),
            credit.Amount.Value.ToString(CultureInfo.InvariantCulture),
            credit.Amount.Currency,
            credit.Status.ToString(),
            credit.StartedOn?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            credit.EndsOn?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            credit.MonthlyAmount?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            credit.ArrearsCode ?? string.Empty);
    }

    public static string Of(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Compute(
            account.ExternalId,
            account.Type.ToString(),
            account.DisplayName,
            account.Iban ?? string.Empty,
            account.Currency,
            account.Balance?.Amount.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>
    /// Length-prefixed so that no rearrangement of field boundaries can
    /// collide - ("ab","c") and ("a","bc") must not hash the same.
    /// </summary>
    private static string Compute(params string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(part.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(part)
                .Append(Separator);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }
}
