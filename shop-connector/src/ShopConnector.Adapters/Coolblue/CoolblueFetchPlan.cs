using Connector.Kit.Errors;
using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Coolblue;

/// <summary>
/// The seam between the half of this adapter that is finished and the half
/// that cannot be.
///
/// Coolblue's login is confirmed, reusable and complete. Its consumer order
/// endpoint is unknown to everyone outside Coolblue: there is no public prior
/// art of any kind, <c>/graphql</c> is a 404 and <c>api.coolblue.nl</c> does
/// not resolve. So the fetch is not guessed - it is refused, loudly, with a
/// message that turns finishing it into twenty minutes of devtools rather than
/// a research project.
///
/// That refusal is the deliberate product here. A half-adapter whose missing
/// half is precisely specified is worth far more than a guessed whole one:
/// this repo already carries a provider whose invented response paths and
/// invented money unit produce receipts that look plausible and are wrong.
/// </summary>
internal sealed record CoolblueFetchPlan
{
    public required string Url { get; init; }

    public required HttpMethod Method { get; init; }

    public required CoolblueOrderCredential Credential { get; init; }

    /// <summary>Declared by an operator from a capture. Never inferred from a value's shape.</summary>
    public required MoneyUnit TotalUnit { get; init; }

    /// <summary>Null when the caller asked for no items, in which case it is never needed.</summary>
    public MoneyUnit? ItemUnit { get; init; }

    /// <summary>
    /// Falls back to <see cref="ItemUnit"/>: a discount that sits inside a line
    /// is overwhelmingly written the way that line is.
    /// </summary>
    public MoneyUnit? DiscountUnit { get; init; }

    /// <summary>
    /// Exactly what a human has to capture, in the order they will meet it.
    ///
    /// Written to be actionable at 3am by somebody who has never read this
    /// adapter: every step names the option it fills in, and the last step
    /// names the one failure mode reconciliation cannot catch.
    /// </summary>
    public const string CaptureRecipe =
        """
        coolblue: the consumer order endpoint is NOT KNOWN and this adapter refuses to guess one.
        The login half is finished and confirmed - this session already holds a working access
        token, a refresh token and the browser session - so only the fetch is missing.

        To finish it, sign in as a real customer and capture the following, then set
        CoolblueOptions.OrdersEndpointConfirmed = true along with the values named:

          1. Open https://www.coolblue.nl/mijn-coolblue-account/bestellingen with the Network tab
             recording and the XHR/Fetch filter on.
          2. If NO JSON call answers with the orders, the page is server-rendered HTML. Stop:
             this adapter parses JSON only, and an HTML path has to be written. Capture the HTML
             of one order block for whoever writes it.
          3. If a JSON call does answer, record its full URL and method   -> OrdersUrl, OrdersMethod.
          4. Record every request header beyond cookies - an Authorization: Bearer? an
             x-csrf-token? an api-version?                                -> ExtraHeaders.
          5. Record WHICH credential it carries: an Authorization: Bearer header (the OIDC access
             token is usable, keep Credential = Bearer) or only the Coolblue-Session cookie
             (Credential = Cookie, and the sealed browser state is the credential).
          6. Record whether the response paginates, and how: a page or offset query parameter, a
             cursor in the body, or a Link header. Nothing here paginates yet.
          7. From the response body, record the dotted path to the order ARRAY -> OrderPaths, and
             the property names for the order id, the placed date, the stated total and the line
             items                              -> IdNames, DateNames, TotalNames, ItemsNames.
          8. Record whether the placed date carries a timezone offset. If it does not, it is read
             as Europe/Amsterdam, which is what the platform requires and what a bare date gets.
          9. Record whether amounts are euros ("12.34" or 12.34) or cents (1234)
                                                                          -> TotalUnit, ItemUnit.
             This is the one value that must not be guessed. Reconciliation compares a total
             against its own lines, so it catches an inconsistent PAIR and never a consistently
             wrong one: a total and its lines both misread as cents reconcile perfectly at a
             hundredth of the real money, silently, forever.
        """;

    /// <summary>
    /// The plan, or a refusal that says what to do instead.
    ///
    /// Called before any session material is touched and before a single
    /// packet leaves the machine, so a caller asking a provider that cannot
    /// answer costs Coolblue nothing at all.
    /// </summary>
    public static CoolblueFetchPlan Require(CoolblueOptions options, bool wantsItems)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The safety catch. provider_changed rather than unsupported_resource:
        // the resource is real and declared in the manifest, and it is an
        // adapter that needs finishing - which is exactly the state
        // provider_changed exists to page an operator about.
        if (!options.OrdersEndpointConfirmed)
        {
            throw ConnectorException.ProviderChanged(CaptureRecipe);
        }

        if (string.IsNullOrWhiteSpace(options.OrdersUrl))
        {
            throw Misconfigured("orders_endpoint_confirmed is set but orders_url is empty");
        }

        var method = options.OrdersMethod.Trim().ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            var other => throw Misconfigured($"orders_method '{other}' is neither GET nor POST"),
        };

        // Money is declared per field or the fetch does not happen. There is
        // no sniffing overload anywhere in this service and this is why: the
        // cost of being wrong is silent, permanent and invisible to review.
        var total = options.TotalUnit
                    ?? throw Misconfigured(
                        "orders_endpoint_confirmed is set but total_unit is not declared. Read the unit off " +
                        "the captured response - euros (\"12.34\") or cents (1234) - and state it. It is never " +
                        "inferred from the value's shape, and reconciliation cannot catch it being wrong.");

        var item = wantsItems
            ? options.ItemUnit
              ?? throw Misconfigured(
                  "items were requested but item_unit is not declared. A line amount gets its own " +
                  "declaration because an API that states a total in one unit and its lines in another is " +
                  "not hypothetical.")
            : options.ItemUnit;

        return new CoolblueFetchPlan
        {
            Url = options.OrdersUrl,
            Method = method,
            Credential = options.Credential,
            TotalUnit = total,
            ItemUnit = item,
            DiscountUnit = options.DiscountUnit ?? item,
        };
    }

    /// <summary>
    /// Ours, not theirs. An operator armed the fetch without finishing the
    /// configuration it needs, which is a fault in this deployment rather than
    /// anything Coolblue did - so it must not degrade the provider or tell a
    /// user to reconnect.
    /// </summary>
    private static ConnectorException Misconfigured(string detail) =>
        new(ErrorCode.Internal, $"{CoolblueAdapter.ProviderId}: {detail}");
}
