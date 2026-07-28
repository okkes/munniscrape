using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoClient.Web.Relay;

/// <summary>
/// A sealed bundle in flight through the relay.
///
/// The invariant this demo exists to demonstrate is that a bundle enters the
/// relay's memory in a request body, is used, and leaves in the same response.
/// It is never written to a store, never cached, never logged in full. Stated
/// as a rule that would be one careless string interpolation away from being
/// false; stated as a type, the compiler carries it:
///
/// <list type="bullet">
///   <item><see cref="ToString"/> is the redacted form, so a bundle that
///   reaches a log reaches it as <c>sb_v1.k3…9f2ab1</c>.</item>
///   <item><see cref="Reveal"/> is the single greppable door to the
///   ciphertext, and only the outbound connector request opens it.</item>
/// </list>
///
/// The wire form is <c>sb_v1.&lt;kid&gt;.&lt;nonce&gt;.&lt;ciphertext&gt;.&lt;tag&gt;</c>;
/// the redaction keeps the kid and the last six characters, which is enough to
/// correlate two log lines about the same connection and useless to anyone
/// who reads them.
/// </summary>
[JsonConverter(typeof(BundleJsonConverter))]
public readonly struct Bundle : IEquatable<Bundle>
{
    private readonly string? _ciphertext;

    private Bundle(string? ciphertext) => _ciphertext = ciphertext;

    public static Bundle None => default;

    public bool IsPresent => !string.IsNullOrWhiteSpace(_ciphertext);

    public static Bundle Of(string? ciphertext) =>
        string.IsNullOrWhiteSpace(ciphertext) ? None : new Bundle(ciphertext);

    /// <summary>
    /// The ciphertext, for the one caller that needs it: the body of the
    /// outbound <c>sessions/resume</c> call. Anything else calling this is a
    /// bug worth finding, which is why it is a method and not a property.
    /// </summary>
    public string Reveal() =>
        _ciphertext ?? throw new InvalidOperationException("no bundle was supplied");

    public override string ToString()
    {
        if (_ciphertext is not { Length: > 0 } value) return "sb_none";

        var parts = value.Split('.');
        var kid = parts.Length > 1 ? parts[1] : "?";
        var tail = value.Length <= 6 ? value : value[^6..];

        return $"sb_v1.{kid}...{tail}";
    }

    public bool Equals(Bundle other) => string.Equals(_ciphertext, other._ciphertext, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Bundle other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_ciphertext ?? string.Empty);

    public static bool operator ==(Bundle left, Bundle right) => left.Equals(right);

    public static bool operator !=(Bundle left, Bundle right) => !left.Equals(right);
}

/// <summary>
/// Reads a bundle from a request body and writes it back out to a connector.
/// Serialisation reveals the ciphertext on purpose - the resume body is the
/// only place a <see cref="Bundle"/> is ever serialised, and it is the one
/// place the real value belongs.
/// </summary>
public sealed class BundleJsonConverter : JsonConverter<Bundle>
{
    public override Bundle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? Bundle.Of(reader.GetString()) : Bundle.None;

    public override void Write(Utf8JsonWriter writer, Bundle value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Reveal());
    }
}

/// <summary>
/// The runtime half of the same rule, for the paths a type cannot cover.
///
/// The relay has exactly one thing that outlives a request - the development
/// credentials file - and a bundle appearing in it would mean someone taught
/// the relay to hold a session. Failing loudly there is cheaper than
/// discovering later that "the relay never persists a bundle" had quietly
/// become untrue.
/// </summary>
public static class BundleGuard
{
    public const string Prefix = "sb_v1.";

    public static bool LooksLikeBundle(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static void RejectBundle(string where, string? value)
    {
        if (!LooksLikeBundle(value)) return;

        throw new InvalidOperationException(
            $"a sealed bundle reached '{where}'. The relay passes bundles through and stores none; " +
            "see Bundle in Relay/BundleGuard.cs");
    }
}
