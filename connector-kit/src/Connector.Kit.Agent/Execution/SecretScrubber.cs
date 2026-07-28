namespace Connector.Kit.Agent.Execution;

/// <summary>
/// Last-ditch removal of credential values from anything travelling upstream.
///
/// Adapters are told not to put a credential in an exception message, and
/// mostly they do not. But <c>detail</c> on a failed job is operator-facing
/// free text assembled from whatever went wrong, and a provider library that
/// echoes a request body into its exception message is a normal thing for a
/// provider library to do. This is the belt to that braces: it costs one
/// string scan per failed job and removes an entire class of accident.
/// </summary>
internal static class SecretScrubber
{
    private const string Mask = "***";

    /// <summary>
    /// Values shorter than this are skipped. A two-character PIN appears
    /// inside ordinary words, and masking every occurrence would destroy the
    /// diagnostic without protecting anything a four-character search would
    /// not already find.
    /// </summary>
    private const int MinimumLength = 4;

    private const int MaximumDetailLength = 2000;

    /// <summary>
    /// Removes every secret value from arbitrary text. Used for LOG output as
    /// well as the wire: an exception whose message carries a password is just
    /// as leaked in an operator's log aggregator as in a failure report.
    /// </summary>
    public static string Scrub(string text, IReadOnlyCollection<string> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;
        foreach (var secret in secrets)
        {
            if (secret.Length < MinimumLength) continue;
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>Scrubbed and bounded, for the <c>detail</c> field of a failure.</summary>
    public static string? Detail(string? detail, IReadOnlyCollection<string> secrets)
    {
        if (string.IsNullOrEmpty(detail)) return detail;

        var scrubbed = Scrub(detail, secrets);
        return scrubbed.Length > MaximumDetailLength ? scrubbed[..MaximumDetailLength] : scrubbed;
    }
}
