using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;

namespace BankConnector.Adapters.MockBank;

/// <summary>
/// The offline backbone of the bank connector.
///
/// These providers exist so that the control plane, the agent protocol, the
/// challenge relay, SSE, BYO routing and the operator alert path can all be
/// exercised end to end before a single real bank is implemented - and so
/// that a demo user sees a complete bank connection flow without a byte
/// leaving the process.
///
/// The rules that make them useful are all negative ones. They open no
/// socket. They never touch <see cref="IJobContext.Browser"/>, even where
/// the manifest declares a browser tier, because the tier is what routing
/// and agent-class logic are supposed to see, not an instruction to start
/// Chromium. And their data never moves, so a content hash asserted today
/// still holds next year.
/// </summary>
public abstract class MockBankAdapter : IProviderAdapter
{
    protected MockBankAdapter(TimeProvider? time = null) => Time = time ?? TimeProvider.System;

    protected TimeProvider Time { get; }

    /// <summary>The fixture set this provider serves.</summary>
    protected virtual MockBankLedger Ledger => MockBankLedger.Consistent;

    public abstract ProviderManifest Describe();

    public abstract Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct);

    public virtual Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return Task.FromResult(Serve(ctx, request));
    }

    /// <summary>
    /// A mock has no upstream to log out of. Overriding to do nothing is
    /// still the correct behaviour rather than an omission: a user
    /// disconnecting must always succeed locally.
    /// </summary>
    public virtual Task LogoutAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Progress(JobStep.LoggingOut);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The whole read path, shared by every provider in the fleet: resolve
    /// the window, resolve which accounts the caller asked for, page, verify
    /// the balance chain, emit.
    /// </summary>
    protected FetchResult Serve(IJobContext ctx, ResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        var manifest = Describe();
        var accounts = Ledger.ToAccounts(ctx.SessionId);

        ctx.Progress(JobStep.Parsing);

        switch (request.ResourceId)
        {
            case BankResources.Accounts:
                ctx.Progress(JobStep.Normalizing);
                return BankEmission.Verified(manifest.Id, BankAccountFilter.Select(accounts, request), [], via: "fixture");

            case BankResources.Transactions:
            {
                var selected = BankAccountFilter.Select(accounts, request);
                var window = BankWindow.Resolve(request, manifest, DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime));

                ctx.Progress(JobStep.Normalizing);
                var (page, complete) = BankEmission.Page(
                    Ledger.ToTransactions(ctx.SessionId, selected, window),
                    manifest.Resource(BankResources.Transactions)?.MaxRecordsPerFetch ?? 200);

                ctx.Progress(JobStep.Finalizing);
                return BankEmission.Verified(manifest.Id, selected, page, complete, via: "fixture");
            }

            default:
                throw ConnectorException.Unsupported($"{manifest.Id} has no resource '{request.ResourceId}'");
        }
    }

    protected static string RequireInput(IJobContext ctx, string key)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"login input '{key}' is required");
    }

    protected LoginResult Connected(IJobContext ctx, SessionMaterial material, int ttlSeconds)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return new LoginResult
        {
            Material = material,
            Account = new ProviderAccount
            {
                DisplayName = MockBankCredentials.AccountLabel,
                ExternalId = MockBankLedger.CurrentIban,
            },
            Reachable = Ledger.Reachable,
            // Stating the real expiry rather than letting the manifest TTL
            // stand: a manifest that claims thirty days for a one-hour
            // session makes the consumer promise a month of silent syncing
            // and then fail.
            ExpiresAt = Time.GetUtcNow().AddSeconds(ttlSeconds),
        };
    }
}

/// <summary>
/// The fixed inputs the fleet recognises, exposed so tests and the demo
/// seed do not have to guess them.
/// </summary>
public static class MockBankCredentials
{
    public const string Username = "demo";

    public const string Password = "demo-password";

    /// <summary>
    /// Any password is accepted except this one, which fails with
    /// <see cref="ErrorCode.InvalidCredentials"/> - the code that must never
    /// be retried by anything, anywhere.
    /// </summary>
    public const string RejectedPassword = "invalid";

    /// <summary>The number a digipass-style provider displays for the human to key in.</summary>
    public const string DisplayedCode = "84213906";

    /// <summary>
    /// Not a real Playwright storage state, but the same shape, so a control
    /// plane that seals and reopens it exercises the real path.
    /// </summary>
    public const string StorageState = """{"cookies":[{"name":"mock-bank-session","value":"fixture","domain":"mock.invalid","path":"/"}],"origins":[]}""";

    /// <summary>
    /// Shown so a user can tell two connections apart. Not a real address:
    /// <c>.invalid</c> is reserved by RFC 2606 and can never be routed.
    /// </summary>
    public const string AccountLabel = "Mock Bank - demo@mock.invalid";
}
