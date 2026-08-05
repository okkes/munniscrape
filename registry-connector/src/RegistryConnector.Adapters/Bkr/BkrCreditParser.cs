using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;

namespace RegistryConnector.Adapters.Bkr;

/// <summary>
/// Every credit BKR has registered against you, read out of one page.
///
/// CONFIRMED live on 2026-08-04. The portal is server-rendered ASP.NET MVC and
/// its only JSON endpoint (<c>POST /home/inquirecki</c>) answers the literal
/// <c>True</c> - it is a trigger, not data. So this is HTML parsing, and the
/// question was which HTML.
///
/// The visible list gives four columns per credit and a link to a detail page
/// each. Walking those would be six requests for five credits, keyed on
/// positional classes like <c>col-sm-6 col-md-2 font14 pt-3</c>, and it would
/// still not carry the dates.
///
/// The page also contains a hidden print block - <c>#pdf-export</c>, pushed
/// off-screen by CSS but fully in the DOM - holding one page per credit plus
/// one for the consumer, as label/value pairs:
/// <code>
///   &lt;div class="info-item"&gt;
///     &lt;div class="label"&gt;Overeenkomstnummer&lt;/div&gt;
///     &lt;div class="value"&gt;R1234567&lt;/div&gt;
///   &lt;/div&gt;
/// </code>
/// That is one request for everything, keyed on the label text a human reads
/// rather than on a grid class that changes when somebody adjusts a column.
/// So it is what this reads.
///
/// Two hazards the capture made visible, both of which would produce wrong
/// data quietly:
///
///   * The visible rows appear TWICE - the "Alles" and "Lopend" tabs are both
///     server-rendered into the page - so anything counting li.contract sees
///     ten credits where there are five.
///   * The captured account has no ended credits, so nobody has ever seen an
///     ended row. Status is therefore read from the section the print block
///     states, never from a guessed CSS class.
/// </summary>
internal static partial class BkrCreditParser
{
    private const string ProviderId = BkrAdapter.ProviderId;

    [GeneratedRegex(@"<div class=""pdf-page"">(?<page>.*?)(?=<div class=""pdf-page"">|\z)", RegexOptions.Singleline)]
    private static partial Regex Page { get; }

    [GeneratedRegex(
        @"<div class=""label"">(?<label>.*?)</div>\s*<div class=""value"">(?<value>.*?)</div>",
        RegexOptions.Singleline)]
    private static partial Regex Pair { get; }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }

    /// <summary>
    /// The credits, in the order the register lists them.
    /// </summary>
    public static IReadOnlyList<CreditRegistration> Parse(string? html, BkrOptions options, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(html))
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: the portal returned an empty page");
        }

        if (!html.Contains(options.PrintBlockMarker, StringComparison.Ordinal))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the page carries no '{options.PrintBlockMarker}' block, which is where every " +
                "credit's detail is stated. Either the portal was rebuilt or this is not the credit overview.");
        }

        var credits = new List<CreditRegistration>();

        foreach (Match page in Page.Matches(html))
        {
            var fields = Fields(page.Groups["page"].Value);

            // The consumer's own page carries a birth date and no credit
            // reference. Skipped here; ProfileOf reads it.
            if (!fields.TryGetValue(options.ReferenceLabel, out var reference)) continue;
            if (string.IsNullOrWhiteSpace(reference)) continue;

            credits.Add(Credit(fields, reference, options, sessionId));
        }

        if (credits.Count == 0 && !html.Contains(options.EmptyRegisterMarker, StringComparison.OrdinalIgnoreCase))
        {
            // An account with nothing registered is a real answer and a
            // valuable one - "BKR has nothing on you" is what most people hope
            // to read. But it is only allowed when the page says so itself,
            // never as the result of a parser that stopped matching.
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the print block yielded no credits and the page does not say the register is empty");
        }

        return credits;
    }

    /// <summary>
    /// The consumer, as the register knows them. The first print page states
    /// it and carries no credit reference.
    /// </summary>
    public static string? ProfileOf(string? html, BkrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(html)) return null;

        foreach (Match page in Page.Matches(html))
        {
            var fields = Fields(page.Groups["page"].Value);
            if (fields.ContainsKey(options.ReferenceLabel)) continue;

            var surname = fields.GetValueOrDefault(options.SurnameLabel);
            var initials = fields.GetValueOrDefault(options.InitialsLabel);

            if (string.IsNullOrWhiteSpace(surname)) continue;

            return string.IsNullOrWhiteSpace(initials) ? surname : $"{initials} {surname}";
        }

        return null;
    }

    private static CreditRegistration Credit(
        IReadOnlyDictionary<string, string> fields, string reference, BkrOptions options, string sessionId)
    {
        var label = fields.GetValueOrDefault(options.KindLabel);

        var ended = Date(fields.GetValueOrDefault(options.ActualEndLabel), options);

        return new CreditRegistration
        {
            Id = Connector.Kit.Ids.ForRecord(Connector.Kit.Ids.CreditRegistration, sessionId, reference),
            ExternalId = reference,

            Creditor = fields.GetValueOrDefault(options.CreditorLabel)
                       ?? throw ConnectorException.ProviderChanged(
                           $"{ProviderId}: credit '{reference}' states no '{options.CreditorLabel}'"),

            Kind = Kind(label, options),
            KindLabel = label,

            Amount = Money(fields.GetValueOrDefault(options.AmountLabel), options, reference),

            // The register's own words, not a date comparison. A credit whose
            // expected end has passed is not thereby ended - that is exactly
            // what "Werkelijke einddatum" exists to state, and inferring it
            // would report somebody's live debt as settled.
            Status = ended is not null ? CreditStatus.Ended : CreditStatus.Running,

            StartedOn = Date(fields.GetValueOrDefault(options.RegisteredLabel), options),
            EndsOn = ended ?? Date(fields.GetValueOrDefault(options.ExpectedEndLabel), options),

            // Never interpreted, never summarised, and null rather than "none"
            // when the register reports nothing: what an arrears code means for
            // somebody's mortgage application is not a connector's judgement.
            ArrearsCode = Arrears(fields, options),
        };
    }

    /// <summary>
    /// Dutch credit types, mapped. The register's own words ride along on
    /// <see cref="CreditRegistration.KindLabel"/>, so a type this does not
    /// know still reaches the user intact instead of arriving as "other" and
    /// nothing else.
    /// </summary>
    private static CreditKind Kind(string? label, BkrOptions options)
    {
        if (string.IsNullOrWhiteSpace(label)) return CreditKind.Other;

        foreach (var (needle, kind) in options.CreditKinds)
        {
            if (label.Contains(needle, StringComparison.OrdinalIgnoreCase)) return kind;
        }

        return CreditKind.Other;
    }

    /// <summary>
    /// "&#8364; 2.000" is two thousand euros.
    ///
    /// The dot is a THOUSANDS separator here, which is the single most
    /// dangerous character on this page: read as a decimal point it turns a
    /// two-thousand-euro debt into two euros, and the result looks perfectly
    /// reasonable on screen.
    /// </summary>
    private static Money Money(string? raw, BkrOptions options, string reference)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: credit '{reference}' states no '{options.AmountLabel}'");
        }

        var cleaned = raw
            .Replace("€", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (!decimal.TryParse(cleaned, NumberStyles.Number, Dutch, out var amount))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: credit '{reference}' states an unreadable amount '{raw}'");
        }

        return new Money(
            checked((long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero)),
            options.Currency);
    }

    /// <summary>dd-MM-yyyy, as the portal prints it. Null when the field is blank.</summary>
    private static DateOnly? Date(string? raw, BkrOptions options)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return DateOnly.TryParseExact(raw.Trim(), [.. options.DateFormats], Dutch, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// The "Bijzonderheden" section. The register prints a sentence meaning
    /// "nothing to report" when there is nothing, and that is not an arrears
    /// code - it is the absence of one.
    /// </summary>
    private static string? Arrears(IReadOnlyDictionary<string, string> fields, BkrOptions options)
    {
        foreach (var label in options.ArrearsLabels)
        {
            if (!fields.TryGetValue(label, out var value)) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;

            foreach (var nothing in options.NoArrearsMarkers)
            {
                if (value.Contains(nothing, StringComparison.OrdinalIgnoreCase)) return null;
            }

            return value;
        }

        return null;
    }

    /// <summary>
    /// One print page's label/value pairs. Keyed by the label text a human
    /// reads, because the labels are the stable part - the grid classes
    /// around them are Bootstrap column widths and change with the layout.
    /// </summary>
    private static Dictionary<string, string> Fields(string page)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match pair in Pair.Matches(page))
        {
            var label = Text(pair.Groups["label"].Value);
            if (label.Length == 0) continue;

            // First wins. A print page states each label once; a repeat would
            // be a layout artefact rather than a second value.
            found.TryAdd(label, Text(pair.Groups["value"].Value));
        }

        return found;
    }

    private static string Text(string markup) =>
        Whitespace.Replace(WebUtility.HtmlDecode(Tags.Replace(markup, " ")), " ").Trim();

    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
}
