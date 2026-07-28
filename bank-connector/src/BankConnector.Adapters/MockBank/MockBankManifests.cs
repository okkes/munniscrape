using Connector.Kit.Manifests;

namespace BankConnector.Adapters.MockBank;

/// <summary>
/// The manifest pieces every mock provider shares.
///
/// The fleet differs only in runtime tier, custody and challenge shape -
/// which is the point of it - so everything else lives here and a new mock
/// is a handful of lines rather than another eighty-line manifest to keep
/// in sync.
/// </summary>
internal static class MockBankManifests
{
    public const string LogoRef = "mock-bank";

    public static readonly FieldSpec Username = new()
    {
        Key = "username",
        Type = FieldType.Text,
        Secret = false,
        LabelKey = "connect.field.username",
        Pattern = "^.{3,64}$",
        Autofill = "username",
    };

    public static readonly FieldSpec Password = new()
    {
        Key = "password",
        Type = FieldType.Password,
        Secret = true,
        LabelKey = "connect.field.password",
        Pattern = "^.{4,128}$",
        Autofill = "current-password",
    };

    public static readonly AuthStep Credentials = new()
    {
        Id = "credentials",
        LabelKey = "connect.mock_bank.step.credentials",
        Fields = [Username, Password],
    };

    /// <summary>
    /// A pooled agent on a residential Dutch line. Bank fraud systems weigh
    /// IP reputation heavily and a datacenter range is a fraud signal before
    /// credentials are even considered - so even the mocks declare it, or
    /// routing is never exercised the way production will use it.
    /// </summary>
    public static readonly AgentRequirement ResidentialPooled = new()
    {
        Required = true,
        Class = AgentClass.Pooled,
        Egress = new EgressRequirement { Country = "NL", Kind = "residential" },
    };

    public static IReadOnlyList<ResourceSpec> Resources(int typicalDurationSeconds = 10) =>
    [
        BankResources.AccountsSpec(),
        BankResources.TransactionsSpec(
            maxHistoryDays: MockBankLedger.HistoryDays,
            typicalDurationSeconds: typicalDurationSeconds),
    ];

    /// <summary>
    /// Every mock account set includes a credit card, so every mock manifest
    /// declares the settlement lag. Fourteen days is the safe default for
    /// Dutch card payments: a purchase can surface two weeks late still
    /// dated to the day it was made, and a <c>since</c> that is not widened
    /// loses it permanently and invisibly.
    /// </summary>
    public static ProviderLimits Limits() => BankLimits.ForBank(
        minIntervalSeconds: BankLimits.MockMinIntervalSeconds,
        maxHistoryDays: MockBankLedger.HistoryDays,
        settlementLagDays: BankLimits.DutchCardSettlementLagDays);
}
