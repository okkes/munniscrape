using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using RegistryConnector.Adapters;
using RegistryConnector.Adapters.Mock;
using Xunit;

namespace RegistryConnector.Adapters.Tests;

/// <summary>
/// The registry service's own shape, proved before its first real provider
/// exists.
///
/// The thing worth proving is not the fixture data - it is that a provider
/// which re-authenticates on EVERY sync is expressible here at all. Every
/// provider on this platform before it either syncs unattended or needs
/// nobody after the first login; a credit register needs six digits from a
/// human every single time, and a consumer has to be told that rather than
/// discovering it at 3am.
/// </summary>
public sealed class MockRegistryTests
{
    private static readonly IProviderRegistry Registry =
        new ProviderRegistry(RegistryAdapters.All());

    [Fact]
    public void Every_registry_manifest_validates_and_is_a_registry()
    {
        Assert.NotEmpty(Registry.Manifests);

        foreach (var manifest in Registry.Manifests)
        {
            // Throws with every failing rule listed; a manifest that cannot
            // boot the host must not pass the suite either.
            ManifestValidator.Validate(manifest);

            Assert.Equal(ProviderKind.Registry, manifest.Kind);
        }
    }

    /// <summary>
    /// A credit register states what is true NOW, not what happened in a
    /// window. Declaring `since` would invite a question it has no way to
    /// answer.
    /// </summary>
    [Fact]
    public void The_credits_resource_takes_no_date_range()
    {
        foreach (var manifest in Registry.Manifests)
        {
            var credits = manifest.Resource(MockRegistryAdapter.CreditsResource);

            Assert.NotNull(credits);
            Assert.Equal(ResourceShape.CreditRegistration, credits.Returns);
            Assert.Null(credits.Param("since"));
            Assert.Null(credits.Param("until"));
        }
    }

    /// <summary>
    /// The field that stops a consumer promising something it cannot deliver.
    /// A provider needing a fresh code per sync cannot be scheduled, and the
    /// one that does not can.
    /// </summary>
    [Theory]
    [InlineData(MockRegistryAdapters.Simple, true)]
    [InlineData(MockRegistryAdapters.TwoFactor, false)]
    public void Unattended_sync_is_offered_only_where_no_human_is_needed(string providerId, bool unattended)
    {
        var manifest = Registry.RequireManifest(providerId);

        Assert.Equal(unattended, manifest.UnattendedFetch);

        // And the store is offered exactly where it earns its keep: a password
        // that never changes is worth keeping, six digits that change every
        // thirty seconds are not.
        Assert.Equal(!unattended, manifest.OffersCredentialStore);
    }

    [Fact]
    public void A_two_factor_registry_declares_the_code_it_will_ask_for()
    {
        var manifest = Registry.RequireManifest(MockRegistryAdapters.TwoFactor);

        Assert.Equal(AuthFlow.PasswordTotp, manifest.Auth.Flow);
        Assert.Equal([ChallengeType.MfaCode], manifest.Auth.Challenges);

        // Not refreshable, because there is nothing to refresh: the next sync
        // signs in again from the stored password and a new code.
        Assert.False(manifest.Auth.Session.Refreshable);
        Assert.False(manifest.Auth.Session.RotatesOnUse);
    }

    // ---- the credits themselves --------------------------------------------

    [Fact]
    public async Task A_fetch_states_each_credit_the_way_the_register_does()
    {
        var adapter = new MockRegistryAdapter(MockRegistryAdapters.Simple, "Mock registry", twoFactor: false);

        using var ctx = new FakeJobContext();
        var result = await adapter.FetchAsync(ctx, Requests.Credits(), CancellationToken.None);

        Assert.Equal(3, result.Registrations.Count);

        var revolving = Assert.Single(result.Registrations, c => c.ExternalId == "MOCK-0001");
        Assert.Equal("Mock Bank N.V.", revolving.Creditor);
        Assert.Equal(CreditKind.Revolving, revolving.Kind);
        Assert.Equal(CreditStatus.Running, revolving.Status);
        Assert.Equal(200_000, revolving.Amount.Value);
        Assert.Equal("EUR", revolving.Amount.Currency);

        // Revolving credit states no end date, and null is the honest answer
        // rather than a date computed from a term it does not have.
        Assert.Null(revolving.EndsOn);

        // The registry's own words ride along beside the mapped enum, so a
        // label this connector does not recognise still reaches the user.
        Assert.Equal("Doorlopend krediet", revolving.KindLabel);
    }

    /// <summary>
    /// The single most consequential field in a credit register, carried
    /// verbatim. A connector that softened or summarised an arrears code would
    /// be hiding the one thing that decides a mortgage application.
    /// </summary>
    [Fact]
    public async Task An_arrears_code_is_carried_exactly_as_stated()
    {
        var adapter = new MockRegistryAdapter(MockRegistryAdapters.Simple, "Mock registry", twoFactor: false);

        using var ctx = new FakeJobContext();
        var result = await adapter.FetchAsync(ctx, Requests.Credits(), CancellationToken.None);

        var ended = Assert.Single(result.Registrations, c => c.ExternalId == "MOCK-0003");

        Assert.Equal("A2", ended.ArrearsCode);
        Assert.Equal(CreditStatus.Ended, ended.Status);

        // And a credit with nothing against it says nothing, rather than "none"
        // or "A0" - both of which are claims the register never made.
        Assert.All(
            result.Registrations.Where(c => c.ExternalId != "MOCK-0003"),
            c => Assert.Null(c.ArrearsCode));
    }

    // ---- the code, on every sync -------------------------------------------

    [Fact]
    public async Task A_two_factor_fetch_asks_for_six_digits_and_will_not_proceed_without_them()
    {
        var adapter = new MockRegistryAdapter(MockRegistryAdapters.TwoFactor, "Mock registry", twoFactor: true);

        using var ctx = new FakeJobContext { Answer = _ => MockRegistryAdapters.AcceptedCode };

        var result = await adapter.FetchAsync(ctx, Requests.Credits(), CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.MfaCode, challenge.Type);
        Assert.Equal(6, challenge.Length);

        Assert.Equal(3, result.Registrations.Count);
    }

    /// <summary>
    /// A wrong code is not a wrong password. Reporting it as one sends
    /// somebody to reset a credential that was never the problem, and on a
    /// provider that locks accounts that is an expensive place to send them.
    /// </summary>
    [Fact]
    public async Task A_wrong_code_is_an_mfa_failure_and_never_a_credential_one()
    {
        var adapter = new MockRegistryAdapter(MockRegistryAdapters.TwoFactor, "Mock registry", twoFactor: true);

        using var ctx = new FakeJobContext { Answer = _ => "000000" };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => adapter.FetchAsync(ctx, Requests.Credits(), CancellationToken.None));

        Assert.Equal(ErrorCode.MfaFailed, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_registry_with_no_second_factor_asks_nothing_at_all()
    {
        var adapter = new MockRegistryAdapter(MockRegistryAdapters.Simple, "Mock registry", twoFactor: false);

        using var ctx = new FakeJobContext();
        await adapter.FetchAsync(ctx, Requests.Credits(), CancellationToken.None);

        Assert.Empty(ctx.Asked);
    }
}
