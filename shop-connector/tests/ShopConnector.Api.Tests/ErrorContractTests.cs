using System.Net;
using Connector.Kit.Hosting.Auth;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §5. Never a bare 500, never an HTML error page, never English.
///
/// Every assertion here goes through <see cref="ErrorEnvelope"/>, which checks
/// the whole shape rather than the status code: an error body that grew a
/// free-text <c>message</c> would be a contract change the consumer cannot see
/// coming, and it is exactly the kind of change that arrives by accident.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class ErrorContractTests(ShopApiFactory factory)
{
    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    [Fact]
    public async Task A_call_with_no_credential_is_401_and_still_an_envelope()
    {
        // Deliberately not the authorized client: nobody but the consuming
        // server may reach a connector at all.
        using var http = factory.CreateClient();

        using var response = await http.GetAsync("/v1/providers");
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_call_with_the_wrong_credential_is_401()
    {
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add(ConnectorAuth.DevelopmentHeader, "not-the-secret");

        using var response = await http.GetAsync("/v1/providers");
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Liveness_needs_no_credential()
    {
        // A probe that needs a credential is a probe that fails for the wrong reason.
        using var http = factory.CreateClient();

        using var response = await http.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.JsonAsync()).Text("status"));
    }

    [Fact]
    public async Task An_unknown_provider_is_unsupported_resource_rather_than_a_404()
    {
        using var http = factory.CreateAuthorizedClient();

        using var catalogue = await http.GetAsync("/v1/providers/no-such-store");
        await ErrorEnvelope.AssertAsync(catalogue, HttpStatusCode.BadRequest, "unsupported_resource");

        using var request = Wire.Post("/v1/no-such-store/login",
            new { Subject = Flows.NewSubject("unknown-provider"), Inputs = Credentials });

        using var login = await http.SendAsync(request);
        await ErrorEnvelope.AssertAsync(login, HttpStatusCode.BadRequest, "unsupported_resource");
    }

    [Fact]
    public async Task An_unknown_resource_on_a_known_provider_is_unsupported_resource()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("bad-resource"), Credentials);
        var ticket = await Flows.ResumeAsync(http, provider, connection);

        using var response = await Flows.FetchAsync(http, $"/v1/{provider}/loyalty-points", ticket);
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.BadRequest, "unsupported_resource");
    }

    [Fact]
    public async Task An_unknown_query_parameter_is_rejected_rather_than_ignored()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("bad-param"), Credentials);
        var ticket = await Flows.ResumeAsync(http, provider, connection);

        // A caller that misspells `since` and silently gets a full history has
        // not been told that anything went wrong.
        using var unknown = await Flows.FetchAsync(http, $"/v1/{provider}/receipts?since=2026-06-01&sicne=x", ticket);
        await ErrorEnvelope.AssertAsync(unknown, HttpStatusCode.BadRequest, "invalid_request");

        using var missing = await Flows.FetchAsync(http, $"/v1/{provider}/receipts", ticket);
        await ErrorEnvelope.AssertAsync(missing, HttpStatusCode.BadRequest, "invalid_request");

        using var malformed = await Flows.FetchAsync(http, $"/v1/{provider}/receipts?since=1st-june", ticket);
        await ErrorEnvelope.AssertAsync(malformed, HttpStatusCode.BadRequest, "invalid_request");

        using var notAnOption = await Flows.FetchAsync(
            http, $"/v1/{provider}/receipts?since=2026-06-01&include=everything", ticket);
        await ErrorEnvelope.AssertAsync(notAnOption, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task An_operator_only_parameter_may_not_be_set_by_a_caller()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("internal-param"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = RotatingStoreAdapter.Username,
                ["password"] = RotatingStoreAdapter.Password,
            });

        var ticket = await Flows.ResumeAsync(http, provider, connection);

        // The param is declared in the manifest so operators can read it, and
        // refused from a query string: a caller that could set one could reach
        // past the manifest, which is what the manifest exists to prevent.
        using var response = await Flows.FetchAsync(
            http,
            $"/v1/{provider}/receipts?since=2026-01-01&{RotatingStoreAdapter.InternalParam}=x",
            ticket);

        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_login_input_the_manifest_never_declared_is_refused()
    {
        using var http = factory.CreateAuthorizedClient();

        using var request = Wire.Post($"/v1/{MockStoreAdapters.Simple}/login", new
        {
            Subject = Flows.NewSubject("bad-input"),
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = "shopper",
                ["password"] = "hunter2",
                ["pin"] = "0000",
            },
        });

        using var response = await http.SendAsync(request);
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_rejected_credential_is_invalid_credentials_and_the_session_fails()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var subject = Flows.NewSubject("bad-password");

        using var request = Wire.Post($"/v1/{provider}/login", new
        {
            Subject = subject,
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = "shopper",
                // The one password the fixture rejects. It exists so the rule
                // that matters most has an offline test.
                ["password"] = "wrong",
            },
        });

        using var response = await http.SendAsync(request);
        var error = await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "invalid_credentials");

        // A compile-time constant in the kit, asserted here on the wire: this
        // code is never retriable, because three retries locks a real account.
        Assert.False(error.GetProperty("retriable").GetBoolean());
        Assert.Equal("reauth", error.Text("user_action"));

        // The rule, checked where it actually lives: one attempt, and the inputs
        // are gone. Nothing in the platform may submit that password again.
        var job = Db.JobForSubject(factory, subject);
        Assert.Equal(1, job.Attempts);
        Assert.True(job.CredentialSubmitted);
        Assert.Null(job.InputsJson);
    }

    [Fact]
    public async Task An_unparseable_device_class_is_rejected_rather_than_guessed()
    {
        using var http = factory.CreateAuthorizedClient();

        using var request = Wire.Post($"/v1/{MockStoreAdapters.Simple}/login",
            new { Subject = Flows.NewSubject("bad-device"), Inputs = Credentials });

        request.AddHeader("X-Device-Class", "sideways");

        using var response = await http.SendAsync(request);
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }
}
