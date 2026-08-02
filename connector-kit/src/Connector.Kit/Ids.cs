using System.Security.Cryptography;

namespace Connector.Kit;

/// <summary>
/// Opaque, prefixed identifiers.
///
/// Prefixes are not decoration: they make it impossible to pass a job id
/// where a session id belongs and survive review, and they make a log line
/// self-describing. The random part is 128 bits from a CSPRNG, so an id is
/// never guessable - several endpoints take an id as their only lookup key.
/// </summary>
public static class Ids
{
    public const string Session = "ses";
    public const string Job = "job";
    public const string Challenge = "chl";
    public const string Agent = "agt";
    public const string Profile = "prf";
    public const string Ticket = "tkt";
    public const string Account = "acc";
    public const string Transaction = "txn";
    public const string Receipt = "rcp";

    /// <summary>One credit as a registry states it.</summary>
    public const string CreditRegistration = "crd";
    public const string Cursor = "cur";
    public const string Error = "err";
    public const string Event = "evt";

    public static string New(string prefix) =>
        $"{prefix}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";

    public static bool Is(string? id, string prefix) =>
        id is not null && id.StartsWith(prefix + "_", StringComparison.Ordinal);

    /// <summary>
    /// Deterministic id for an emitted record, so re-running a fetch
    /// produces the same ids for the same upstream data. Together with
    /// content hashing this is what makes retries free for the caller.
    /// </summary>
    public static string ForRecord(string prefix, string sessionId, string externalId)
    {
        var digest = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{prefix}|{sessionId}|{externalId}"));
        return $"{prefix}_{Convert.ToHexStringLower(digest.AsSpan(0, 16))}";
    }
}
