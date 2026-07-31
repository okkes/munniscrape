using Connector.Kit.Challenges;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;

namespace Connector.Kit.Adapters;

/// <summary>
/// Everything a provider plugin implements, and the entire surface its
/// author sees.
///
/// Adapters never touch the database, never call the consuming app, never
/// decide retry policy, and never emit user-facing prose. Those are all
/// platform concerns precisely so that an adapter author cannot get them
/// wrong - most importantly the rule that a failed login is never retried.
/// </summary>
public interface IProviderAdapter
{
    /// <summary>The catalogue entry. Pure; called often; must not do I/O.</summary>
    ProviderManifest Describe();

    /// <summary>
    /// Authenticate. May raise challenges via
    /// <see cref="IJobContext.AskAsync"/> as many times as the provider
    /// demands. Returns the material to be sealed into the user's bundle.
    /// </summary>
    Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct);

    /// <summary>
    /// Read one resource. May also raise challenges - a T3 provider
    /// re-authenticates on every run.
    /// </summary>
    Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct);

    /// <summary>
    /// Best-effort upstream logout. Failure here is logged, never fatal: a
    /// user disconnecting must always succeed locally.
    /// </summary>
    Task LogoutAsync(IJobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The adapter's window onto the run. Deliberately narrow.
/// </summary>
public interface IJobContext
{
    string SessionId { get; }

    string JobId { get; }

    /// <summary>
    /// Login inputs. Secret-tainted: values for fields declared
    /// <c>secret</c> must never be logged or screenshotted. Empty for a
    /// fetch - a fetch has a session, not a password.
    /// </summary>
    IReadOnlyDictionary<string, string> Inputs { get; }

    /// <summary>Non-secret provider settings such as country and language.</summary>
    IReadOnlyDictionary<string, string> Config { get; }

    /// <summary>
    /// The session material from the user's bundle. Null on a first login.
    /// For an agent-custody provider this is only a pointer.
    /// </summary>
    SessionMaterial? Material { get; }

    /// <summary>
    /// An HTTP client with the politeness limiter already installed. Using
    /// anything else bypasses rate limiting and is a bug.
    /// </summary>
    HttpClient Http { get; }

    /// <summary>
    /// A browser, launched on first access. A T1 adapter never touches this,
    /// so no browser is ever started for it.
    /// </summary>
    IBrowserLease Browser { get; }

    /// <summary>Typed progress. The consumer renders and translates it.</summary>
    void Progress(JobStep step);

    /// <summary>
    /// Relay a question to the human who owns the account and wait for their
    /// answer. The single call that implements the whole challenge protocol.
    /// Throws when the challenge expires unanswered.
    /// </summary>
    Task<ChallengeAnswer> AskAsync(Challenge challenge, CancellationToken ct);

    /// <summary>
    /// Report a credential as submitted upstream. After this point a lost
    /// lease fails the job permanently instead of requeuing it, because
    /// retrying a login that may already have counted is how accounts get
    /// locked.
    /// </summary>
    void CredentialSubmitted();

    /// <summary>A directory that is deleted when the job ends, including on failure.</summary>
    string WorkDirectory { get; }

    /// <summary>
    /// True when a human can actually see and touch the browser this job is
    /// driving - a headed agent on hardware its owner is sitting at.
    ///
    /// Some challenges cannot be relayed. An hCaptcha or reCAPTCHA is an
    /// interactive widget, not a picture: it wants drags, tile clicks and a
    /// token minted by its own JavaScript, so a screenshot plus a text box
    /// cannot answer it however faithfully we photograph it. That leaves two
    /// honest paths, and this flag is how an adapter tells them apart - the
    /// owner solves it in the window already open in front of them, or we are
    /// <see cref="Errors.ErrorCode.BlockedByProvider"/> and say so.
    ///
    /// Defaults to false: a pooled agent in a datacenter has nobody to ask,
    /// and assuming otherwise would hang a job until its budget ran out -
    /// which is precisely the failure this was added to fix.
    /// </summary>
    bool Attended => false;
}

/// <summary>
/// A browser that is only launched if an adapter actually asks for one.
/// </summary>
public interface IBrowserLease : IAsyncDisposable
{
    /// <summary>
    /// The page, launched on first call. Restores a storage state from the
    /// session material when one is present, so a second run skips the full
    /// login and 2FA without any password having been stored.
    /// </summary>
    Task<Microsoft.Playwright.IPage> PageAsync(CancellationToken ct);

    /// <summary>
    /// The current cookies and local storage, for sealing into the bundle.
    /// This is the cheap 80% win of session reuse.
    /// </summary>
    Task<string> StorageStateAsync(CancellationToken ct);

    /// <summary>
    /// A redacted screenshot for a challenge. Refuses to capture while any
    /// secret-declared field holds content.
    /// </summary>
    Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct);

    /// <summary>True when a browser was actually started.</summary>
    bool Started { get; }
}

public sealed record LoginResult
{
    /// <summary>What gets sealed into the user's bundle.</summary>
    public required SessionMaterial Material { get; init; }

    /// <summary>Shown to the user so they can tell two connections apart.</summary>
    public ProviderAccount? Account { get; init; }

    /// <summary>What this session can reach, for cheap discovery later.</summary>
    public IReadOnlyList<ReachableAccount> Reachable { get; init; } = [];

    /// <summary>
    /// Overrides the manifest's TTL when the provider tells us something
    /// more specific. Being honest here matters: a manifest that claims 30
    /// days for a 24-hour session makes the consumer promise a month of
    /// silent syncing and then fail.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record ProviderAccount
{
    [System.Text.Json.Serialization.JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("external_id")]
    public string? ExternalId { get; init; }
}

public sealed record FetchResult
{
    public IReadOnlyList<Account> Accounts { get; init; } = [];

    public IReadOnlyList<Transaction> Transactions { get; init; } = [];

    public IReadOnlyList<Receipt> Receipts { get; init; } = [];

    /// <summary>
    /// Refreshed material when the provider rotated a token. Sealed into a
    /// new bundle the caller must persist.
    /// </summary>
    public SessionMaterial? RefreshedMaterial { get; init; }

    /// <summary>
    /// False when more remains beyond this pass. A first connect on a heavy
    /// account paginates rather than running for ten minutes.
    /// </summary>
    public bool Complete { get; init; } = true;

    /// <summary>
    /// Which upstream recipe answered, when a provider has more than one.
    /// The signal that makes the next breakage diagnosable.
    /// </summary>
    public string? Via { get; init; }

    /// <summary>
    /// The provider's own payload for a record, keyed by its external id.
    ///
    /// Populated only when <see cref="ResourceRequest.WantsRaw"/> asked for it.
    /// An adapter that has nothing meaningful to hand back - one that scraped
    /// a page, or built a record from three calls - leaves it empty rather than
    /// inventing a shape, and the caller gets a normalised record with no raw
    /// beside it, which is the honest answer.
    /// </summary>
    public IReadOnlyDictionary<string, string> Raw { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public int Count => Accounts.Count + Transactions.Count + Receipts.Count;

    public static FetchResult Empty { get; } = new();
}
