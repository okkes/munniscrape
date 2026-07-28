using Connector.Kit.Errors;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// The retry policy. One assertion in this file matters more than the rest
/// of the suite put together, and it has its own test with its own name.
/// </summary>
public sealed class ErrorCatalogTests
{
    /// <summary>
    /// The table published in connector-api-spec.md §5, transcribed. It is
    /// the contract every consumer was handed, so a change here is a
    /// breaking change whether or not anyone noticed.
    /// </summary>
    private static readonly (ErrorCode Code, string Wire, int Status, bool Retriable, UserAction Action)[] Published =
    [
        (ErrorCode.InvalidCredentials, "invalid_credentials", 401, false, UserAction.Reauth),
        (ErrorCode.SessionExpired, "session_expired", 401, false, UserAction.Reauth),
        (ErrorCode.MfaFailed, "mfa_failed", 401, false, UserAction.Reauth),
        (ErrorCode.MfaTimeout, "mfa_timeout", 408, true, UserAction.Retry),
        (ErrorCode.ChallengeExpired, "challenge_expired", 410, true, UserAction.Retry),
        (ErrorCode.BlockedByProvider, "blocked_by_provider", 403, false, UserAction.Wait),
        (ErrorCode.ProviderChanged, "provider_changed", 502, false, UserAction.Wait),
        (ErrorCode.ProviderUnavailable, "provider_unavailable", 503, true, UserAction.Retry),
        (ErrorCode.RateLimited, "rate_limited", 429, true, UserAction.Wait),
        (ErrorCode.AgentUnavailable, "agent_unavailable", 503, true, UserAction.StartYourAgent),
        (ErrorCode.UnsupportedResource, "unsupported_resource", 400, false, UserAction.None),
        (ErrorCode.InvalidRequest, "invalid_request", 400, false, UserAction.None),
        (ErrorCode.ConsentExpired, "consent_expired", 403, false, UserAction.Reconnect),
        (ErrorCode.ReconciliationFailed, "reconciliation_failed", 502, false, UserAction.Wait),
        (ErrorCode.Internal, "internal", 500, true, UserAction.Retry),
    ];

    public static TheoryData<ErrorCode> EveryCode()
    {
        var data = new TheoryData<ErrorCode>();
        foreach (var code in Enum.GetValues<ErrorCode>()) data.Add(code);
        return data;
    }

    public static TheoryData<ErrorCode, string, int, bool, UserAction> PublishedTaxonomy()
    {
        var data = new TheoryData<ErrorCode, string, int, bool, UserAction>();
        foreach (var row in Published) data.Add(row.Code, row.Wire, row.Status, row.Retriable, row.Action);
        return data;
    }

    /// <summary>
    /// The single highest-consequence rule in the platform. Three retries
    /// lock a real bank account, and an account lockout is a support
    /// incident no amount of good architecture makes up for. If this test
    /// ever fails, nothing else in the suite matters.
    /// </summary>
    [Fact]
    public void InvalidCredentials_is_never_retriable()
    {
        Assert.False(ErrorCatalog.IsRetriable(ErrorCode.InvalidCredentials));
        Assert.False(ErrorCatalog.Behaviour(ErrorCode.InvalidCredentials).Retriable);
        Assert.False(new ConnectorException(ErrorCode.InvalidCredentials).Retriable);
        Assert.False(ConnectorException.InvalidCredentials("wrong password").ToError().Retriable);
        Assert.Contains(ErrorCode.InvalidCredentials, ErrorCatalog.NeverRetry);
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Every_code_has_a_behaviour(ErrorCode code)
    {
        var behaviour = ErrorCatalog.Behaviour(code);

        // A code with no registered behaviour is a code the platform would
        // have to guess about at the exact moment guessing is worst.
        Assert.InRange(behaviour.HttpStatus, 400, 599);
        Assert.True(Enum.IsDefined(behaviour.UserAction));
        Assert.Equal(behaviour.HttpStatus, ErrorCatalog.HttpStatus(code));
        Assert.Equal(behaviour.Retriable, ErrorCatalog.IsRetriable(code));
        Assert.Equal(behaviour.UserAction, ErrorCatalog.ActionFor(code));
    }

    [Fact]
    public void An_unregistered_code_throws_rather_than_defaulting()
    {
        // Defaulting to "retriable" here is how a lockout gets shipped by
        // someone who only added an enum member.
        Assert.Throws<ArgumentOutOfRangeException>(() => ErrorCatalog.Behaviour((ErrorCode)9_999));
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Never_retry_and_retriable_false_are_the_same_set(ErrorCode code)
    {
        // Two lists that must agree, kept apart because one drives the queue
        // and the other drives the wire. Asserting the equivalence per code
        // is what stops them drifting.
        Assert.Equal(ErrorCatalog.NeverRetry.Contains(code), !ErrorCatalog.IsRetriable(code));
    }

    [Fact]
    public void The_never_retry_list_cannot_quietly_shrink()
    {
        var expected = new[]
        {
            ErrorCode.InvalidCredentials,
            ErrorCode.SessionExpired,
            ErrorCode.MfaFailed,
            ErrorCode.BlockedByProvider,
            ErrorCode.ProviderChanged,
            ErrorCode.UnsupportedResource,
            ErrorCode.InvalidRequest,
            ErrorCode.ConsentExpired,
            ErrorCode.ReconciliationFailed,
        };

        Assert.Equal(expected.Order().ToArray(), ErrorCatalog.NeverRetry.Order().ToArray());
    }

    [Theory]
    [MemberData(nameof(PublishedTaxonomy))]
    public void Matches_the_published_taxonomy(
        ErrorCode code, string wire, int status, bool retriable, UserAction action)
    {
        Assert.Equal(wire, ErrorCatalog.Wire(code));
        Assert.Equal(status, ErrorCatalog.HttpStatus(code));
        Assert.Equal(retriable, ErrorCatalog.IsRetriable(code));
        Assert.Equal(action, ErrorCatalog.ActionFor(code));
    }

    [Fact]
    public void The_published_taxonomy_covers_every_code()
    {
        // Otherwise a new code slips in with no published row and the
        // implementation drifts from the contract without a test failing.
        Assert.Equal(
            Enum.GetValues<ErrorCode>().Order().ToArray(),
            Published.Select(p => p.Code).Order().ToArray());
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Wire_form_is_snake_case(ErrorCode code)
    {
        Assert.Matches("^[a-z]+(_[a-z]+)*$", ErrorCatalog.Wire(code));
    }

    [Fact]
    public void Wire_forms_are_unique()
    {
        var wires = Enum.GetValues<ErrorCode>().Select(ErrorCatalog.Wire).ToList();

        // Two codes sharing a wire form makes the taxonomy lossy at exactly
        // the boundary a consumer branches on.
        Assert.Equal(wires.Count, wires.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Message_key_is_a_key_and_never_prose(ErrorCode code)
    {
        var key = ErrorCatalog.MessageKey(code);

        Assert.Equal("connect.error." + ErrorCatalog.Wire(code), key);
        Assert.DoesNotContain(" ", key, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_envelope_matches_the_spec()
    {
        var error = ConnectorException.Blocked("jumbo returned 403 from a datacenter range")
            .ToError(detailId: "err_9f2c", retryAfterSeconds: 3_600);

        Assert.Equal("blocked_by_provider", error.Code);
        Assert.False(error.Retriable);
        Assert.Equal(UserAction.Wait, error.UserAction);
        Assert.Equal("connect.error.blocked_by_provider", error.MessageKey);
        Assert.Equal("err_9f2c", error.DetailId);
        Assert.Equal(3_600, error.RetryAfterSeconds);

        // There is deliberately no free-text message field on the wire; the
        // operator-facing detail stays on this side of the boundary.
        Assert.Null(typeof(ConnectorError).GetProperty("Message"));
        Assert.Null(typeof(ConnectorError).GetProperty("Detail"));
    }

    [Fact]
    public void The_operator_detail_never_reaches_the_wire()
    {
        const string operatorDetail = "selector .challenge-number vanished on jumbo.com/login";
        var ex = ConnectorException.ProviderChanged(operatorDetail);

        Assert.Equal(operatorDetail, ex.Detail);
        Assert.Equal(operatorDetail, ex.Message);

        var wire = ex.ToError();
        var rendered = string.Join('|', wire.Code, wire.MessageKey, wire.DetailId ?? string.Empty);
        Assert.DoesNotContain(operatorDetail, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_with_no_detail_still_carries_its_wire_form_as_the_message()
    {
        Assert.Equal("session_expired", new ConnectorException(ErrorCode.SessionExpired).Message);
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Nothing_that_returns_401_is_retriable(ErrorCode code)
    {
        var behaviour = ErrorCatalog.Behaviour(code);

        // A 401 that retries is an account lockout in slow motion: the
        // credential is what the provider rejected, so repeating it counts.
        if (behaviour.HttpStatus == 401) Assert.False(behaviour.Retriable);
    }

    [Fact]
    public void Snake_case_never_emits_a_leading_or_doubled_separator()
    {
        Assert.Equal("internal", ErrorCatalog.Wire(ErrorCode.Internal));

        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var wire = ErrorCatalog.Wire(code);
            Assert.False(wire.StartsWith('_'), wire);
            Assert.DoesNotContain("__", wire, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_factory_helpers_carry_the_code_they_name()
    {
        Assert.Equal(ErrorCode.InvalidCredentials, ConnectorException.InvalidCredentials().Code);
        Assert.Equal(ErrorCode.SessionExpired, ConnectorException.SessionExpired().Code);
        Assert.Equal(ErrorCode.ProviderChanged, ConnectorException.ProviderChanged("x").Code);
        Assert.Equal(ErrorCode.BlockedByProvider, ConnectorException.Blocked().Code);
        Assert.Equal(ErrorCode.UnsupportedResource, ConnectorException.Unsupported("x").Code);
        Assert.Equal(ErrorCode.InvalidRequest, ConnectorException.InvalidRequest("x").Code);
    }
}
