using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DemoClient.Web.Relay;

/// <summary>
/// One human, one pseudonym per service, and no way to line the two up.
///
/// <code>
/// subject = "u_" + base64url(HMACSHA256(key: SubjectSalt_&lt;service&gt;, msg: userId))[0..21]
/// </code>
///
/// The salt differs per service on purpose. The shop connector's <c>u_…</c> and
/// the bank connector's <c>u_…</c> for the same person share no derivable
/// relationship, so two connectors comparing notes - or two breach dumps
/// landing on the same desk - learn nothing about who is who. That property is
/// what lets a consumer hand a durable identifier to neither of them, and it
/// costs one HMAC per service per process.
///
/// The consumer keeps the mapping. A connector never sees a real user id, an
/// email, or anything that outlives the relay's configuration.
/// </summary>
public sealed class SubjectMinter
{
    private readonly RelayOptions _relay;
    private readonly DemoOptions _demo;

    // Minted once and kept: the input never changes within a process, and the
    // HMAC is not the interesting part.
    private readonly ConcurrentDictionary<string, string> _minted =
        new(StringComparer.OrdinalIgnoreCase);

    public SubjectMinter(RelayOptions relay, DemoOptions demo)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(demo);

        _relay = relay;
        _demo = demo;
    }

    /// <summary>
    /// The subject this relay presents to <paramref name="service"/>. Throws
    /// for an unconfigured service rather than falling back to a shared salt,
    /// which would quietly undo the whole point.
    /// </summary>
    public string For(string service) => _minted.GetOrAdd(service, key =>
    {
        if (!_relay.Connectors.TryGetValue(key, out var endpoint))
        {
            throw new InvalidOperationException($"no connector configured for service '{key}'");
        }

        return Mint(endpoint.SubjectSalt, _demo.UserId);
    });

    public static string Mint(string salt, string userId)
    {
        var digest = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(salt),
            Encoding.UTF8.GetBytes(userId));

        // Truncation is fine here and would not be for a MAC: this is an
        // opaque handle, not a verification tag. 21 base64url characters is
        // ~126 bits, far past any collision worry for a user population.
        return "u_" + Base64Url.EncodeToString(digest)[0..21];
    }
}
