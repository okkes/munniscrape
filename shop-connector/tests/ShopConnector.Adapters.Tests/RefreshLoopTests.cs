using System.Net;
using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Security;
using ShopConnector.Adapters.LidlPlus;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The token loop, end to end and from the client's side of it.
///
/// Sign in once; the client stores a bundle carrying access + refresh; every
/// later fetch sends it back; the connector refreshes silently when the access
/// token has died and hands back rotated material to persist. The human is
/// never asked to sign in again until the refresh token itself dies.
///
/// Every other suite tests a piece of that. This one tests the LOOP: the
/// material that comes OUT of one fetch is the material that goes INTO the
/// next, over and over, against an upstream that behaves like a real OAuth
/// server - it retires what it rotates, so presenting a superseded refresh
/// token is refused rather than quietly tolerated. That is the only way the
/// two failures this design dies of can show up at all: material that is
/// dropped between passes, and material that is carried forward when it
/// should not be.
///
/// Lidl is the vehicle because it is the plainest <see cref="Support"/>
/// <c>BearerSession</c> consumer in the fleet - one authorization-code login,
/// then nothing but bearer HTTP - but nothing here is Lidl-specific: the
/// refresh, the retry and the carry-forward all live in <c>BearerSession</c>
/// and every T1 adapter shares them.
/// </summary>
public sealed class RefreshLoopTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Wide enough that the clock moving a month never changes which receipts are in scope.</summary>
    private static readonly DateOnly Since = new(2026, 1, 1);

    private static readonly IReadOnlyDictionary<string, string> DutchConfig =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["country"] = "NL", ["language"] = "nl" };

    // ── an expired access token and a live refresh token ─────────────────

    [Fact]
    public async Task An_expired_access_token_refreshes_silently_and_hands_back_material_to_persist()
    {
        var clock = new MovingClock(Day0);
        var upstream = new TokenServer(clock);

        // What the client stored at connect time, an hour and a half ago.
        var stored = Material(upstream.AccessToken, upstream.RefreshToken, clock.GetUtcNow().AddMinutes(-30));

        var handler = new StubHttpHandler(upstream.Route);
        using var ctx = Context(handler, stored);

        var result = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);

        // The data came back. No challenge was raised, no browser was leased -
        // FakeJobContext refuses both - so nothing asked the human anything.
        Assert.NotEmpty(result.Receipts);
        Assert.Empty(ctx.Asked);
        Assert.False(ctx.Browser.Started);

        Assert.Equal(1, upstream.TokenCalls);

        var rotated = Assert.IsType<SessionMaterial>(result.RefreshedMaterial);
        Assert.Equal(upstream.AccessToken, rotated.AccessToken);
        Assert.Equal(upstream.RefreshToken, rotated.RefreshToken);
        Assert.NotEqual(stored.AccessToken, rotated.AccessToken);

        // A minute short of the hour the server promised: a token that expires
        // in flight costs a wasted round trip and an avoidable 401.
        Assert.Equal(clock.GetUtcNow().AddSeconds(3600 - 60), rotated.AccessTokenExpiresAt);

        // Everything the bundle carried that is not a token survives the
        // rotation. Losing the country here would send the next fetch to
        // another Lidl deployment.
        Assert.Equal("NL", rotated.Extra["country"]);
        Assert.Equal("fixture-device", rotated.DeviceId);
    }

    // ── the owner's sentence, as one test ────────────────────────────────

    [Fact]
    public async Task The_rotated_material_serves_the_next_fetch_and_the_one_it_replaced_is_dead()
    {
        var clock = new MovingClock(Day0);
        var upstream = new TokenServer(clock);

        var first = Material(upstream.AccessToken, upstream.RefreshToken, clock.GetUtcNow().AddMinutes(-1));
        var superseded = first;

        SessionMaterial second;
        using (var ctx = Context(new StubHttpHandler(upstream.Route), first))
        {
            var result = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);
            second = Assert.IsType<SessionMaterial>(result.RefreshedMaterial);
        }

        // The upstream rotated, exactly as `rotates_on_use` advertises.
        Assert.NotEqual(superseded.RefreshToken, second.RefreshToken);

        // THE SENTENCE: next time the client wants receipts it provides what
        // it was handed, and that is enough. No sign-in, no challenge.
        clock.Advance(TimeSpan.FromDays(1));
        using (var ctx = Context(new StubHttpHandler(upstream.Route), second))
        {
            var result = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);

            Assert.NotEmpty(result.Receipts);
            Assert.Empty(ctx.Asked);
            Assert.NotNull(result.RefreshedMaterial);
        }

        // And the half that would otherwise go unnoticed: the bundle it
        // replaced is genuinely spent. A real authorization server retires a
        // rotated refresh token, so a client that kept persisting the OLD one
        // would look fine for an hour and then be locked out - which is the
        // failure this whole loop exists to prevent.
        clock.Advance(TimeSpan.FromDays(1));
        using (var ctx = Context(new StubHttpHandler(upstream.Route), superseded))
        {
            var dead = await Assert.ThrowsAsync<ConnectorException>(
                () => Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None));

            Assert.Equal(ErrorCode.SessionExpired, dead.Code);
        }

        Assert.Contains(superseded.RefreshToken!, upstream.Retired);
    }

    // ── nothing rotated ──────────────────────────────────────────────────

    [Fact]
    public async Task A_fetch_that_rotated_nothing_reports_no_material_and_leaves_the_stored_bundle_current()
    {
        var clock = new MovingClock(Day0);
        var upstream = new TokenServer(clock);

        // The access token is still good for another half hour, so there is
        // nothing to refresh and nothing for the caller to persist.
        var stored = Material(upstream.AccessToken, upstream.RefreshToken, clock.GetUtcNow().AddMinutes(30));

        using (var ctx = Context(new StubHttpHandler(upstream.Route), stored))
        {
            var result = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);

            Assert.NotEmpty(result.Receipts);

            // Null is the whole signal: the control plane seals a new bundle
            // only when this is non-null, so a session block that appeared
            // here would be the connector handing back something it had merely
            // been holding.
            Assert.Null(result.RefreshedMaterial);
            Assert.Equal(0, upstream.TokenCalls);
        }

        // The caller's bundle is unchanged and still works.
        clock.Advance(TimeSpan.FromMinutes(5));
        using (var ctx = Context(new StubHttpHandler(upstream.Route), stored))
        {
            var again = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);

            Assert.NotEmpty(again.Receipts);
            Assert.Null(again.RefreshedMaterial);
        }
    }

    // ── the refresh token itself is dead ─────────────────────────────────

    [Fact]
    public async Task A_dead_refresh_token_is_session_expired_after_exactly_one_attempt()
    {
        var clock = new MovingClock(Day0);
        var upstream = new TokenServer(clock);

        // The bundle was good when it was sealed. Then the user hit "sign out
        // everywhere" upstream, which is the one thing no amount of refreshing
        // can recover from.
        var stored = Material(upstream.AccessToken, upstream.RefreshToken, clock.GetUtcNow().AddMinutes(-1));
        upstream.Revoke();

        var handler = new StubHttpHandler(upstream.Route);
        using var ctx = Context(handler, stored);

        var failure = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None));

        // The one code the consumer can act on: sign in again.
        Assert.Equal(ErrorCode.SessionExpired, failure.Code);
        Assert.Equal(UserAction.Reauth, ErrorCatalog.ActionFor(failure.Code));
        Assert.False(ErrorCatalog.IsRetriable(failure.Code));
        Assert.Contains(failure.Code, ErrorCatalog.NeverRetry);

        // Once. Presenting a refresh token that has already been refused,
        // over and over, is how a provider starts treating a client as
        // hostile - and it would lock the account rather than fix anything.
        Assert.Equal(1, upstream.TokenCalls);
        Assert.Equal(0, upstream.TicketCalls);
    }

    [Fact]
    public async Task A_401_on_a_live_looking_token_refreshes_once_and_gives_up_once()
    {
        var clock = new MovingClock(Day0);

        // The access token still looks live to us - the bundle says it has
        // half an hour left - and it is refused upstream anyway, with the
        // refresh grant gone too. This is the shape a revocation takes when
        // the expiry clock knows nothing about it.
        var upstream = new TokenServer(clock);
        var stored = Material(upstream.AccessToken, upstream.RefreshToken, clock.GetUtcNow().AddMinutes(30));
        upstream.Revoke();

        var handler = new StubHttpHandler(upstream.Route);
        using var ctx = Context(handler, stored);

        var failure = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, failure.Code);

        // One rejected call, one refresh attempt, and then it stops. Not a
        // page-by-page storm across the twenty pages the walk would allow.
        Assert.Equal(1, upstream.TicketCalls);
        Assert.Equal(1, upstream.TokenCalls);
    }

    // ── the refresh token is carried forward ─────────────────────────────

    /// <summary>
    /// The failure that only surfaces a week later. A refresh answers with a
    /// new access token and says nothing about the refresh token, which means
    /// "the one you have still stands". A client that reads that as "no
    /// refresh token" and persists the absence has turned a permanent
    /// connection into a one-hour one - and everything keeps working until the
    /// next access token expires, which is when the user finds out.
    /// </summary>
    [Theory]
    [InlineData(RefreshReissue.Absent)]
    [InlineData(RefreshReissue.Null)]
    [InlineData(RefreshReissue.Blank)]
    [InlineData(RefreshReissue.Whitespace)]
    public async Task A_refresh_that_reissues_nothing_carries_the_stored_refresh_token_forward(RefreshReissue reissue)
    {
        var clock = new MovingClock(Day0);
        var upstream = new TokenServer(clock) { Reissue = reissue };

        var stored = Material(upstream.AccessToken, upstream.RefreshToken, clock.GetUtcNow().AddMinutes(-1));
        var original = stored.RefreshToken;

        SessionMaterial rotated;
        using (var ctx = Context(new StubHttpHandler(upstream.Route), stored))
        {
            var result = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);
            rotated = Assert.IsType<SessionMaterial>(result.RefreshedMaterial);
        }

        // The access token rotated; the refresh token was kept, verbatim.
        Assert.NotEqual(stored.AccessToken, rotated.AccessToken);
        Assert.Equal(original, rotated.RefreshToken);

        // Proved rather than asserted: a month later the carried-forward token
        // is still the thing that keeps the connection alive.
        clock.Advance(TimeSpan.FromDays(30));
        using (var ctx = Context(new StubHttpHandler(upstream.Route), rotated))
        {
            var later = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);

            Assert.NotEmpty(later.Receipts);
            Assert.Empty(ctx.Asked);
        }
    }

    // ── a month of it ────────────────────────────────────────────────────

    [Fact]
    public async Task A_month_of_daily_fetches_never_asks_the_human_to_sign_in_again()
    {
        var clock = new MovingClock(Day0);
        var upstream = new TokenServer(clock);

        // One sign-in, at the start, and never again. This is what the client
        // put in its keychain that day.
        var stored = Material(upstream.AccessToken, upstream.RefreshToken, clock.GetUtcNow().AddSeconds(3600 - 60));

        var rotations = 0;

        for (var day = 1; day <= 30; day++)
        {
            clock.Advance(TimeSpan.FromDays(1));

            using var ctx = Context(new StubHttpHandler(upstream.Route), stored);
            var result = await Adapter(clock).FetchAsync(ctx, Requests.Receipts(since: Since), CancellationToken.None);

            Assert.NotEmpty(result.Receipts);

            // Nobody was asked anything and no browser was ever opened: an
            // unattended sync is the entire promise of holding a refresh token.
            Assert.Empty(ctx.Asked);
            Assert.False(ctx.Browser.Started);
            Assert.False(ctx.CredentialWasSubmitted);

            // The client persists whatever it is handed, exactly as
            // JobOutcomeService seals it into the next bundle.
            if (result.RefreshedMaterial is { } fresh)
            {
                stored = fresh;
                rotations++;
            }
        }

        // Every day's access token had expired overnight, so every day
        // refreshed - and every one of those handed the caller a new bundle.
        Assert.Equal(30, rotations);
        Assert.Equal(30, upstream.TokenCalls);

        // The refresh chain never broke: what the client holds on day 30 is
        // the newest grant the server issued, and every one of the thirty it
        // replaced was retired behind it. A client that had dropped or replayed
        // a single link would be holding a retired token here.
        Assert.Equal(upstream.RefreshToken, stored.RefreshToken);
        Assert.Equal(30, upstream.Retired.Count);
        Assert.DoesNotContain(stored.RefreshToken!, upstream.Retired);
    }

    // ── the world under test ─────────────────────────────────────────────

    private static LidlPlusAdapter Adapter(TimeProvider clock) => new(new LidlPlusOptions(), clock);

    private static FakeJobContext Context(HttpMessageHandler handler, SessionMaterial material) =>
        new(handler) { Config = DutchConfig, Material = material };

    /// <summary>
    /// A bundle's worth of material. The device id and the country ride along
    /// because a rotation that keeps only the tokens is a rotation that
    /// silently drops them.
    /// </summary>
    private static SessionMaterial Material(string access, string? refresh, DateTimeOffset expiresAt) => new()
    {
        AccessToken = access,
        RefreshToken = refresh,
        AccessTokenExpiresAt = expiresAt,
        DeviceId = "fixture-device",
        Extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["country"] = "NL",
            ["language"] = "nl",
        },
    };
}

/// <summary>What a refresh response says about the refresh token.</summary>
public enum RefreshReissue
{
    /// <summary>A new one, and the old one is retired. What a real rotating server does.</summary>
    Rotate,

    /// <summary>No <c>refresh_token</c> property at all.</summary>
    Absent,

    /// <summary><c>"refresh_token": null</c>.</summary>
    Null,

    /// <summary><c>"refresh_token": ""</c> - absence spelled as a value.</summary>
    Blank,

    /// <summary><c>"refresh_token": "   "</c> - the same, with padding.</summary>
    Whitespace,
}

/// <summary>
/// An OAuth server with a memory.
///
/// The memory is the point. A stub that accepts any refresh token proves
/// nothing about rotation, because dropping the new token and replaying the
/// old one looks identical to doing it right. This one retires what it
/// rotates and refuses a retired grant with the 400 the real endpoint sends,
/// so "the client persisted the newest" is a property the tests can actually
/// observe.
/// </summary>
internal sealed class TokenServer
{
    private readonly TimeProvider _clock;
    private readonly List<string> _retired = [];
    private int _generation;

    public TokenServer(TimeProvider clock)
    {
        _clock = clock;
        AccessToken = "access-0";
        RefreshToken = "refresh-0";
    }

    public string AccessToken { get; private set; }

    public string RefreshToken { get; private set; }

    public RefreshReissue Reissue { get; init; } = RefreshReissue.Rotate;

    public int AccessTokenSeconds { get; init; } = 3600;

    public int TokenCalls { get; private set; }

    public int TicketCalls { get; private set; }

    public IReadOnlyList<string> Retired => _retired;

    /// <summary>
    /// Everything this client holds stops working: the bearer token is
    /// refused and the refresh grant is retired. The end of the road, and the
    /// only case in the whole loop where a human has to sign in again.
    /// </summary>
    public void Revoke()
    {
        _retired.Add(RefreshToken);
        AccessToken = "access-revoked";
        RefreshToken = "refresh-revoked";
    }

    public HttpResponseMessage Route(RecordedRequest request, int _)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Path.EndsWith("/connect/token", StringComparison.Ordinal)) return Token(request);

        TicketCalls++;

        if (!string.Equals(request.Header("Authorization"), $"Bearer {AccessToken}", StringComparison.Ordinal))
        {
            return Stub.Status(HttpStatusCode.Unauthorized);
        }

        if (request.Path.StartsWith("/api/v3/", StringComparison.Ordinal)) return Stub.Fixture("lidl/ticket-detail.json");

        return request.Query.Contains("pageNumber=1", StringComparison.Ordinal)
            ? Stub.Fixture("lidl/tickets-page-1.json")
            : Stub.Fixture("lidl/tickets-page-2.json");
    }

    private HttpResponseMessage Token(RecordedRequest request)
    {
        TokenCalls++;

        var presented = FormValue(request.Body, "refresh_token");

        // A retired grant is refused with the same 400 the real endpoint
        // sends, and so is an empty one - a client that dropped its refresh
        // token presents nothing and must not be told "unauthorized" as if
        // the token were merely stale.
        if (string.IsNullOrWhiteSpace(presented) || !string.Equals(presented, RefreshToken, StringComparison.Ordinal))
        {
            return Stub.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        }

        _generation++;
        AccessToken = $"access-{_generation}";

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["access_token"] = AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenSeconds,
        };

        switch (Reissue)
        {
            case RefreshReissue.Rotate:
                _retired.Add(RefreshToken);
                RefreshToken = $"refresh-{_generation}";
                body["refresh_token"] = RefreshToken;
                break;

            case RefreshReissue.Null:
                body["refresh_token"] = null;
                break;

            case RefreshReissue.Blank:
                body["refresh_token"] = string.Empty;
                break;

            case RefreshReissue.Whitespace:
                body["refresh_token"] = "   ";
                break;

            case RefreshReissue.Absent:
            default:
                break;
        }

        // Referenced so the clock is a real dependency rather than decoration:
        // the expiry the adapter computes is relative to it, not to wall time.
        _ = _clock.GetUtcNow();

        return Stub.Json(JsonSerializer.Serialize(body));
    }

    private static string? FormValue(string? body, string key)
    {
        if (body is null) return null;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            if (!pair.AsSpan(0, separator).SequenceEqual(key)) continue;

            return Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return null;
    }
}

/// <summary>
/// A clock a test can push forward.
///
/// <c>FixedTimeProvider</c> cannot express the only thing that matters here -
/// that an hour passed between two fetches - and only <c>GetUtcNow</c> is
/// overridden, so nothing accidentally starts depending on a fake timer.
/// </summary>
internal sealed class MovingClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
