using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.Mock;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The offline backbone, held to its own promise: six registered providers,
/// one adapter, and not one byte of network traffic between them.
///
/// If a mock ever reached the network it would stop being usable in CI, in a
/// demo, or on a machine with no egress - which is the entire reason it
/// exists.
/// </summary>
public sealed class MockStoreAdapterTests
{
    /// <summary>Zero step delay: the suite must exercise the slow profile without sleeping.</summary>
    private static readonly MockStoreOptions Fast = new() { SlowStepDelay = TimeSpan.Zero };

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> GoodCredentials =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "demo@example.invalid",
            ["password"] = "demo",
        };

    public static TheoryData<string> AllMockIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in MockStoreAdapters.Ids) data.Add(id);
            return data;
        }
    }

    private static MockStoreAdapter Adapter(string id) =>
        MockStoreAdapters.Create(id, Fast, new FixedTimeProvider(Now));

    private static string AnswerFor(Challenge challenge) => challenge.Type switch
    {
        ChallengeType.MfaCode => "123456",
        ChallengeType.Image => "MOCK1",
        _ => throw new InvalidOperationException($"unexpected challenge {challenge.Type}"),
    };

    [Theory]
    [MemberData(nameof(AllMockIds))]
    public async Task A_full_login_and_fetch_makes_no_network_call_at_all(string providerId)
    {
        var handler = new ThrowingHttpHandler();
        using var ctx = new FakeJobContext(handler)
        {
            Inputs = GoodCredentials,
            Answer = AnswerFor,
        };

        var adapter = Adapter(providerId);
        await adapter.LoginAsync(ctx, CancellationToken.None);

        try
        {
            await adapter.FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);
        }
        catch (ConnectorException error) when (error.Code == ErrorCode.ProviderChanged)
        {
            // mock-store-broken exists to produce exactly this, and it must
            // produce it without contacting anything either.
            Assert.Equal(MockStoreAdapters.Broken, providerId);
        }

        Assert.Equal(0, handler.Calls);

        // Nor a browser: the lease throws on any use, including the T4 mock,
        // whose tier exists for routing rather than as an instruction to
        // start Chromium.
        Assert.False(ctx.Browser.Started);
    }

    [Fact]
    public async Task The_simple_mock_returns_deterministic_reconciled_receipts()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials };

        var result = await Adapter(MockStoreAdapters.Simple)
            .FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.Equal(3, result.Receipts.Count);
        Assert.Equal("fixture:mock/receipts.json", result.Via);
        Assert.True(result.Complete);

        var newest = result.Receipts[0];
        Assert.Equal("mock-2026-07-19-0001", newest.ExternalId);
        Assert.Equal(1085, newest.Total.Value);
        Assert.Equal(TimeSpan.FromHours(2), newest.PurchasedAt.Offset);
        Assert.Equal("card", newest.Payment?.Method);
        Assert.Equal("1234", newest.Payment?.CardLast4);
        Assert.Null(newest.Payment?.IbanTail);

        var ideal = result.Receipts[1];
        Assert.Equal("ideal", ideal.Payment?.Method);
        Assert.Null(ideal.Payment?.CardLast4);
        Assert.Equal("4300", ideal.Payment?.IbanTail);
    }

    [Fact]
    public async Task A_receipt_that_does_not_reconcile_is_emitted_with_the_verdict_rather_than_dropped()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials };

        var result = await Adapter(MockStoreAdapters.Simple)
            .FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        // 329 in line items against a stated total of 500. Silently dropping
        // it would hide a real purchase; silently trusting it would hand over
        // a total we know disagrees with its own contents.
        var suspect = Assert.Single(result.Receipts, r => !r.Reconciled);
        Assert.Equal("mock-2026-06-28-0003", suspect.ExternalId);
        Assert.Equal(500, suspect.Total.Value);
        Assert.Equal(329, suspect.Items.Sum(i => i.Total.Value));

        Assert.All(result.Receipts.Where(r => r.ExternalId != suspect.ExternalId), r => Assert.True(r.Reconciled));
    }

    [Fact]
    public async Task Ids_and_content_hashes_are_stable_across_runs_of_the_same_session()
    {
        using var first = new FakeJobContext { Inputs = GoodCredentials };
        using var second = new FakeJobContext { Inputs = GoodCredentials };

        var a = await Adapter(MockStoreAdapters.Simple).FetchAsync(first, Requests.Receipts(), CancellationToken.None);
        var b = await Adapter(MockStoreAdapters.Simple).FetchAsync(second, Requests.Receipts(), CancellationToken.None);

        // Deterministic ids plus a content hash are what make a re-fetch free
        // for the caller: the same upstream data must produce the same rows.
        Assert.Equal(a.Receipts.Select(r => r.Id), b.Receipts.Select(r => r.Id));
        Assert.Equal(a.Receipts.Select(r => r.ContentHash), b.Receipts.Select(r => r.ContentHash));
    }

    [Fact]
    public async Task Asking_for_items_changes_the_content_hash_because_it_is_different_content()
    {
        using var withItems = new FakeJobContext { Inputs = GoodCredentials };
        using var without = new FakeJobContext { Inputs = GoodCredentials };

        var detailed = await Adapter(MockStoreAdapters.Simple)
            .FetchAsync(withItems, Requests.Receipts(), CancellationToken.None);
        var summary = await Adapter(MockStoreAdapters.Simple)
            .FetchAsync(without, Requests.Receipts(items: false), CancellationToken.None);

        Assert.Equal(detailed.Receipts[0].Id, summary.Receipts[0].Id);
        Assert.NotEqual(detailed.Receipts[0].ContentHash, summary.Receipts[0].ContentHash);
        Assert.Empty(summary.Receipts[0].Items);
    }

    [Fact]
    public async Task The_window_is_honoured_inclusively_at_both_ends()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials };

        var result = await Adapter(MockStoreAdapters.Simple).FetchAsync(
            ctx,
            Requests.Receipts(since: Requests.Day(2026, 7, 5), until: Requests.Day(2026, 7, 19)),
            CancellationToken.None);

        Assert.Equal(2, result.Receipts.Count);
        Assert.DoesNotContain(result.Receipts, r => r.ExternalId == "mock-2026-06-28-0003");
    }

    // ---- the challenge relay -----------------------------------------------

    [Fact]
    public async Task The_sms_mock_relays_a_code_challenge_and_accepts_the_right_answer()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials, Answer = AnswerFor };

        await Adapter(MockStoreAdapters.Sms).LoginAsync(ctx, CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.MfaCode, challenge.Type);
        Assert.Equal("sms", challenge.Delivery);
        Assert.Equal(6, challenge.Length);

        // Mandatory, because a challenge holds a live browser hostage.
        Assert.True(challenge.ExpiresAt > Now);
        Assert.Contains(JobStep.AwaitingHuman, ctx.Steps);
    }

    [Fact]
    public async Task The_captcha_mock_relays_real_image_bytes_and_never_solves_them()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials, Answer = AnswerFor };

        await Adapter(MockStoreAdapters.Captcha).LoginAsync(ctx, CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.Image, challenge.Type);
        Assert.NotNull(challenge.Image);
        Assert.NotEmpty(challenge.Image);

        // A PNG signature, so the bytes really do survive capture, upload and
        // rendering rather than being a placeholder string.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, challenge.Image.Take(4));
        Assert.NotNull(challenge.Crop);
    }

    [Fact]
    public async Task A_wrong_challenge_answer_is_mfa_failed_and_is_never_retried()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials, Answer = _ => "000000" };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(MockStoreAdapters.Sms).LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.MfaFailed, error.Code);
        Assert.Contains(ErrorCode.MfaFailed, ErrorCatalog.NeverRetry);
    }

    // ---- the rule that matters most ----------------------------------------

    [Fact]
    public async Task A_rejected_password_latches_the_credential_and_is_never_retried()
    {
        using var ctx = new FakeJobContext
        {
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = "demo@example.invalid",
                ["password"] = "wrong",
            },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(MockStoreAdapters.Simple).LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
        Assert.False(error.Retriable);
        Assert.Contains(ErrorCode.InvalidCredentials, ErrorCatalog.NeverRetry);

        // The credential went upstream and may already have counted, so a
        // lost lease must fail this job rather than requeue it. Three retries
        // is how a real account gets locked.
        Assert.True(ctx.CredentialWasSubmitted);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    public async Task A_missing_login_input_is_refused_before_anything_is_submitted(string omitted)
    {
        var inputs = new Dictionary<string, string>(GoodCredentials, StringComparer.Ordinal);
        inputs.Remove(omitted);

        using var ctx = new FakeJobContext { Inputs = inputs };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(MockStoreAdapters.Simple).LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.False(ctx.CredentialWasSubmitted);
    }

    // ---- the alert path and BYO routing ------------------------------------

    [Fact]
    public async Task The_broken_mock_fails_every_fetch_with_the_code_that_pages_an_operator()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(MockStoreAdapters.Broken).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.False(error.Retriable);
        Assert.Equal(502, error.HttpStatus);
    }

    [Fact]
    public async Task The_persistent_mock_returns_a_pointer_and_no_secret_at_all()
    {
        using var ctx = new FakeJobContext();

        var result = await Adapter(MockStoreAdapters.Persistent).LoginAsync(ctx, CancellationToken.None);

        // Agent custody: the control plane never holds a credential for this
        // connection, so a breach of the connector yields nothing.
        Assert.True(result.Material.IsAgentPointer);
        Assert.Null(result.Material.AccessToken);
        Assert.Null(result.Material.RefreshToken);
        Assert.Null(result.Material.StorageState);
        Assert.StartsWith("agt_", result.Material.AgentId, StringComparison.Ordinal);
        Assert.StartsWith("prf_", result.Material.ProfileId, StringComparison.Ordinal);

        // Nothing was submitted upstream: the human authenticated into the
        // profile on their own machine long before this job ran.
        Assert.False(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task The_slow_mock_walks_the_progress_vocabulary_a_consumer_renders()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials };

        await Adapter(MockStoreAdapters.Slow).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        // Typed steps rather than free text: a status string cannot be
        // translated and cannot drive a progress bar.
        Assert.Contains(JobStep.OpeningProvider, ctx.Steps);
        Assert.Contains(JobStep.Downloading, ctx.Steps);
        Assert.Contains(JobStep.Normalizing, ctx.Steps);
        Assert.Contains(JobStep.Finalizing, ctx.Steps);
    }

    [Fact]
    public async Task An_unknown_resource_is_refused()
    {
        using var ctx = new FakeJobContext { Inputs = GoodCredentials };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(MockStoreAdapters.Simple).FetchAsync(
                ctx, new ResourceRequest { ResourceId = "orders" }, CancellationToken.None));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
    }

    [Fact]
    public void Logout_is_a_no_op_that_always_succeeds()
    {
        using var ctx = new FakeJobContext();

        // A user disconnecting must always succeed locally, whatever the
        // provider does.
        IProviderAdapter adapter = Adapter(MockStoreAdapters.Simple);
        Assert.True(adapter.LogoutAsync(ctx, CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void Every_fixture_the_catalogue_advertises_is_readable()
    {
        // The mocks read their data from the same catalogue the parse tests
        // do, so a fixture that rots breaks the offline suite immediately.
        Assert.NotEmpty(FixtureNames);
        Assert.All(FixtureNames, name => Assert.False(string.IsNullOrWhiteSpace(Adapters.Fixtures.FixtureCatalog.Read(name))));
    }

    private static IReadOnlyList<string> FixtureNames => Adapters.Fixtures.FixtureCatalog.Names;
}
