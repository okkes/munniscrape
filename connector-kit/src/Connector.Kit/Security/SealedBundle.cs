using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connector.Kit.Errors;

namespace Connector.Kit.Security;

/// <summary>
/// The mechanism that lets a user's own device hold their credentials while
/// the connector stays the only party that can read them.
///
/// Wire form: <c>sb_v1.{kid}.{nonce}.{ciphertext}.{tag}</c>, all base64url.
/// AES-256-GCM, with the associated data binding the bundle to a provider,
/// a subject and a manifest version - so a blob stolen from one device
/// cannot be replayed for another user, and a blob minted before a breaking
/// adapter change is rejected rather than misinterpreted.
/// </summary>
public sealed class SealedBundleCodec
{
    private const string Prefix = "sb_v1";

    /// <summary>
    /// A credential bundle's own prefix.
    ///
    /// Distinct so the two kinds can never be confused for one another. They
    /// share a key, an algorithm and their associated data, so without this a
    /// credential bundle handed to a route expecting a session bundle would
    /// decrypt perfectly and then fail somewhere further in, deserialising into
    /// a shape it is not. The prefix check is the first thing
    /// <see cref="Open"/> does, so the wrong kind is refused as malformed -
    /// which surfaces as the same session_expired everything else here does.
    /// </summary>
    private const string CredentialPrefix = "cb_v1";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly IBundleKeyRing _keys;
    private readonly TimeProvider _time;

    public SealedBundleCodec(IBundleKeyRing keys, TimeProvider? time = null)
    {
        _keys = keys;
        _time = time ?? TimeProvider.System;
    }

    public string Seal(BundlePayload payload, BundleBinding binding) =>
        Seal(payload, binding, Prefix);

    /// <summary>
    /// Seals what the human typed, for their own device to hold.
    ///
    /// The same mechanism as a session bundle and for the same reason: the
    /// connector stays the only party that can read it, and the AAD binds it to
    /// one provider, one subject and one manifest version - so a blob lifted
    /// from one device is useless for anybody else.
    /// </summary>
    public string SealCredentials(CredentialPayload payload, BundleBinding binding) =>
        Seal(payload, binding, CredentialPrefix);

    /// <summary>As <see cref="Open"/>, for a credential bundle.</summary>
    public CredentialPayload OpenCredentials(string bundle, BundleBinding binding) =>
        Open<CredentialPayload>(bundle, binding, CredentialPrefix);

    private string Seal<T>(T payload, BundleBinding binding, string prefix)
        where T : ISealedPayload
    {
        ArgumentNullException.ThrowIfNull(payload);

        var (kid, key) = _keys.Current;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, BundleJson.Options);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(binding));
        CryptographicOperations.ZeroMemory(plaintext);

        return string.Join('.', prefix, kid, B64(nonce), B64(ciphertext), B64(tag));
    }

    /// <summary>
    /// Rejection rules, in order: unknown kid, malformed, AAD mismatch
    /// (wrong subject, wrong provider, stale manifest version), then expiry.
    /// All of them surface as the same <see cref="ErrorCode.SessionExpired"/>
    /// so a probing caller learns nothing from the distinction.
    /// </summary>
    public BundlePayload Open(string bundle, BundleBinding binding) =>
        Open<BundlePayload>(bundle, binding, Prefix);

    private T Open<T>(string bundle, BundleBinding binding, string prefix)
        where T : ISealedPayload
    {
        if (string.IsNullOrWhiteSpace(bundle)) throw ConnectorException.SessionExpired("empty bundle");

        var parts = bundle.Split('.');
        if (parts.Length != 5 || parts[0] != prefix) throw ConnectorException.SessionExpired("malformed bundle");

        if (!_keys.TryGet(parts[1], out var key)) throw ConnectorException.SessionExpired("unknown key id");

        byte[] nonce, ciphertext, tag;
        try
        {
            nonce = UnB64(parts[2]);
            ciphertext = UnB64(parts[3]);
            tag = UnB64(parts[4]);
        }
        catch (FormatException)
        {
            throw ConnectorException.SessionExpired("malformed bundle encoding");
        }

        if (nonce.Length != NonceBytes || tag.Length != TagBytes)
        {
            throw ConnectorException.SessionExpired("malformed bundle envelope");
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(binding));
        }
        catch (CryptographicException)
        {
            // Wrong subject, wrong provider, stale manifest version, or a
            // tampered blob - all indistinguishable, all the same outcome.
            throw ConnectorException.SessionExpired("bundle failed authentication");
        }

        T payload;
        try
        {
            payload = JsonSerializer.Deserialize<T>(plaintext, BundleJson.Options)
                      ?? throw ConnectorException.SessionExpired("empty bundle payload");
        }
        catch (JsonException)
        {
            // Authenticated but not the shape this route wanted. The prefix
            // check above already refuses the other kind, so reaching here
            // means a payload minted by something that is not this codec.
            throw ConnectorException.SessionExpired("bundle payload is not readable");
        }

        CryptographicOperations.ZeroMemory(plaintext);

        if (payload.ExpiresAt <= _time.GetUtcNow()) throw ConnectorException.SessionExpired("bundle expired");

        return payload;
    }

    /// <summary>
    /// Binding the ciphertext to who it is for. Any mismatch fails the AEAD
    /// check, which is what makes a stolen blob useless on its own.
    /// </summary>
    private static byte[] AssociatedData(BundleBinding b) =>
        Encoding.UTF8.GetBytes($"{b.ProviderId}|{b.Subject}|{b.ManifestVersion}");

    private static string B64(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    private static byte[] UnB64(string value) => Base64Url.DecodeFromChars(value);
}

/// <summary>Who a bundle is for. Every field is authenticated, none is secret.</summary>
public readonly record struct BundleBinding(string ProviderId, string Subject, int ManifestVersion);

/// <summary>
/// What every sealed payload must state: when it stops being usable. Checked
/// after the AEAD, so an expired blob is refused rather than read.
/// </summary>
public interface ISealedPayload
{
    DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// What the human typed, sealed for their own device to hold.
///
/// It exists for one shape of provider: one whose session cannot be refreshed,
/// so a fresh username and password is needed again in a day - and where asking
/// the human daily is the alternative. The connector keeps no copy; this is the
/// same custody argument as a session bundle, applied to the thing that
/// produces sessions.
///
/// Shorter-lived than a session bundle on purpose. A password is the credential
/// that does not rotate, cannot be revoked from here, and opens everything -
/// so the window in which a stolen blob is useful has to be the smaller one.
/// </summary>
public sealed record CredentialPayload : ISealedPayload
{
    public int V { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    public required string Provider { get; init; }

    [JsonPropertyName("issued_at")]
    public required DateTimeOffset IssuedAt { get; init; }

    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Exactly what was posted to the login, and nothing added. It is fed back
    /// through the manifest's own input validator on redemption, so a blob
    /// cannot smuggle a key the provider never declared.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Inputs { get; init; }
}

/// <summary>
/// The plaintext inside a bundle. For an <c>agent</c>-custody provider this
/// carries no secret at all - only a pointer to the agent and profile that
/// hold the real session.
/// </summary>
public sealed record BundlePayload : ISealedPayload
{
    public int V { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    public required string Provider { get; init; }

    [JsonPropertyName("issued_at")]
    public required DateTimeOffset IssuedAt { get; init; }

    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The credential material, shaped by the provider's runtime tier:
    /// tokens for T1/T2, a browser storage state for T2/T3, or nothing but
    /// an agent pointer for T4.
    /// </summary>
    public required SessionMaterial Material { get; init; }

    /// <summary>
    /// Non-secret provider settings (country, language) so a later fetch
    /// need not resend them.
    /// </summary>
    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What this session can reach, for cheap discovery without a provider round trip.</summary>
    public IReadOnlyList<ReachableAccount> Accounts { get; init; } = [];
}

public sealed record ReachableAccount
{
    [JsonPropertyName("external_id")]
    public required string ExternalId { get; init; }

    public required string Type { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}

internal static class BundleJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
