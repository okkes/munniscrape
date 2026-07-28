namespace DemoClient.Web.Relay;

/// <summary>
/// Where the connectors are, how to authenticate to them, and - the part that
/// matters - one salt per service.
///
/// Mirrors munni's <c>Connectors__{Service}__*</c> environment shape. An absent
/// connector section disables that service cleanly, so a stack can run with the
/// shop connector and without the bank one.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>Keyed by the service name that appears in <c>/api/{service}/…</c>: <c>shop</c> or <c>bank</c>.</summary>
    public Dictionary<string, ConnectorEndpointOptions> Connectors { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The start-up refusals.
    ///
    /// Two salts that are equal - or absent, which makes them equal - would let
    /// the two connectors line up the same person from their subjects alone.
    /// That is the one privacy property this whole topology is built to
    /// provide, so a configuration that silently gives it away must not boot.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Connectors.Count == 0)
        {
            problems.Add($"{SectionName}:Connectors lists no connector; the relay would have nothing to relay to");
        }

        var salts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (service, endpoint) in Connectors)
        {
            var prefix = $"{SectionName}:Connectors:{service}";

            if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out _))
            {
                problems.Add($"{prefix}:BaseUrl must be an absolute url");
            }

            if (string.IsNullOrWhiteSpace(endpoint.SubjectSalt))
            {
                problems.Add($"{prefix}:SubjectSalt is required - without it this service's subjects are derivable");
            }
            else if (salts.TryGetValue(endpoint.SubjectSalt, out var owner))
            {
                problems.Add(
                    $"{prefix}:SubjectSalt is the same value as {owner}'s; the two connectors could then " +
                    "correlate the same person from their subjects alone");
            }
            else
            {
                salts[endpoint.SubjectSalt] = service;
            }
        }

        return problems;
    }
}

/// <summary>One connector: its address, its credential, and its subject salt.</summary>
public sealed class ConnectorEndpointOptions
{
    /// <summary>Display name. The relay owns user-facing copy; the connector never emits any.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <c>store</c> or <c>bank</c>. Only a fallback for <c>/api/services</c>
    /// when the connector cannot be reached to state its own kind.
    /// </summary>
    public string Kind { get; set; } = "store";

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Sort key for <c>/api/services</c>, so the list does not reshuffle between runs.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Longer than the connector's own inline fetch window (25s by default) so
    /// the relay never abandons a call the connector was about to answer, and
    /// short enough that a dead connector fails fast instead of hanging a tab.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Development auth: the value sent in <c>X-Connector-Key</c>. Absent means
    /// no header at all, which is what the connectors accept on localhost.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// HMAC key for this service's subjects. A <b>different</b> value per
    /// service - see <see cref="SubjectMinter"/>.
    /// </summary>
    public string SubjectSalt { get; set; } = string.Empty;

    /// <summary>
    /// Set only when the consumer actually shows terms. The relay refuses to
    /// invent a consent record it never collected: consent is a legal claim
    /// long before it is a JSON field.
    /// </summary>
    public string? ConsentTermsVersion { get; set; }

    // ── Production placeholders ────────────────────────────────────────────
    // Not wired here, and deliberately so: a demo that shipped half an mTLS
    // handler would be read as a working one. In munni these carry the M2M
    // client credential and the client certificate, and ConnectorClient gains
    // a token cache plus a ConfigurePrimaryHttpMessageHandler that attaches
    // the cert. Everything else on this path stays identical.

    /// <summary>Production: the OIDC authority that issues the M2M token.</summary>
    public string? M2mAuthority { get; set; }

    /// <summary>Production: the audience the connector checks on that token.</summary>
    public string? M2mAudience { get; set; }

    public string? M2mClientId { get; set; }

    public string? M2mClientSecret { get; set; }

    /// <summary>Production: PKCS#12 holding the mTLS client certificate.</summary>
    public string? ClientCertificatePath { get; set; }

    public string? ClientCertificatePassword { get; set; }
}

/// <summary>
/// The stand-in for an authenticated munni user. Real munni takes the subject
/// seed from the signed-in principal; there is exactly one user here.
/// </summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Seed for every minted subject. Never sent to a connector.</summary>
    public string UserId { get; set; } = "demo-user";

    /// <summary>
    /// Development-only credential file for the prefill endpoint, resolved
    /// relative to the content root and then upwards. Never committed.
    /// JSONC, because the file is mostly comments and an editor that flags
    /// them as errors is an editor a developer stops reading.
    /// </summary>
    public string CredentialsFile { get; set; } = "credentials.local.jsonc";
}
