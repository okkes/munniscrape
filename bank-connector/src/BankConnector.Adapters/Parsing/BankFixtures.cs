using System.Reflection;

namespace BankConnector.Adapters.Parsing;

/// <summary>
/// The recorded bank exports this assembly ships, for offline tests.
///
/// Every adapter is required to ship its provider's export shape as a
/// fixture, because that is what catches a bank changing its export without
/// anyone holding a live account. These two are the reference dialects: a
/// bank that populates almost every optional element, and one that
/// populates almost none.
/// </summary>
public static class BankFixtures
{
    /// <summary>camt.053.001.02, ISO transaction codes, RmtInf, full RltdPties.</summary>
    public const string Camt053IngSavings = "camt053-ing-savings.xml";

    /// <summary>camt.053.001.08, proprietary codes, AddtlNtryInf, a batched entry.</summary>
    public const string Camt053AsnCurrent = "camt053-asn-current.xml";

    /// <summary>Semicolon-delimited, Dutch headers, comma decimals, separate Af/Bij column.</summary>
    public const string StatementCsvDutch = "bank-statement-nl.csv";

    /// <summary>
    /// Comma-delimited, English headers, dot decimals, signed amounts, and a
    /// quoted description containing a comma. Same statement as the Dutch
    /// one, which is what makes the two comparable in a test.
    /// </summary>
    public const string StatementCsvEnglish = "bank-statement-en.csv";

    public static IReadOnlyList<string> All =>
        [Camt053IngSavings, Camt053AsnCurrent, StatementCsvDutch, StatementCsvEnglish];

    public static string Read(string fixtureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureName);

        var assembly = typeof(BankFixtures).Assembly;
        var resource = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith('.' + fixtureName, StringComparison.Ordinal));

        if (resource is null)
        {
            throw new FileNotFoundException(
                $"fixture '{fixtureName}' is not embedded in {assembly.GetName().Name}", fixtureName);
        }

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
