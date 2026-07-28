using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;

namespace BankConnector.Adapters.Parsing;

public sealed record BankStatementCsvOptions
{
    public required string SessionId { get; init; }

    public required string ProviderId { get; init; }

    /// <summary>
    /// Used when the export has no account column - a single-account export
    /// often does not repeat the account on every row. One of the two must
    /// resolve or the transactions have nowhere to attach.
    /// </summary>
    public string? AccountExternalId { get; init; }

    /// <summary>
    /// Dutch bank CSV exports carry no currency column at all; the account
    /// is in euros and the bank does not say so. Declared rather than
    /// assumed so a non-euro export is a one-line change, not a bug hunt.
    /// </summary>
    public string Currency { get; init; } = "EUR";

    public CsvDateOrder DateOrder { get; init; } = CsvDateOrder.DayFirst;
}

/// <summary>
/// The Dutch bank statement export, in whichever language and dialect the
/// user's online banking happened to produce.
///
/// The alias lists below are the whole point: they are the accumulated
/// knowledge of how banks spell these columns, and extending one is the
/// normal repair when a bank changes its export. Everything else about the
/// file - delimiter, decimal separator, date format, quoting, signed
/// amounts versus a separate Af/Bij column - is handled generically.
/// </summary>
public static class BankStatementCsv
{
    public const string ColumnDate = "date";
    public const string ColumnValueDate = "value_date";
    public const string ColumnAccount = "account";
    public const string ColumnCounterpartyIban = "counterparty_iban";
    public const string ColumnCounterpartyName = "counterparty_name";
    public const string ColumnAmount = "amount";
    public const string ColumnDirection = "direction";
    public const string ColumnDescription = "description";
    public const string ColumnCode = "code";
    public const string ColumnBalanceAfter = "balance_after";

    public static CsvSchema Schema(CsvDateOrder dateOrder = CsvDateOrder.DayFirst) => new()
    {
        Name = "bank-statement-csv",
        DateOrder = dateOrder,
        Columns =
        [
            new CsvColumnSpec
            {
                Key = ColumnDate,
                Aliases = ["datum", "boekingsdatum", "transactiedatum", "date", "booking date", "transaction date"],
            },
            new CsvColumnSpec
            {
                Key = ColumnAmount,
                Aliases = ["bedrag (eur)", "bedrag eur", "bedrag", "transactiebedrag", "amount (eur)", "amount eur", "amount"],
            },
            // Everything below is optional because at least one Dutch bank
            // omits each of them, and a missing description is not a reason
            // to refuse a statement.
            new CsvColumnSpec
            {
                Key = ColumnDirection,
                Required = false,
                Aliases = ["af bij", "af/bij", "bij/af", "debet/credit", "debit/credit", "debit credit", "bij af"],
            },
            new CsvColumnSpec
            {
                Key = ColumnValueDate,
                Required = false,
                Aliases = ["rentedatum", "valutadatum", "interest date", "value date"],
            },
            new CsvColumnSpec
            {
                Key = ColumnAccount,
                Required = false,
                Aliases = ["rekening", "rekeningnummer", "iban/bban", "eigen rekening", "account", "account number", "own account"],
            },
            new CsvColumnSpec
            {
                Key = ColumnCounterpartyIban,
                Required = false,
                Aliases = ["tegenrekening", "tegenrekening iban/bban", "iban tegenpartij", "counterparty iban", "opposing account", "contra account"],
            },
            new CsvColumnSpec
            {
                Key = ColumnCounterpartyName,
                Required = false,
                Aliases = ["naam tegenpartij", "naam / omschrijving", "naam tegenrekening", "naam", "counterparty name", "name / description", "name"],
            },
            new CsvColumnSpec
            {
                Key = ColumnDescription,
                Required = false,
                Aliases = ["mededelingen", "omschrijving", "omschrijving-1", "description", "remarks", "notifications"],
            },
            new CsvColumnSpec
            {
                Key = ColumnCode,
                Required = false,
                Aliases = ["mutatiesoort", "transactiesoort", "code", "transaction type", "mutation type"],
            },
            new CsvColumnSpec
            {
                Key = ColumnBalanceAfter,
                Required = false,
                Aliases = ["saldo na mutatie", "saldo na boeking", "saldo", "balance after mutation", "balance after", "balance"],
            },
        ],
    };

    /// <summary>
    /// Reads a statement export into normalised transactions, oldest-first
    /// as the file states them.
    ///
    /// The order is deliberately preserved rather than sorted: where the
    /// export carries a running balance the caller can hand the result
    /// straight to <see cref="BalanceChain"/>, and sorting first would hide
    /// the very reordering that check exists to catch.
    /// </summary>
    public static IReadOnlyList<Transaction> ReadTransactions(string text, BankStatementCsvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var schema = Schema(options.DateOrder);
        var table = LocaleTolerantCsv.Read(text, schema);

        var signed = !table.HasColumn(ColumnDirection);
        var hasBalance = table.HasColumn(ColumnBalanceAfter);
        var transactions = new List<Transaction>(table.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in table)
        {
            var accountExternalId = row.Text(ColumnAccount) ?? options.AccountExternalId
                ?? throw ConnectorException.ProviderChanged(
                    $"{options.ProviderId}: the export has no '{ColumnAccount}' column and no account was supplied by the adapter");

            var bookedAt = row.Date(ColumnDate);
            var amount = new Money(
                signed ? row.MinorUnits(ColumnAmount) : row.SignedMinorUnits(ColumnAmount, ColumnDirection),
                options.Currency);

            var name = row.Text(ColumnCounterpartyName);
            var iban = row.Text(ColumnCounterpartyIban);
            var description = row.Text(ColumnDescription);

            var accountId = BankRecords.AccountId(options.SessionId, accountExternalId);

            transactions.Add(BankRecords.NewTransaction(
                options.SessionId,
                accountId,
                // CSV exports carry no transaction reference at all, so the
                // id is synthesized from the row's own content plus an
                // occurrence counter - two identical purchases on one day
                // are common and must not collapse into one record.
                ExternalId(accountExternalId, bookedAt, amount, name, description, seen),
                bookedAt,
                amount,
                row.DateOrNull(ColumnValueDate),
                name is null && iban is null ? null : new Counterparty { Name = name, Iban = iban },
                description,
                Kind(row.Text(ColumnCode)),
                hasBalance
                    ? new Money(row.MinorUnits(ColumnBalanceAfter), options.Currency)
                    : null));
        }

        return transactions;
    }

    private static string ExternalId(
        string accountExternalId,
        DateOnly bookedAt,
        Money amount,
        string? counterparty,
        string? description,
        Dictionary<string, int> seen)
    {
        var seed = string.Create(CultureInfo.InvariantCulture,
            $"{accountExternalId}|{bookedAt:yyyy-MM-dd}|{amount.Value}|{counterparty}|{description}");

        seen.TryGetValue(seed, out var occurrence);
        seen[seed] = occurrence + 1;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{seed}|{occurrence}")));
        return "csv-" + Convert.ToHexStringLower(digest.AsSpan(0, 10));
    }

    /// <summary>
    /// Dutch banks put a two-letter mutation code in their exports. It is a
    /// UI hint only and never touches an amount, so an unknown code degrades
    /// to <see cref="TransactionKind.Other"/> rather than failing the run -
    /// the opposite of how an unparseable amount is treated.
    /// </summary>
    private static TransactionKind Kind(string? code) =>
        LocaleTolerantCsv.Normalize(code) switch
        {
            "ba" or "gm" or "betaalautomaat" or "pinnen" => TransactionKind.CardPayment,
            "gt" or "ov" or "vz" or "tb" or "overboeking" or "transfer" => TransactionKind.Transfer,
            "ic" or "dv" or "incasso" or "directdebit" => TransactionKind.DirectDebit,
            "re" or "rb" or "rente" or "interest" => TransactionKind.Interest,
            "kn" or "ks" or "kosten" or "fee" => TransactionKind.Fee,
            _ => TransactionKind.Other,
        };
}
