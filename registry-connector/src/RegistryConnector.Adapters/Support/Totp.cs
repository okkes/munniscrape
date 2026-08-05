using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Errors;

namespace RegistryConnector.Adapters.Support;

/// <summary>
/// Six digits from a shared secret and the clock. RFC 6238.
///
/// Written rather than taken from a package because it is forty lines of
/// arithmetic with an official answer key: RFC 6238 Appendix B publishes test
/// vectors, so this can be checked against the standard instead of against
/// itself. A TOTP implementation that only agrees with its own tests is worth
/// nothing - the whole point is agreeing with somebody else's clock.
///
/// This exists so a provider that demands a code on EVERY sign-in can be
/// connected without asking a human for six digits every time. That is a
/// deliberate weakening of the user's second factor and is never the default;
/// see <see cref="Bkr.BkrManifest"/> for the shape of the choice and what the
/// user is told before making it.
/// </summary>
internal static class Totp
{
    /// <summary>The step every authenticator uses unless told otherwise.</summary>
    public const int DefaultPeriodSeconds = 30;

    public const int DefaultDigits = 6;

    /// <summary>
    /// The code for <paramref name="at"/>, from a secret in raw bytes.
    /// </summary>
    public static string Generate(
        ReadOnlySpan<byte> secret,
        DateTimeOffset at,
        int digits = DefaultDigits,
        int periodSeconds = DefaultPeriodSeconds,
        TotpAlgorithm algorithm = TotpAlgorithm.Sha1)
    {
        if (secret.Length == 0) throw ConnectorException.InvalidRequest("totp: the secret is empty");
        if (digits is < 6 or > 8) throw ConnectorException.InvalidRequest($"totp: {digits} digits is not 6, 7 or 8");
        if (periodSeconds <= 0) throw ConnectorException.InvalidRequest("totp: the period must be positive");

        // The counter is whole steps since the Unix epoch, big-endian in eight
        // bytes. Negative time is not a thing an authenticator has to handle.
        var counter = Math.Max(0, at.ToUnixTimeSeconds()) / periodSeconds;

        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        Span<byte> mac = stackalloc byte[64];
        var written = algorithm switch
        {
            TotpAlgorithm.Sha1 => HMACSHA1.HashData(secret, message, mac),
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(secret, message, mac),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(secret, message, mac),
            _ => throw ConnectorException.InvalidRequest($"totp: unsupported algorithm '{algorithm}'"),
        };

        // Dynamic truncation, RFC 4226 section 5.3: the low nibble of the last
        // byte picks where to read four bytes from, and the top bit is masked
        // off so the result is positive on every platform.
        var offset = mac[written - 1] & 0x0F;

        var binary = ((mac[offset] & 0x7F) << 24)
                     | ((mac[offset + 1] & 0xFF) << 16)
                     | ((mac[offset + 2] & 0xFF) << 8)
                     | (mac[offset + 3] & 0xFF);

        var modulo = (int)Math.Pow(10, digits);

        // Left-padded: "012345" is a valid code and a provider comparing
        // strings would reject "12345".
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// RFC 4648 base32, which is how every authenticator writes a secret down.
    /// Case-insensitive, padding optional, and spaces ignored because the
    /// providers that print a secret for manual entry group it in fours.
    /// </summary>
    public static byte[] FromBase32(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.InvalidRequest("totp: the secret is empty");
        }

        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new List<byte>(raw.Length * 5 / 8 + 1);
        var buffer = 0;
        var bits = 0;

        foreach (var character in raw)
        {
            if (character is ' ' or '-' or '=') continue;

            var value = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (value < 0)
            {
                throw ConnectorException.InvalidRequest(
                    $"totp: '{character}' is not a base32 character; an authenticator secret is A-Z and 2-7");
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits < 8) continue;

            bits -= 8;
            bytes.Add((byte)((buffer >> bits) & 0xFF));
        }

        if (bytes.Count == 0)
        {
            throw ConnectorException.InvalidRequest("totp: the secret decoded to nothing");
        }

        return [.. bytes];
    }
}

internal enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>One authenticator entry: the secret and how to use it.</summary>
internal sealed record TotpSecret
{
    public required byte[] Secret { get; init; }

    public int Digits { get; init; } = Totp.DefaultDigits;

    public int PeriodSeconds { get; init; } = Totp.DefaultPeriodSeconds;

    public TotpAlgorithm Algorithm { get; init; } = TotpAlgorithm.Sha1;

    /// <summary>Who the entry is for, where the source stated it. Never a secret.</summary>
    public string? Issuer { get; init; }

    public string Now(TimeProvider time) =>
        Totp.Generate(Secret, time.GetUtcNow(), Digits, PeriodSeconds, Algorithm);
}
