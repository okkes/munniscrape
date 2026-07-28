using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Picnic;

/// <summary>
/// Picnic - T1, and the only store in this catalogue that needs no browser at
/// all.
///
/// The whole provider is a mobile JSON API: <c>POST /user/login</c> with the
/// MD5 of the password, a token comes back in the <c>x-picnic-auth</c>
/// RESPONSE header, and every call after that carries it in the request
/// header. No OAuth, no PKCE, no redirect, no captcha and - per the research -
/// no edge protection. That is why this one runs inline in the control plane's
/// own process while every other store waits for a pooled agent.
///
/// Two things here are load-bearing and easy to get wrong. Money is CENTS: the
/// unit is declared, never sniffed, and it is confirmed both by the reference's
/// own type comments and arithmetically against a stated total. And a delivery
/// has two timestamps - <c>purchased_at</c> is the order's creation time, not
/// the moment a van arrived a day later.
/// </summary>
public sealed class PicnicAdapter : IProviderAdapter
{
    public const string ProviderId = "picnic";
    public const string ReceiptsResource = "receipts";

    private const string CountryKey = "country";

    private static readonly ProviderManifest Manifest = PicnicManifest.Build();

    private readonly PicnicOptions _options;
    private readonly TimeProvider _time;

    public PicnicAdapter(PicnicOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new PicnicOptions();
        _time = time ?? TimeProvider.System;
    }

    public ProviderManifest Describe() => Manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Everything that can fail without contacting anyone, checked first. A
        // login attempt is a thing a user may only get to spend once before an
        // account starts locking, so it is never spent discovering that a
        // required field was blank.
        var settings = PicnicSettings.From(ctx.Config, ctx.Material, _options);
        var username = RequiredInput(ctx, "username");
        var password = RequiredInput(ctx, "password");

        // Minted once, at connect, and carried for the life of the connection.
        // Re-using the stored one on a re-login is the point: a device that
        // changes identity every run looks like exactly what it is.
        var deviceId = ctx.Material?.DeviceId ?? PicnicCredential.DeviceId(_options.DeviceIdBytes);
        settings = settings with { DeviceId = deviceId };

        ctx.Progress(JobStep.OpeningProvider);

        var body = new JsonObject
        {
            ["key"] = username,
            // Hashed here, in this process, and never held anywhere else. The
            // digest is a password-equivalent to Picnic, so it is treated as
            // the secret it is.
            ["secret"] = PicnicCredential.Secret(password),
            ["client_id"] = _options.ClientId,
        }.ToJsonString();

        ctx.Progress(JobStep.Authenticating);

        // Latched before the bytes leave the machine, not after. From here a
        // lost lease fails the job instead of requeuing it: retrying a login
        // that may already have counted is how accounts get locked, and Picnic
        // may already have sent a text message.
        ctx.CredentialSubmitted();

        var (token, account) = await SignInAsync(ctx, settings, body, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            Material = new SessionMaterial
            {
                AccessToken = token,
                DeviceId = deviceId,
                // Carried here as well as in the bundle's config block: the
                // country is the API HOST, so a fetch that arrived without
                // config would have nowhere to send its request.
                Extra = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CountryKey] = settings.Country,
                },
            },
            Account = account,
            // Deliberately unset. The manifest's TTL governs, and the token
            // renews itself on every response - claiming a hard expiry here
            // would make the consumer prompt for a reconnect that is not
            // needed.
        };
    }

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, ReceiptsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"picnic: no resource '{request.ResourceId}'");
        }

        var settings = PicnicSettings.From(ctx.Config, ctx.Material, _options);
        var session = new PicnicSession(ctx.Material, ProviderId);
        var zone = RetailZones.For(settings.Country);

        ctx.Progress(JobStep.Downloading);

        // One call for the whole history: /deliveries/summary takes a status
        // filter and nothing else, so there is no cursor to walk and no page
        // that can repeat under us.
        var summaries = await SummaryAsync(ctx, session, settings, zone, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Parsing);

        var cap = Manifest.Resource(ReceiptsResource)!.MaxRecordsPerFetch;
        var selected = new List<PicnicOrderSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var summary in summaries.OrderByDescending(s => s.PurchasedAt))
        {
            if (!ReceiptFactory.InWindow(summary.PurchasedAt, request)) continue;
            if (!seen.Add(summary.OrderId)) continue;

            selected.Add(summary);
        }

        // Newest first, so a partial pass hands back the rows a caller wants
        // most and comes back for the rest.
        var complete = selected.Count <= cap;
        if (!complete) selected = [.. selected.Take(cap)];

        var details = await DetailsAsync(ctx, session, settings, request, selected, ct).ConfigureAwait(false);

        var receipts = new List<Receipt>(selected.Count);

        foreach (var summary in selected)
        {
            IReadOnlyList<ReceiptItem> items = [];
            var payment = ReceiptFactory.Payment();

            if (details.TryGetValue(summary.OrderId, out var parsed))
            {
                items = parsed.Items;
                payment = parsed.Payment;
            }

            // The summary's stated total against the detail's own lines is the
            // redundancy Picnic gives us, and ReceiptFactory spends it: every
            // receipt leaves here with a reconciliation verdict on it, and one
            // that does not add up is flagged rather than dropped.
            receipts.Add(ReceiptFactory.Build(
                ctx.SessionId,
                summary.OrderId,
                PicnicDeliveryParser.Merchant(),
                summary.PurchasedAt,
                summary.Total,
                payment,
                items));
        }

        ctx.Progress(JobStep.Normalizing);

        return new FetchResult
        {
            Receipts = receipts,
            // Non-null only when Picnic re-issued the token while we were
            // working, which it may do on any response.
            RefreshedMaterial = session.RefreshedMaterial(ctx.Material),
            Complete = complete,
            Via = request.WantsItems ? "deliveries-summary+detail" : "deliveries-summary",
        };
    }

    /// <summary>
    /// Best effort, and never fatal: a user disconnecting must always succeed
    /// locally whatever Picnic thinks about it.
    /// </summary>
    public async Task LogoutAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        try
        {
            var settings = PicnicSettings.From(ctx.Config, ctx.Material, _options);
            var session = new PicnicSession(ctx.Material, ProviderId);

            ctx.Progress(JobStep.LoggingOut);

            using var request = Request(
                HttpMethod.Post, Url(settings, _options.LogoutPath), session.Token, settings,
                json: null, picnicHeaders: false);

            using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);
        }
        catch (ConnectorException)
        {
            // Already logged out, already expired, or unreachable. None of
            // those is a reason to fail a disconnect.
        }
    }

    // ---- login -------------------------------------------------------------

    /// <summary>
    /// Posts the credentials, meets the second factor if the account has one,
    /// and returns the token plus who it belongs to.
    /// </summary>
    private async Task<(string Token, ProviderAccount Account)> SignInAsync(
        IJobContext ctx, PicnicSettings settings, string body, CancellationToken ct)
    {
        string? userId;
        bool secondFactor;
        string token;

        using (var request = Request(
                   HttpMethod.Post, Url(settings, _options.LoginPath), token: null, settings, body,
                   picnicHeaders: false))
        using (var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await LoginFailureAsync(response, ct).ConfigureAwait(false);
            }

            // Confirmed: the token is a RESPONSE HEADER, not a body field. A
            // reader that only looks at the body finds a perfectly valid login
            // response with no credential in it.
            token = PicnicSession.TokenOf(response)
                    ?? throw ConnectorException.ProviderChanged(
                        $"picnic: the login succeeded but carried no '{PicnicSession.AuthHeader}' response header");

            using var document = await ProviderHttp
                .ReadJsonAsync(response, ProviderId, "login", ct).ConfigureAwait(false);

            userId = JsonAccess.StrOf(document.RootElement, "user_id", "userId");
            secondFactor = Truthy(document.RootElement, "second_factor_authentication_required");
        }

        if (secondFactor)
        {
            token = await SecondFactorAsync(ctx, settings, token, ct).ConfigureAwait(false);
        }

        return (token, new ProviderAccount
        {
            // A brand name is not prose and needs no translation. The user id
            // is what lets somebody tell two Picnic connections apart.
            DisplayName = Manifest.Name,
            ExternalId = userId,
        });
    }

    /// <summary>
    /// Picnic's second factor: ask it to send a code, relay the question to the
    /// human who owns the account, hand the answer back. Never solved here -
    /// the code is on their phone and only they can read it.
    ///
    /// Both calls carry the <c>x-picnic-agent</c>/<c>x-picnic-did</c> pair,
    /// which is the confirmed requirement for exactly this pair of endpoints,
    /// and the verify answers <c>204</c> with an empty body and the new token
    /// in the header - so nothing here may try to parse a response.
    /// </summary>
    private async Task<string> SecondFactorAsync(
        IJobContext ctx, PicnicSettings settings, string token, CancellationToken ct)
    {
        var generate = new JsonObject { ["channel"] = _options.TwoFactorChannel }.ToJsonString();

        using (var request = Request(
                   HttpMethod.Post, Url(settings, _options.TwoFactorGeneratePath), token, settings, generate,
                   picnicHeaders: true))
        using (var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false))
        {
            await ProviderHttp.EnsureSuccessAsync(
                response, ProviderId, "2fa generate", ProviderHttp.RetailBlockStatuses, ct).ConfigureAwait(false);

            token = PicnicSession.TokenOf(response) ?? token;
        }

        ctx.Progress(JobStep.AwaitingHuman);

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.MfaCode,
            PromptKey = MessageKeys.VerificationCode,
            // Confirmed: SMS is the only channel the generate call is asked
            // for, so the human is told to look at their phone and that is
            // where the code actually is.
            Delivery = "sms",
            Length = _options.OtpLength,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.OtpChallengeSeconds),
        }, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Authenticating);

        var verify = new JsonObject { ["otp"] = answer.Value }.ToJsonString();

        using (var request = Request(
                   HttpMethod.Post, Url(settings, _options.TwoFactorVerifyPath), token, settings, verify,
                   picnicHeaders: true))
        using (var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                // A rejected code is mfa_failed and nothing else. It is not
                // invalid_credentials - the password was already accepted, and
                // saying otherwise would send the user to reset one that works.
                if (ProviderHttp.RetailBlockStatuses.Contains((int)response.StatusCode))
                {
                    throw ConnectorException.Blocked(
                        $"picnic: the 2fa verify was refused with {(int)response.StatusCode}");
                }

                throw new ConnectorException(ErrorCode.MfaFailed,
                    $"picnic: the 2fa verify rejected the code ({(int)response.StatusCode})");
            }

            // Confirmed: 204, no body, and the new token in the header.
            return PicnicSession.TokenOf(response)
                   ?? throw ConnectorException.ProviderChanged(
                       $"picnic: the 2fa verify succeeded but carried no '{PicnicSession.AuthHeader}' response header");
        }
    }

    /// <summary>
    /// How a failed login is read - and the one decision on this provider that
    /// is worth more than the rest of the file.
    ///
    /// A refusal is reported as a refusal. Only a stated error code from the
    /// configured list may become <c>invalid_credentials</c>, because that code
    /// is never retried by anything: a false one is permanent for the connect
    /// attempt AND sends the user off to reset a password that was fine, while
    /// the real cause goes undiagnosed. Everything else follows the shared
    /// reading, in which 403 is bot protection and never a password.
    /// </summary>
    private async Task<ConnectorException> LoginFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (ProviderHttp.RetailBlockStatuses.Contains((int)response.StatusCode))
        {
            return ConnectorException.Blocked(
                $"picnic: the login was refused with {(int)response.StatusCode}; " +
                "the research records no bot protection on this host, so a refusal here is a finding");
        }

        var code = await ErrorCodeAsync(response, ct).ConfigureAwait(false);

        if (code is not null &&
            _options.InvalidCredentialCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            return ConnectorException.InvalidCredentials($"picnic: the login endpoint stated '{code}'");
        }

        // No recognisable code. Whatever this is, it is not evidence about the
        // password, so it is never reported as one.
        return ProviderHttp.Failure(
            response.StatusCode, ProviderId, "login", ProviderHttp.RetailBlockStatuses);
    }

    /// <summary>
    /// The <c>error.code</c> from a Picnic error body, best effort. Bounded and
    /// never allowed to mask the failure it is describing.
    /// </summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 8_000) return null;

            using var document = JsonDocument.Parse(text);
            return JsonAccess.TryPath(document.RootElement, "error", out var error)
                ? JsonAccess.StrOf(error, "code")
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or ObjectDisposedException)
        {
            // A block page, an empty body, a truncated stream. The status is
            // still the finding, and a body we could not read must never
            // upgrade a refusal into an accusation about a password.
            return null;
        }
    }

    // ---- fetch --------------------------------------------------------------

    private async Task<IReadOnlyList<PicnicOrderSummary>> SummaryAsync(
        IJobContext ctx, PicnicSession session, PicnicSettings settings, TimeZoneInfo zone, CancellationToken ct)
    {
        var filter = new JsonArray();
        foreach (var status in _options.StatusFilter) filter.Add(JsonValue.Create(status));

        using var request = Request(
            HttpMethod.Post, Url(settings, _options.DeliverySummaryPath), session.Token, settings,
            filter.ToJsonString(), picnicHeaders: false);

        using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);
        session.Observe(response);

        await ProviderHttp.EnsureSuccessAsync(
            response, ProviderId, "delivery summary", ProviderHttp.RetailBlockStatuses, ct).ConfigureAwait(false);

        using var document = await ProviderHttp
            .ReadJsonAsync(response, ProviderId, "delivery summary", ct).ConfigureAwait(false);

        return PicnicDeliveryParser.ParseSummary(document.RootElement, _options, zone);
    }

    /// <summary>
    /// The N+1 the API forces on us: the summary carries totals but no items,
    /// so line items cost one call per DELIVERY - not per order, which is why
    /// the ids are grouped first. Only for deliveries with an order inside the
    /// caller's window, and only when items were actually asked for.
    /// </summary>
    private async Task<Dictionary<string, ParsedOrder>> DetailsAsync(
        IJobContext ctx, PicnicSession session, PicnicSettings settings, ResourceRequest request,
        IReadOnlyList<PicnicOrderSummary> selected, CancellationToken ct)
    {
        var details = new Dictionary<string, ParsedOrder>(StringComparer.Ordinal);
        if (!request.WantsItems) return details;

        foreach (var deliveryId in selected.Select(s => s.DeliveryId).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            var url = Url(settings, _options.DeliveryDetailPathTemplate
                .Replace("{id}", Uri.EscapeDataString(deliveryId), StringComparison.Ordinal));

            using var http = Request(HttpMethod.Get, url, session.Token, settings, json: null, picnicHeaders: false);
            using var response = await ProviderHttp.SendAsync(ctx.Http, http, ProviderId, ct).ConfigureAwait(false);
            session.Observe(response);

            await ProviderHttp.EnsureSuccessAsync(
                response, ProviderId, "delivery detail", ProviderHttp.RetailBlockStatuses, ct).ConfigureAwait(false);

            using var document = await ProviderHttp
                .ReadJsonAsync(response, ProviderId, "delivery detail", ct).ConfigureAwait(false);

            var orders = PicnicDeliveryParser.OrdersById(document.RootElement);

            foreach (var summary in selected)
            {
                if (!string.Equals(summary.DeliveryId, deliveryId, StringComparison.Ordinal)) continue;

                // An order the summary listed but the detail does not carry is
                // left without items rather than failing the pass: it is a
                // normal outcome for an order that was merged or cancelled
                // between the two calls, and the receipt is still real. The
                // detail having no orders array AT ALL is the shape change, and
                // OrdersById raises that.
                if (!orders.TryGetValue(summary.OrderId, out var order)) continue;

                details[summary.OrderId] = new ParsedOrder(
                    PicnicDeliveryParser.ParseItems(order, _options),
                    PicnicDeliveryParser.ParsePayment(order));
            }
        }

        return details;
    }

    // ---- transport ----------------------------------------------------------

    /// <summary>
    /// Confirmed base URL: the country is a HOST label and the API version is a
    /// PATH segment, so both are substituted here and neither is a header.
    /// </summary>
    internal string Url(PicnicSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var baseUrl = _options.BaseUrlTemplate
            .Replace("{country}", settings.Country.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("{version}", _options.ApiVersion, StringComparison.Ordinal);

        return baseUrl.TrimEnd('/') + path;
    }

    /// <summary>
    /// The header block Picnic's own client sends, and nothing beyond it.
    ///
    /// Sent because this is a mobile API that answers differently without them,
    /// not to disguise anything - the honest client identity the platform's
    /// posture allows. There is deliberately no <c>Accept</c> header: the
    /// reference sends none, nothing suggests the API wants one, and inventing
    /// headers a provider has never been observed to receive is how a client
    /// starts looking like something other than what it is.
    /// </summary>
    private HttpRequestMessage Request(
        HttpMethod method, string url, string? token, PicnicSettings settings, string? json, bool picnicHeaders)
    {
        var request = new HttpRequestMessage(method, url);

        if (json is not null)
        {
            var content = new StringContent(json, Encoding.UTF8);
            // Set explicitly: StringContent would render "charset=utf-8", and
            // the confirmed value is "application/json; charset=UTF-8".
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(_options.ContentType);
            request.Content = content;
        }

        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", settings.Language);

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(PicnicSession.AuthHeader, token);
        }

        // Confirmed as required for the 2FA calls specifically. The device id
        // is omitted rather than invented when a resumed session has none:
        // a device identity that changes per run is worse than one that is
        // absent.
        if ((picnicHeaders || _options.AlwaysSendPicnicHeaders) && settings.DeviceId is { Length: > 0 } deviceId)
        {
            request.Headers.TryAddWithoutValidation("x-picnic-agent", _options.PicnicAgentHeader);
            request.Headers.TryAddWithoutValidation("x-picnic-did", deviceId);
        }

        return request;
    }

    private static bool Truthy(JsonElement parent, params string[] names) =>
        JsonAccess.TryProp(parent, out var value, names) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String &&
          string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static string RequiredInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"picnic: '{key}' is required");

    private sealed record ParsedOrder(IReadOnlyList<ReceiptItem> Items, ReceiptPayment Payment);
}
