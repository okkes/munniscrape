using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;

namespace ShopConnector.Api.Tests.Infrastructure;

/// <summary>
/// A T1 store shaped like Jumbo: a password login whose session cannot be
/// refreshed, so the same human would be asked again tomorrow.
///
/// It exists because that shape is exactly the one a credential store is for
/// and no offline provider has it. Jumbo itself is browser-tier and needs a
/// pooled residential agent and a real account, so the store's whole round trip
/// - sealed on success, delivered once, offered back instead of a password -
/// would otherwise be asserted by nothing.
///
/// The login refuses a wrong password, which is what makes the redemption test
/// mean something: a bundle that came back with the password missing or mangled
/// fails the login rather than quietly succeeding.
/// </summary>
internal sealed class DailyLoginAdapter : IProviderAdapter
{
    public const string ProviderId = "test-daily-login";

    public const string Username = "daily-user";
    public const string Password = "daily-password";

    private static readonly ProviderManifest Contract = Build();

    public ProviderManifest Describe() => Contract;

    public Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var username = Required(ctx, "username");
        var password = Required(ctx, "password");

        ctx.Progress(JobStep.Authenticating);
        ctx.CredentialSubmitted();

        if (!string.Equals(password, Password, StringComparison.Ordinal))
        {
            throw ConnectorException.InvalidCredentials($"{ProviderId}: that is not the password");
        }

        return Task.FromResult(new LoginResult
        {
            Material = new SessionMaterial { AccessToken = $"daily-{ctx.SessionId}" },

            // Echoes the username back, so a test can prove WHICH credential
            // reached the adapter rather than merely that one did.
            Account = new ProviderAccount { DisplayName = "Daily Login Store", ExternalId = username },
        });
    }

    public Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(request.ResourceId, "receipts", StringComparison.Ordinal)
            ? Task.FromResult(FetchResult.Empty)
            : throw ConnectorException.Unsupported($"{ProviderId}: no resource '{request.ResourceId}'");
    }

    private static string Required(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw ConnectorException.InvalidRequest($"{ProviderId}: '{key}' is required");

    private static ProviderManifest Build() => new()
    {
        Id = ProviderId,
        Name = "Daily Login Store",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,
        UnattendedFetch = false,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,

        // The whole reason this adapter exists.
        OffersCredentialStore = true,

        Auth = new AuthSpec
        {
            Flow = AuthFlow.Password,
            Steps =
            [
                new AuthStep
                {
                    Id = "credentials",
                    LabelKey = "connect.step.credentials",
                    Fields =
                    [
                        new FieldSpec { Key = "username", Type = FieldType.Text, LabelKey = "connect.field.email" },
                        new FieldSpec
                        {
                            Key = "password",
                            Type = FieldType.Password,
                            LabelKey = "connect.field.password",
                            Secret = true,
                        },
                    ],
                },
            ],
            // Not refreshable: the reason a stored credential is offerable at
            // all, and the rule the validator enforces.
            Session = new SessionSpec { TtlSeconds = 86_400, Refreshable = false },
            Reauth = new ReauthSpec { Cheap = false, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = "receipts",
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                ],
                TypicalDurationSeconds = 1,
            },
        ],
        NotesKey = "connect.mock.notes",
    };
}
