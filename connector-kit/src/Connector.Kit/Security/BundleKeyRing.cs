using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Connector.Kit.Security;

/// <summary>
/// Key rotation is a <c>kid</c> bump: new bundles seal with the new key,
/// existing ones keep validating under their own until they expire, then
/// die naturally. No mass re-login event, ever.
/// </summary>
public interface IBundleKeyRing
{
    /// <summary>The key new bundles are sealed with.</summary>
    (string Kid, byte[] Key) Current { get; }

    bool TryGet(string kid, out byte[] key);
}

public sealed class BundleKeyRing : IBundleKeyRing
{
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _currentKid;

    public BundleKeyRing(string currentKid, IReadOnlyDictionary<string, byte[]> keys)
    {
        if (string.IsNullOrWhiteSpace(currentKid)) throw new ArgumentException("kid required", nameof(currentKid));
        ArgumentNullException.ThrowIfNull(keys);

        foreach (var (kid, key) in keys)
        {
            if (key.Length != 32)
            {
                throw new ArgumentException($"key '{kid}' must be 32 bytes (AES-256), was {key.Length}", nameof(keys));
            }
        }

        if (!keys.ContainsKey(currentKid))
        {
            throw new ArgumentException($"current kid '{currentKid}' is not in the ring", nameof(currentKid));
        }

        _keys = new Dictionary<string, byte[]>(keys, StringComparer.Ordinal);
        _currentKid = currentKid;
    }

    public (string Kid, byte[] Key) Current => (_currentKid, _keys[_currentKid]);

    public bool TryGet(string kid, out byte[] key) => _keys.TryGetValue(kid, out key!);

    /// <summary>
    /// Builds a ring from configuration of the form
    /// <c>{ "CurrentKid": "k1", "Keys": { "k1": "&lt;base64&gt;" } }</c>.
    /// </summary>
    public static BundleKeyRing FromBase64(string currentKid, IReadOnlyDictionary<string, string> base64Keys)
    {
        var keys = base64Keys.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value), StringComparer.Ordinal);
        return new BundleKeyRing(currentKid, keys);
    }

    /// <summary>
    /// A throwaway ring for local development and tests. Never reachable in
    /// a deployed configuration - the host refuses to start without real
    /// keys unless it is explicitly in development.
    /// </summary>
    public static BundleKeyRing Ephemeral(string kid = "dev")
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [kid] = RandomNumberGenerator.GetBytes(32) };
        return new BundleKeyRing(kid, keys);
    }
}

/// <summary>
/// The credential material a bundle carries. Which fields are populated is
/// determined entirely by the provider's runtime tier.
/// </summary>
public sealed record SessionMaterial
{
    /// <summary>T1/T2: the bearer token, if the provider issues one.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    /// <summary>T1/T2: what keeps the session alive without a human.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("access_token_expires_at")]
    public DateTimeOffset? AccessTokenExpiresAt { get; init; }

    /// <summary>
    /// T2/T3: a Playwright storage state. Persisting this is the cheap 80%
    /// win - the second fetch skips a full login and 2FA without any
    /// password ever being stored.
    /// </summary>
    [JsonPropertyName("storage_state")]
    public string? StorageState { get; init; }

    /// <summary>
    /// A stable per-connection device identifier where a provider expects
    /// one. Rotating it every run is a fraud signal, so it is minted at
    /// connect time and carried for the life of the session.
    /// </summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }

    /// <summary>T4: the agent holding the real session. No secret travels.</summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    /// <summary>T4: the persistent browser profile on that agent.</summary>
    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; init; }

    /// <summary>Anything provider-specific and non-standard. Treated as opaque.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True when this material is only a pointer, holding no credential at all.</summary>
    [JsonIgnore]
    public bool IsAgentPointer => AgentId is not null && AccessToken is null && RefreshToken is null && StorageState is null;

    public static SessionMaterial ForAgent(string agentId, string profileId) =>
        new() { AgentId = agentId, ProfileId = profileId };
}
