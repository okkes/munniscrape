using Connector.Kit.Challenges;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// Three small contracts that the state machines lean on: what a caller
/// asked for, whether a challenge is still worth waiting on, and whether a
/// provider is accepting work at all.
/// </summary>
public sealed class RequestAndStatusTests
{
    [Fact]
    public void An_absent_selection_reads_as_empty_and_never_as_null()
    {
        var request = new ResourceRequest { ResourceId = "transactions" };

        // An adapter that has to null-check every selection eventually
        // forgets one, and the forgotten one is a fetch that returns
        // everything instead of what was asked for.
        Assert.Empty(request.Include);
        Assert.Empty(request.Accounts);
        Assert.False(request.WantsItems);
    }

    [Fact]
    public void Selections_are_surfaced_by_name()
    {
        var request = new ResourceRequest
        {
            ResourceId = "transactions",
            Since = new DateOnly(2026, 1, 1),
            Selections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["accounts"] = ["savings", "credit_card"],
                ["include"] = ["items"],
            },
        };

        Assert.Equal(new[] { "savings", "credit_card" }, request.Accounts);
        Assert.Equal(new[] { "items" }, request.Include);
        Assert.True(request.WantsItems);
        Assert.Equal(new DateOnly(2026, 1, 1), request.Since);
        Assert.Null(request.Until);
    }

    [Fact]
    public void Include_matching_is_ordinal()
    {
        var request = new ResourceRequest
        {
            ResourceId = "receipts",
            Selections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["include"] = ["Items"],
            },
        };

        // The manifest declares the legal values in lower case; accepting a
        // near-miss here would make the manifest's enum advisory.
        Assert.False(request.WantsItems);
    }

    [Fact]
    public void A_challenge_expires_at_its_stated_instant()
    {
        var challenge = new Challenge { Type = ChallengeType.Image, ExpiresAt = TestTime.Anchor };

        // A challenge holds a live browser hostage, so a stale one must fail
        // the job cleanly rather than pinning an agent for the full timeout.
        Assert.False(challenge.IsExpired(TestTime.Anchor.AddTicks(-1)));
        Assert.True(challenge.IsExpired(TestTime.Anchor));
        Assert.True(challenge.IsExpired(TestTime.Anchor.AddMinutes(1)));
    }

    [Theory]
    [InlineData(ChallengeType.AppApproval, true)]
    [InlineData(ChallengeType.Image, false)]
    [InlineData(ChallengeType.MfaCode, false)]
    [InlineData(ChallengeType.CodeDisplay, false)]
    [InlineData(ChallengeType.QrDisplay, false)]
    [InlineData(ChallengeType.SelectOption, false)]
    [InlineData(ChallengeType.Redirect, false)]
    public void Only_app_approval_expects_no_typed_answer(ChallengeType type, bool passive)
    {
        var challenge = new Challenge { Type = type, ExpiresAt = TestTime.Anchor.AddMinutes(3) };

        Assert.Equal(passive, challenge.IsPassive);
    }

    [Fact]
    public void A_challenge_never_serialises_its_image_or_crop()
    {
        // The bytes are ephemeral and operator-controlled; they travel as a
        // separate authenticated fetch, never inside a status payload.
        var image = typeof(Challenge).GetProperty(nameof(Challenge.Image))!;
        var crop = typeof(Challenge).GetProperty(nameof(Challenge.Crop))!;

        Assert.NotEmpty(image.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
        Assert.NotEmpty(crop.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
    }

    [Theory]
    [InlineData(ProviderState.Healthy, true)]
    [InlineData(ProviderState.Degraded, true)]
    [InlineData(ProviderState.Paused, false)]
    [InlineData(ProviderState.Retired, false)]
    public void The_kill_switch_stops_new_work_without_stopping_a_degraded_provider(
        ProviderState state, bool acceptsWork)
    {
        var status = new ProviderStatus { ProviderId = "jumbo", State = state, Since = TestTime.Anchor };

        // Degraded means "something is wrong and an operator knows"; work
        // still flows. Paused is the switch that actually stops it.
        Assert.Equal(acceptsWork, status.AcceptsWork);
    }

    [Fact]
    public void A_healthy_status_is_healthy_and_dated()
    {
        var status = ProviderStatus.Healthy("jumbo", TestTime.Anchor);

        Assert.Equal(ProviderState.Healthy, status.State);
        Assert.Equal(TestTime.Anchor, status.Since);
        Assert.True(status.AcceptsWork);
        Assert.Null(status.ReasonKey);
    }

    [Fact]
    public void An_agent_serves_only_what_it_advertises()
    {
        var manifest = Make.Manifest() with { Id = "jumbo", Runtime = ProviderRuntime.Http };

        var anything = new Connector.Kit.AgentProtocol.AgentCapabilities();
        var wrongProvider = new Connector.Kit.AgentProtocol.AgentCapabilities { Providers = ["asn"] };
        var wrongRuntime = new Connector.Kit.AgentProtocol.AgentCapabilities
        {
            Runtimes = [ProviderRuntime.BrowserPersistent],
        };
        var match = new Connector.Kit.AgentProtocol.AgentCapabilities
        {
            Providers = ["jumbo"],
            Runtimes = [ProviderRuntime.Http],
        };

        // Empty means "any" on both axes, which is what lets a pooled agent
        // opt into everything without listing it.
        Assert.True(anything.CanServe(manifest));
        Assert.False(wrongProvider.CanServe(manifest));
        Assert.False(wrongRuntime.CanServe(manifest));
        Assert.True(match.CanServe(manifest));
    }
}
