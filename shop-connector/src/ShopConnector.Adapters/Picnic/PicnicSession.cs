using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Security;

namespace ShopConnector.Adapters.Picnic;

/// <summary>
/// Country and language, resolved once per job.
///
/// They arrive in the bundle's config block; the copy sealed into the session
/// material is the fallback for a caller that resumed without one. Picnic puts
/// the country in the HOST, so getting it wrong is not a cosmetic error - it
/// is a request to a different deployment.
/// </summary>
internal sealed record PicnicSettings
{
    public required string Country { get; init; }

    public required string Language { get; init; }

    /// <summary>
    /// Null on a fetch whose bundle predates device ids. Never minted outside a
    /// login: a device identity that changes every run is a fraud signal, and
    /// omitting the header is strictly better than rotating it.
    /// </summary>
    public string? DeviceId { get; init; }

    public static PicnicSettings From(
        IReadOnlyDictionary<string, string> config, SessionMaterial? material, PicnicOptions options)
    {
        var country = Pick(config, material, "country");

        if (!PicnicManifest.Countries.Contains(country, StringComparer.Ordinal))
        {
            throw ConnectorException.InvalidRequest(
                $"picnic: country '{country}' is not one of the supported values");
        }

        if (!options.LanguageByCountry.TryGetValue(country, out var language))
        {
            throw ConnectorException.InvalidRequest($"picnic: no Accept-Language configured for country '{country}'");
        }

        return new PicnicSettings
        {
            Country = country,
            Language = language,
            DeviceId = material?.DeviceId,
        };
    }

    private static string Pick(
        IReadOnlyDictionary<string, string> config, SessionMaterial? material, string key)
    {
        if (config.TryGetValue(key, out var fromConfig) && !string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig.Trim().ToUpperInvariant();
        }

        if (material?.Extra.TryGetValue(key, out var fromMaterial) == true && !string.IsNullOrWhiteSpace(fromMaterial))
        {
            return fromMaterial.Trim().ToUpperInvariant();
        }

        throw ConnectorException.InvalidRequest($"picnic: config '{key}' is required");
    }
}

/// <summary>
/// Picnic's credential, and the one thing about it that is easy to lose.
///
/// There is no OAuth here and no refresh grant. A single opaque token travels
/// in the <c>x-picnic-auth</c> REQUEST header, and every response may carry a
/// re-issued one in the <c>x-picnic-auth</c> RESPONSE header. A client that
/// only ever reads the token it was given keeps presenting an older and older
/// one until the day it stops being accepted - so the newest is adopted on
/// every response and handed back to be sealed into a new bundle.
/// </summary>
internal sealed class PicnicSession
{
    public const string AuthHeader = "x-picnic-auth";

    private readonly string _providerId;

    public PicnicSession(SessionMaterial? material, string providerId)
    {
        _providerId = providerId;

        Token = material?.AccessToken is { Length: > 0 } stored
            ? stored
            : throw ConnectorException.SessionExpired(
                $"{providerId}: the session material carries no {AuthHeader} token");
    }

    public string Token { get; private set; }

    /// <summary>True once the provider re-issued a token the caller must persist.</summary>
    public bool Rotated { get; private set; }

    /// <summary>
    /// The token a response carried, if it carried one. Static because the
    /// login has no session yet and reads the very same header.
    /// </summary>
    public static string? TokenOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues(AuthHeader, out var values)) return null;

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    /// <summary>Adopts a re-issued token. Called on every response, success or not.</summary>
    public void Observe(HttpResponseMessage response)
    {
        if (TokenOf(response) is not { } fresh) return;
        if (string.Equals(fresh, Token, StringComparison.Ordinal)) return;

        Token = fresh;
        Rotated = true;
    }

    /// <summary>
    /// The material to seal into a new bundle, or null when nothing rotated and
    /// the caller's stored bundle is still current.
    /// </summary>
    public SessionMaterial? RefreshedMaterial(SessionMaterial? original) => Rotated
        ? (original ?? new SessionMaterial()) with { AccessToken = Token }
        : null;

    public ConnectorException Expired(string what) =>
        ConnectorException.SessionExpired($"{_providerId}: {what} rejected the {AuthHeader} token");
}

/// <summary>
/// The two values Picnic expects a client to compute for itself.
/// </summary>
internal static class PicnicCredential
{
    /// <summary>
    /// Picnic's login takes <c>secret</c> = the MD5 of the password, hex,
    /// lower-case - confirmed from <c>lib/domains/auth/service.js</c>:
    /// <c>createHash("md5").update(password, "utf8").digest("hex")</c>.
    ///
    /// MD5 is not a security decision this adapter gets to make. It is the wire
    /// format Picnic's own API compares against, the transport underneath is
    /// TLS, and the digest is a password-equivalent - so it is handled with
    /// exactly the care the plaintext gets and never logged, never put in an
    /// error message, and never written to the work directory.
    /// </summary>
#pragma warning disable CA5351 // the provider defines the wire format; see the remarks above
    public static string Secret(string password) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5351

    /// <summary>
    /// A device identifier of the shape the reference uses - 16 upper-case hex
    /// characters. Minted once per connection and carried in the session
    /// material, never taken from the reference's hard-coded literal: that
    /// would give every user of this connector one shared device identity,
    /// which is both a fraud signal and a lie about who is calling.
    /// </summary>
    public static string DeviceId(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
}
