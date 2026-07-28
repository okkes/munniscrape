using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// RFC 7636 proof key for code exchange.
///
/// A native OAuth client redirects to a scheme any app on the device can
/// claim, so an authorization code can be intercepted before it is spent.
/// PKCE is what makes an intercepted code worthless: the authorize request
/// carries a one-way hash of a secret this process invented, and the token
/// exchange has to produce the secret itself. An authorization server that
/// asked for a challenge and is then offered a code without a verifier
/// rejects the exchange outright - which is why omitting this does not
/// degrade a login, it fails one.
///
/// The verifier and the challenge are one object precisely because they must
/// stay together: they are two halves of the same proof, and a verifier that
/// does not survive from the authorize call to the exchange is the same bug
/// as not sending one at all.
/// </summary>
internal sealed record PkceChallenge
{
    /// <summary>
    /// The only transform worth sending. RFC 7636 also defines <c>plain</c>,
    /// which puts the secret in the URL and protects nothing; a server that
    /// supports both must prefer S256 and so must we.
    /// </summary>
    public const string Method = "S256";

    /// <summary>
    /// The secret, replayed with the code at the exchange. Never logged: it
    /// is one of the two things that make a stolen code usable.
    /// </summary>
    public required string Verifier { get; init; }

    /// <summary>BASE64URL(SHA256(ASCII(verifier))), sent with the authorize request.</summary>
    public required string Challenge { get; init; }

    /// <summary>
    /// A fresh proof key. 32 random bytes is 43 base64url characters, which
    /// is the minimum length RFC 7636 allows and already 256 bits of entropy;
    /// the alphabet is exactly the unreserved set the spec requires, so the
    /// value survives a URL and a form body untouched.
    /// </summary>
    public static PkceChallenge Create() => For(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

    /// <summary>
    /// The challenge for a verifier somebody else chose. Exists so the
    /// offline suite can prove the hash rather than take it on faith.
    /// </summary>
    public static PkceChallenge For(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);

        // ASCII, not UTF-8-of-whatever: the spec hashes the octets of the
        // verifier's own alphabet, and the two only agree because that
        // alphabet is ASCII. Saying so keeps a future "tidy-up" to
        // Encoding.UTF8 from silently changing the hash for nobody's benefit.
        return new PkceChallenge
        {
            Verifier = verifier,
            Challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
        };
    }
}
