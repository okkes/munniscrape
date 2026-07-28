using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;

namespace BankConnector.Adapters.Parsing;

/// <summary>
/// What the caller has to tell the parser, because CAMT.053 does not say it.
/// </summary>
public sealed record Camt053Options
{
    public required string SessionId { get; init; }

    /// <summary>Named in every <see cref="ErrorCode.ProviderChanged"/> detail.</summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// CAMT.053 identifies an account but never says what KIND it is - the
    /// standard has no notion of "savings" versus "credit card". The adapter
    /// knows, because it drove the export form that produced the file, so it
    /// declares the type here rather than the parser guessing from a name.
    /// </summary>
    public AccountType AccountType { get; init; } = AccountType.Unknown;

    /// <summary>Overrides <c>Stmt/Acct/Nm</c>, which many banks omit.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>One <c>Stmt</c>: an account, its balances, and its entries.</summary>
public sealed record Camt053Statement
{
    public required string StatementId { get; init; }

    public required Account Account { get; init; }

    /// <summary>Oldest-first, in the file's own order.</summary>
    public required IReadOnlyList<Transaction> Transactions { get; init; }

    /// <summary><c>OPBD</c>, when the bank states one.</summary>
    public Balance? Opening { get; init; }

    /// <summary><c>CLBD</c>, when the bank states one.</summary>
    public Balance? Closing { get; init; }
}

/// <summary>
/// ISO 20022 CAMT.053 bank statements.
///
/// Two rules shape everything here, and they pull in opposite directions.
/// Optional elements are genuinely optional: banks differ in which of
/// <c>RmtInf</c>, <c>AddtlNtryInf</c>, <c>ValDt</c>, <c>RltdPties</c> and
/// <c>Bal</c> they populate, and that is a fixture problem, not a
/// code-branch problem. But a REQUIRED element that is missing, or a value
/// that will not parse, means the export's shape moved - so it raises
/// <see cref="ErrorCode.ProviderChanged"/>, which pages an operator and
/// degrades the provider, and never <see cref="ErrorCode.Internal"/>, which
/// would be quietly retried forever.
/// </summary>
public static class Camt053Parser
{
    public static IReadOnlyList<Camt053Statement> Parse(string xml, Camt053Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw ConnectorException.ProviderChanged($"{options.ProviderId}: camt.053 export is empty");
        }

        XDocument document;
        try
        {
            // DTD processing stays off: a statement file is data from a
            // third party, and an external entity in one is a file-read
            // primitive on the agent.
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ConnectorException(
                ErrorCode.ProviderChanged, $"{options.ProviderId}: camt.053 export is not well-formed XML", ex);
        }

        var root = document.Root
                   ?? throw ConnectorException.ProviderChanged($"{options.ProviderId}: camt.053 export has no root element");

        // Some banks wrap the message in an envelope, so the report is
        // located by name rather than by assuming it is the root's child.
        var report = string.Equals(root.Name.LocalName, "BkToCstmrStmt", StringComparison.Ordinal)
            ? root
            : root.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "BkToCstmrStmt", StringComparison.Ordinal));

        if (report is null)
        {
            throw ConnectorException.ProviderChanged(
                $"{options.ProviderId}: no BkToCstmrStmt element - this is not a camt.053 statement");
        }

        var statements = report.Children("Stmt").ToList();
        if (statements.Count == 0)
        {
            throw ConnectorException.ProviderChanged($"{options.ProviderId}: BkToCstmrStmt contains no Stmt");
        }

        return [.. statements.Select(stmt => ParseStatement(stmt, options))];
    }

    public static IReadOnlyList<Camt053Statement> ParseFile(string path, Camt053Options options) =>
        Parse(File.ReadAllText(path), options);

    private static Camt053Statement ParseStatement(XElement stmt, Camt053Options options)
    {
        var provider = options.ProviderId;
        var statementId = stmt.Child("Id").Text() ?? stmt.Child("ElctrncSeqNb").Text() ?? "stmt";

        var accountExternalId = AccountIdentification(stmt, provider, out var iban);

        var opening = FindBalance(stmt, provider, "OPBD", "PRCD");
        var closing = FindBalance(stmt, provider, "CLBD", "CLAV");

        var entries = stmt.Children("Ntry").ToList();

        // Currency, in descending order of authority: the account's own
        // declaration, then the first entry's amount, then the closing
        // balance. A statement with none of the three cannot be normalised
        // at all - money without a currency is not money.
        var currency = stmt.Path("Acct", "Ccy").Text()
                       ?? entries.FirstOrDefault().Child("Amt").Attr("Ccy")
                       ?? closing?.Amount.Currency
                       ?? throw ConnectorException.ProviderChanged(
                           $"{provider}: statement '{statementId}' states no currency on Acct/Ccy, Ntry/Amt@Ccy or Bal");

        var accountId = BankRecords.AccountId(options.SessionId, accountExternalId);

        var transactions = new List<Transaction>(entries.Count);
        var synthesized = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            transactions.Add(ParseEntry(entry, options, accountId, currency, statementId, synthesized));
        }

        VerifyAgainstBalances(provider, statementId, opening, closing, transactions);

        var account = BankRecords.NewAccount(
            options.SessionId,
            accountExternalId,
            options.AccountType,
            options.DisplayName ?? stmt.Path("Acct", "Nm").Text() ?? accountExternalId,
            currency,
            iban,
            balance: closing ?? opening);

        return new Camt053Statement
        {
            StatementId = statementId,
            Account = account,
            Transactions = transactions,
            Opening = opening,
            Closing = closing,
        };
    }

    /// <summary>
    /// An IBAN where the bank gives one, otherwise the proprietary
    /// identifier under <c>Othr/Id</c> - which is how credit-card and
    /// non-payment accounts appear, and those are exactly the accounts this
    /// service exists to reach.
    /// </summary>
    private static string AccountIdentification(XElement stmt, string provider, out string? iban)
    {
        var id = stmt.Path("Acct", "Id");
        iban = id.Child("IBAN").Text();

        var external = iban ?? id.Path("Othr", "Id").Text();
        return external ?? throw ConnectorException.ProviderChanged(
            $"{provider}: Stmt/Acct/Id carries neither IBAN nor Othr/Id");
    }

    private static Transaction ParseEntry(
        XElement entry,
        Camt053Options options,
        string accountId,
        string statementCurrency,
        string statementId,
        Dictionary<string, int> synthesized)
    {
        var provider = options.ProviderId;

        var amountElement = entry.Child("Amt")
                            ?? throw ConnectorException.ProviderChanged($"{provider}: Ntry without Amt in statement '{statementId}'");

        // The currency lives on the amount, not on the entry, and per-entry
        // currencies do occur on card statements. Fall back to the
        // statement's only when the attribute is genuinely absent.
        var currency = amountElement.Attr("Ccy") ?? statementCurrency;
        var magnitude = MoneyParser.ToMinor(amountElement.Text(), MoneyUnit.MajorString, $"{provider}: Ntry/Amt");

        var sign = DebitCreditSign(entry.Child("CdtDbtInd").Text(), provider, "Ntry/CdtDbtInd");
        var amount = new Money(magnitude * sign, currency);

        var booked = OptionalDate(entry.Child("BookgDt"), provider, "Ntry/BookgDt");
        var value = OptionalDate(entry.Child("ValDt"), provider, "Ntry/ValDt");

        // A booked statement without a booking date is unusable, but some
        // banks date the entry only by value. Falling back is tolerance;
        // having neither is a shape change.
        var bookedAt = booked ?? value
            ?? throw ConnectorException.ProviderChanged(
                $"{provider}: Ntry in statement '{statementId}' has neither BookgDt nor ValDt");

        var details = entry.Path("NtryDtls").Children("TxDtls").ToList();
        var description = Description(entry, details);
        var counterparty = Counterparty(details, sign);

        var externalId = ExternalId(entry, details, accountId, bookedAt, amount, description, synthesized);

        return BankRecords.NewTransaction(
            options.SessionId,
            accountId,
            externalId,
            bookedAt,
            amount,
            value,
            counterparty,
            description,
            Kind(entry),
            // CAMT.053 states no per-entry balance. Deriving one from the
            // opening figure and then "verifying" it would check our own
            // arithmetic against itself; the real redundancy the format
            // offers is OPBD + entries = CLBD, checked once per statement.
            resultingBalance: null);
    }

    /// <summary>
    /// <c>RmtInf/Ustrd</c> is the remitter's own message and is what a user
    /// recognises; <c>AddtlNtryInf</c> is the bank's rendering of the whole
    /// line. Prefer the former, fall back to the latter, and join repeated
    /// <c>Ustrd</c> elements because banks split long messages across them.
    /// </summary>
    private static string? Description(XElement entry, List<XElement> details)
    {
        var unstructured = details
            .SelectMany(d => d.Child("RmtInf").Children("Ustrd"))
            .Select(e => e.Text())
            .Where(t => t is not null)
            .ToList();

        if (unstructured.Count > 0) return string.Join(' ', unstructured);

        return entry.Child("AddtlNtryInf").Text()
               ?? details.Select(d => d.Child("AddtlTxInf").Text()).FirstOrDefault(t => t is not null);
    }

    /// <summary>
    /// On a debit the counterparty is the creditor; on a credit it is the
    /// debtor. Where the expected side is absent we take the other rather
    /// than emitting nothing - some banks always fill <c>Cdtr</c>.
    ///
    /// A batched entry (more than one <c>TxDtls</c>) has no single
    /// counterparty, and picking the first would be an invention. Those get
    /// none at all, and their <c>AddtlNtryInf</c> carries the batch label.
    /// </summary>
    private static Counterparty? Counterparty(List<XElement> details, int sign)
    {
        if (details.Count != 1) return null;

        var parties = details[0].Child("RltdPties");
        if (parties is null) return null;

        var (primary, primaryAccount, secondary, secondaryAccount) = sign < 0
            ? ("Cdtr", "CdtrAcct", "Dbtr", "DbtrAcct")
            : ("Dbtr", "DbtrAcct", "Cdtr", "CdtrAcct");

        var name = PartyName(parties, primary) ?? PartyName(parties, secondary);
        var iban = parties.Path(primaryAccount, "Id", "IBAN").Text()
                   ?? parties.Path(secondaryAccount, "Id", "IBAN").Text();

        return name is null && iban is null ? null : new Counterparty { Name = name, Iban = iban };
    }

    /// <summary>
    /// From camt.053.001.08 the party moved under an extra <c>Pty</c>
    /// wrapper to make room for an agent identification. Both shapes are in
    /// production simultaneously, so both are read.
    /// </summary>
    private static string? PartyName(XElement parties, string role) =>
        parties.Path(role, "Nm").Text() ?? parties.Path(role, "Pty", "Nm").Text();

    /// <summary>
    /// A stable per-entry identifier, in the order banks actually populate
    /// them. When a bank supplies none - which happens on older exports -
    /// one is synthesized from the entry's own content plus an occurrence
    /// counter, so two identical rows on one day stay distinguishable and a
    /// re-fetch still deduplicates.
    /// </summary>
    private static string ExternalId(
        XElement entry,
        List<XElement> details,
        string accountId,
        DateOnly bookedAt,
        Money amount,
        string? description,
        Dictionary<string, int> synthesized)
    {
        var stated = entry.Child("AcctSvcrRef").Text()
                     ?? entry.Child("NtryRef").Text()
                     ?? details.Select(d => d.Path("Refs", "AcctSvcrRef").Text()).FirstOrDefault(t => t is not null)
                     ?? details.Select(d => d.Path("Refs", "EndToEndId").Text()).FirstOrDefault(t => t is not null)
                     ?? details.Select(d => d.Path("Refs", "TxId").Text()).FirstOrDefault(t => t is not null);

        // "NOTPROVIDED" is the ISO placeholder for "no end-to-end reference",
        // and treating it as an id would collapse every such row onto one.
        if (stated is not null && !string.Equals(stated, "NOTPROVIDED", StringComparison.OrdinalIgnoreCase))
        {
            return stated;
        }

        var seed = string.Create(CultureInfo.InvariantCulture,
            $"{accountId}|{bookedAt:yyyy-MM-dd}|{amount.Value}|{amount.Currency}|{description}");

        synthesized.TryGetValue(seed, out var occurrence);
        synthesized[seed] = occurrence + 1;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{seed}|{occurrence}")));
        return "syn-" + Convert.ToHexStringLower(digest.AsSpan(0, 10));
    }

    /// <summary>
    /// A best-effort classification. It is a hint for the consumer's UI and
    /// never touches an amount, so an unrecognised code degrades to
    /// <see cref="TransactionKind.Other"/> instead of failing the run - the
    /// opposite of how an unparseable amount is treated.
    /// </summary>
    private static TransactionKind Kind(XElement entry)
    {
        var code = entry.Path("BkTxCd", "Domn");
        var domain = code.Child("Cd").Text();
        var family = code.Path("Fmly", "Cd").Text();
        var subFamily = code.Path("Fmly", "SubFmlyCd").Text();

        if (string.Equals(domain, "INTR", StringComparison.Ordinal)) return TransactionKind.Interest;

        if (subFamily is "CHRG" or "FEES") return TransactionKind.Fee;
        if (subFamily is "INTR") return TransactionKind.Interest;

        switch (family)
        {
            case "CCRD" or "MCRD": return TransactionKind.CardPayment;
            case "ICDT" or "RCDT": return TransactionKind.Transfer;
            case "IDDT" or "RDDT": return TransactionKind.DirectDebit;
        }

        // Dutch banks that predate the ISO code set still emit their own
        // two-letter mutation codes under Prtry.
        return entry.Path("BkTxCd", "Prtry", "Cd").Text() switch
        {
            "BA" or "GM" => TransactionKind.CardPayment,
            "GT" or "OV" or "VZ" or "TB" => TransactionKind.Transfer,
            "IC" or "DV" => TransactionKind.DirectDebit,
            "RE" or "RB" => TransactionKind.Interest,
            "KN" or "KS" => TransactionKind.Fee,
            _ => TransactionKind.Other,
        };
    }

    private static Balance? FindBalance(XElement stmt, string provider, params string[] codes)
    {
        foreach (var code in codes)
        {
            foreach (var balance in stmt.Children("Bal"))
            {
                var stated = balance.Path("Tp", "CdOrPrtry", "Cd").Text()
                             ?? balance.Path("Tp", "CdOrPrtry", "Prtry").Text();

                if (!string.Equals(stated, code, StringComparison.Ordinal)) continue;

                var amount = balance.Child("Amt")
                             ?? throw ConnectorException.ProviderChanged($"{provider}: Bal '{code}' without Amt");
                var currency = amount.Attr("Ccy")
                               ?? throw ConnectorException.ProviderChanged($"{provider}: Bal '{code}' Amt without Ccy");

                var minor = MoneyParser.ToMinor(amount.Text(), MoneyUnit.MajorString, $"{provider}: Bal[{code}]/Amt");
                var sign = DebitCreditSign(balance.Child("CdtDbtInd").Text(), provider, $"Bal[{code}]/CdtDbtInd");

                // A balance without a date is not a balance - a consumer
                // cannot tell whether it precedes or follows the entries.
                var asOf = OptionalDate(balance.Child("Dt"), provider, $"Bal[{code}]/Dt")
                           ?? throw ConnectorException.ProviderChanged($"{provider}: Bal '{code}' states no date");

                return BankRecords.BalanceOn(new Money(minor * sign, currency), asOf);
            }
        }

        return null;
    }

    /// <summary>
    /// The redundancy CAMT.053 actually offers: opening plus every entry
    /// must equal closing. It catches a dropped entry, an inverted
    /// debit/credit flag and a misread decimal separator - all of which
    /// otherwise produce a statement that looks entirely plausible.
    /// </summary>
    private static void VerifyAgainstBalances(
        string provider,
        string statementId,
        Balance? opening,
        Balance? closing,
        List<Transaction> transactions)
    {
        if (opening is null || closing is null) return;
        if (!string.Equals(opening.Amount.Currency, closing.Amount.Currency, StringComparison.OrdinalIgnoreCase)) return;

        var expected = opening.Amount.Value;
        foreach (var tx in transactions)
        {
            if (!string.Equals(tx.Amount.Currency, opening.Amount.Currency, StringComparison.OrdinalIgnoreCase))
            {
                // A foreign-currency entry on a statement makes the sum
                // meaningless; we decline to check rather than to claim.
                return;
            }

            expected += tx.Amount.Value;
        }

        if (Math.Abs(expected - closing.Amount.Value) > Reconciliation.ToleranceMinorUnits)
        {
            throw ConnectorException.ProviderChanged(
                $"{provider}: statement '{statementId}' does not reconcile - " +
                $"OPBD {opening.Amount.Value} plus {transactions.Count} entries gives {expected}, CLBD states {closing.Amount.Value}");
        }
    }

    private static int DebitCreditSign(string? indicator, string provider, string where) => indicator switch
    {
        "DBIT" => -1,
        "CRDT" => 1,
        null => throw ConnectorException.ProviderChanged($"{provider}: {where} is missing"),
        _ => throw ConnectorException.ProviderChanged($"{provider}: {where} has unknown value '{indicator}'"),
    };

    /// <summary>
    /// Absent is fine; present but unparseable is a shape change. The
    /// wrapper element carries either <c>Dt</c> (a date) or <c>DtTm</c> (a
    /// timestamp) depending on the bank and the message version.
    /// </summary>
    private static DateOnly? OptionalDate(XElement? wrapper, string provider, string where)
    {
        if (wrapper is null) return null;

        var date = wrapper.Child("Dt").Text();
        if (date is not null)
        {
            return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : throw ConnectorException.ProviderChanged($"{provider}: {where}/Dt is not an ISO date: '{date}'");
        }

        var timestamp = wrapper.Child("DtTm").Text();
        if (timestamp is not null)
        {
            return DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? DateOnly.FromDateTime(parsed.UtcDateTime)
                : throw ConnectorException.ProviderChanged($"{provider}: {where}/DtTm is not an ISO timestamp: '{timestamp}'");
        }

        // A wrapper with neither child is a bank emitting an empty element,
        // which is noise rather than a shape change.
        return null;
    }
}
