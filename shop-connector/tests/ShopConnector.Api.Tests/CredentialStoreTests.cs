using System.Net;
using System.Text.Json;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// The credential store, end to end.
///
/// It is for one shape of provider: a session that cannot be refreshed, so a
/// fresh password is wanted again within a day, and the alternative is asking
/// the same human every morning. Jumbo is the case in production; this drives
/// the same shape offline.
///
/// The custody argument is the session bundle's, applied to the thing that
/// produces sessions: the connector seals it, hands it over once, and keeps no
/// copy. What differs is what leaks if it escapes - a password does not rotate
/// and cannot be revoked from here - so the tests that matter most are the ones
/// about who can redeem it, and that it is never handed out twice.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class CredentialStoreTests(ShopApiFactory factory)
{
    private const string Provider = DailyLoginAdapter.ProviderId;

    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = DailyLoginAdapter.Username,
        ["password"] = DailyLoginAdapter.Password,
    };

    [Fact]
    public async Task A_provider_that_offers_a_store_says_so_in_the_catalogue()
    {
        using var http = factory.CreateAuthorizedClient();

        using var response = await http.GetAsync($"/v1/providers/{Provider}");
        var manifest = await response.JsonAsync();

        Assert.True(manifest.GetProperty("offers_credential_store").GetBoolean());
    }

    [Fact]
    public async Task A_successful_login_hands_back_what_was_typed_sealed_for_this_device()
    {
        using var http = factory.CreateAuthorizedClient();

        var login = await LoginAsync(http, Flows.NewSubject("cred-mint"), Credentials);

        var bundle = login.GetProperty("credential_bundle").GetString();
        Assert.NotNull(bundle);
        Assert.StartsWith("cb_v1.", bundle, StringComparison.Ordinal);

        // Sealed, not merely carried. If this ever became plaintext the round
        // trip below would still pass and nothing else would notice.
        Assert.DoesNotContain(DailyLoginAdapter.Password, bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the whole thing: tomorrow's login sends no password.
    /// </summary>
    [Fact]
    public async Task Tomorrows_login_offers_the_bundle_instead_of_asking_again()
    {
        using var http = factory.CreateAuthorizedClient();
        var subject = Flows.NewSubject("cred-redeem");

        var first = await LoginAsync(http, subject, Credentials);
        var bundle = first.GetProperty("credential_bundle").GetString()!;

        var second = await LoginAsync(http, subject, inputs: null, credentialBundle: bundle);

        Assert.Equal("active", second.Text("state"));

        // The adapter echoes back the username it was actually handed, so this
        // proves the stored credential reached it - not merely that a login
        // with no inputs somehow succeeded.
        Assert.Equal(
            DailyLoginAdapter.Username,
            second.GetProperty("provider_account").GetProperty("external_id").GetString());
    }

    [Fact]
    public async Task The_bundle_is_handed_over_once_and_then_forgotten()
    {
        using var http = factory.CreateAuthorizedClient();
        var subject = Flows.NewSubject("cred-once");

        var login = await LoginAsync(http, subject, Credentials);
        var sessionId = login.Text("session_id");

        Assert.NotNull(login.GetProperty("credential_bundle").GetString());

        // The same delivery rule the session bundle has: collected once, then
        // the connector no longer holds it.
        using var poll = await http.GetAsync($"/v1/{Provider}/login/{sessionId}");
        var again = await poll.JsonAsync();

        Assert.False(
            again.TryGetProperty("credential_bundle", out var repeat) && repeat.ValueKind != JsonValueKind.Null,
            "a credential bundle must never be delivered twice");
    }

    /// <summary>
    /// Disconnect means forget, and it has to cover a bundle nobody collected.
    ///
    /// A credential bundle is cleared when it is handed over - so a login whose
    /// bundle was never fetched keeps it on the row. Every other secret is
    /// dropped by a purge; this one was not, which made the single action a
    /// user takes to be rid of a connection the one that left their sealed
    /// password behind.
    /// </summary>
    [Fact]
    public async Task Disconnecting_forgets_a_credential_bundle_nobody_collected()
    {
        using var http = factory.CreateAuthorizedClient();
        var subject = Flows.NewSubject("cred-purge");

        var login = await LoginAsync(http, subject, Credentials);
        var sessionId = login.Text("session_id");

        // Put one back on the row without collecting it, which is what a login
        // whose caller walked away looks like.
        Db.Write(factory, db =>
        {
            var session = db.Sessions.Single(s => s.Id == sessionId);
            session.PendingCredentialBundle = "cb_v1.uncollected";
            session.PendingBundle = "sb_v1.uncollected";
        });

        using var disconnect = await http.DeleteAsync($"/v1/{Provider}/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        var left = Db.Read(factory, db => db.Sessions
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.PendingBundle, s.PendingCredentialBundle })
            .Single());

        Assert.Null(left.PendingBundle);
        Assert.Null(left.PendingCredentialBundle);
    }

    // ---- who may redeem it -------------------------------------------------

    /// <summary>
    /// The authorisation, and the one that matters most here: the subject is
    /// part of the sealed blob's associated data, so a bundle lifted from
    /// somebody else is useless without being them.
    /// </summary>
    [Fact]
    public async Task A_bundle_belonging_to_somebody_else_is_refused()
    {
        using var http = factory.CreateAuthorizedClient();

        var mine = await LoginAsync(http, Flows.NewSubject("cred-owner"), Credentials);
        var bundle = mine.GetProperty("credential_bundle").GetString()!;

        using var request = Wire.Post($"/v1/{Provider}/login", new
        {
            subject = Flows.NewSubject("cred-thief"),
            credential_bundle = bundle,
        });

        using var response = await http.SendAsync(request);

        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
    }

    [Theory]
    [InlineData("not-a-bundle")]
    [InlineData("cb_v1.k1.AAAA.BBBB.CCCC")]
    public async Task A_bundle_that_does_not_open_is_refused_rather_than_ignored(string bundle)
    {
        using var http = factory.CreateAuthorizedClient();

        using var request = Wire.Post($"/v1/{Provider}/login", new
        {
            subject = Flows.NewSubject("cred-garbage"),
            credential_bundle = bundle,
        });

        using var response = await http.SendAsync(request);

        // Refused, not silently downgraded to "you sent no inputs" - a caller
        // whose stored credential has gone stale needs to be told to collect a
        // password, and an invalid_request about a missing field would send
        // them somewhere else entirely.
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
    }

    /// <summary>
    /// A caller that sends both is taken at the word of what it typed. The
    /// human is in front of the form; the bundle is a memory of an older one.
    /// </summary>
    [Fact]
    public async Task What_was_typed_now_wins_over_what_was_remembered()
    {
        using var http = factory.CreateAuthorizedClient();
        var subject = Flows.NewSubject("cred-both");

        var first = await LoginAsync(http, subject, Credentials);
        var bundle = first.GetProperty("credential_bundle").GetString()!;

        var wrong = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = DailyLoginAdapter.Username,
            ["password"] = "the-password-was-changed",
        };

        using var request = Wire.Post($"/v1/{Provider}/login", new
        {
            subject,
            inputs = wrong,
            credential_bundle = bundle,
        });

        using var response = await http.SendAsync(request);

        // The typed password is wrong, so the login fails. Had the stored one
        // won, this would have succeeded and quietly signed the user in with a
        // credential they had just replaced.
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    // ---- the gate ----------------------------------------------------------

    [Fact]
    public async Task A_provider_that_offers_no_store_hands_back_no_bundle()
    {
        using var http = factory.CreateAuthorizedClient();

        var login = await LoginAsync(
            http,
            Flows.NewSubject("cred-absent"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = RotatingStoreAdapter.Username,
                ["password"] = RotatingStoreAdapter.Password,
            },
            provider: RotatingStoreAdapter.ProviderId);

        Assert.False(
            login.TryGetProperty("credential_bundle", out var bundle) && bundle.ValueKind != JsonValueKind.Null,
            "a provider whose session refreshes itself must not store a password");
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<JsonElement> LoginAsync(
        HttpClient http,
        string subject,
        IReadOnlyDictionary<string, string>? inputs,
        string? credentialBundle = null,
        string? provider = null)
    {
        using var request = Wire.Post($"/v1/{provider ?? Provider}/login", new
        {
            subject,
            inputs,
            credential_bundle = credentialBundle,
        });

        using var response = await http.SendAsync(request);
        var body = await response.JsonAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"expected the login to succeed, got {(int)response.StatusCode}: {body}");

        return body;
    }
}
