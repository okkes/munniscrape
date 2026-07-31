using Connector.Kit.Manifests;
using Connector.Kit.Normalization;

namespace Connector.Kit.Tests;

/// <summary>
/// Minimal valid fixtures, built so a test can express exactly one defect
/// with a single <c>with</c>. A test that hand-rolls a whole manifest to
/// check one rule ends up asserting the builder, not the rule.
/// </summary>
internal static class Make
{
    public const string SessionId = "ses_0123456789abcdef0123456789abcdef";
    public const string AccountId = "acc_0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The simplest manifest that passes every rule: T1, inline, client
    /// custody, attended, one password step, one resource.
    /// </summary>
    public static ProviderManifest Manifest() => new()
    {
        Id = "mock-provider",
        Name = "Mock Provider",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,
        UnattendedFetch = false,
        SecretCustody = SecretCustody.Client,
        Auth = Auth(),
        Resources = [Resource()],
    };

    public static AuthSpec Auth() => new()
    {
        Flow = AuthFlow.Password,
        Steps =
        [
            new AuthStep
            {
                Id = "credentials",
                Fields =
                [
                    new FieldSpec { Key = "username", Type = FieldType.Text, LabelKey = "connect.field.username" },
                    new FieldSpec
                    {
                        Key = "password",
                        Type = FieldType.Password,
                        Secret = true,
                        LabelKey = "connect.field.password",
                    },
                ],
            },
        ],
        Session = new SessionSpec { TtlSeconds = 2_592_000, Refreshable = false },
    };

    public static ResourceSpec Resource() => new()
    {
        Id = "receipts",
        Returns = ResourceShape.Receipt,
        Params =
        [
            new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
            new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
        ],
    };

    /// <summary>Replaces the single auth step's fields, leaving the rest intact.</summary>
    public static ProviderManifest WithFields(this ProviderManifest manifest, params FieldSpec[] fields) =>
        manifest with
        {
            Auth = manifest.Auth with
            {
                Steps = [manifest.Auth.Steps[0] with { Fields = fields }],
            },
        };

    public static ProviderManifest WithSession(this ProviderManifest manifest, SessionSpec session) =>
        manifest with { Auth = manifest.Auth with { Session = session } };

    public static Transaction Tx(string externalId, long minorUnits, long? resultingBalance = null) => new()
    {
        Id = Ids.ForRecord(Ids.Transaction, SessionId, externalId),
        ExternalId = externalId,
        AccountId = AccountId,
        BookedAt = new DateOnly(2026, 7, 19),
        Amount = Money.Eur(minorUnits),
        ResultingBalance = resultingBalance is { } balance ? Money.Eur(balance) : null,
    };

    public static Receipt Rcp(long totalMinorUnits, params ReceiptItem[] items) => new()
    {
        Id = Ids.ForRecord(Ids.Receipt, SessionId, "r-1"),
        ExternalId = "r-1",
        Merchant = new Merchant { Id = "jumbo", Name = "Jumbo" },
        PurchasedAt = new DateTimeOffset(2026, 7, 19, 17, 42, 0, TimeSpan.FromHours(2)),
        Total = Money.Eur(totalMinorUnits),
        Items = items,
    };

    public static ReceiptItem Item(string name, long totalMinorUnits, long? discountMinorUnits = null) => new()
    {
        Name = name,
        Quantity = 1m,
        Total = Money.Eur(totalMinorUnits),
        Discount = discountMinorUnits is { } discount
            ? new ReceiptDiscount { Amount = Money.Eur(discount) }
            : null,
    };

    public static Account Acc(string externalId, AccountType type = AccountType.Savings, long? balance = null) =>
        new()
        {
            Id = Ids.ForRecord(Ids.Account, SessionId, externalId),
            ExternalId = externalId,
            Type = type,
            DisplayName = "Oranje Spaarrekening",
            Currency = "EUR",
            Balance = balance is { } value
                ? new Balance { Amount = Money.Eur(value), AsOf = TestTime.Anchor }
                : null,
        };
}
