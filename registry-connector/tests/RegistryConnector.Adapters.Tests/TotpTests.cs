using System.Text;
using Connector.Kit.Errors;
using RegistryConnector.Adapters.Support;
using Xunit;

namespace RegistryConnector.Adapters.Tests;

/// <summary>
/// Six digits, checked against somebody else's clock.
///
/// The vectors below are RFC 6238 Appendix B, verbatim. That is the entire
/// reason this suite is worth having: a TOTP implementation that agrees only
/// with its own tests is worthless, because the one property it needs is
/// agreeing with a server nobody here controls. Every expected value in the
/// first test came from the RFC, not from running the code.
/// </summary>
public sealed class TotpTests
{
    /// <summary>
    /// The RFC's seeds. It uses ASCII strings whose bytes are the secret -
    /// "12345678901234567890" for SHA1, extended for the wider hashes.
    /// </summary>
    private static byte[] Seed(int length)
    {
        var seed = new byte[length];
        for (var i = 0; i < length; i++) seed[i] = (byte)('1' + (i % 10 == 9 ? -1 + 10 : i % 10));

        // The RFC's seed is "12345678901234567890..." - digits 1..9 then 0.
        var ascii = Encoding.ASCII.GetBytes("12345678901234567890123456789012345678901234567890123456789012345678901234567890");
        return ascii[..length];
    }

    /// <summary>
    /// RFC 6238 Appendix B. Times in seconds since the epoch, 8-digit codes,
    /// three hash algorithms.
    /// </summary>
    [Theory]
    // SHA1, 20-byte seed
    [InlineData(59L, "Sha1", 20, "94287082")]
    [InlineData(1111111109L, "Sha1", 20, "07081804")]
    [InlineData(1111111111L, "Sha1", 20, "14050471")]
    [InlineData(1234567890L, "Sha1", 20, "89005924")]
    [InlineData(2000000000L, "Sha1", 20, "69279037")]
    [InlineData(20000000000L, "Sha1", 20, "65353130")]
    // SHA256, 32-byte seed
    [InlineData(59L, "Sha256", 32, "46119246")]
    [InlineData(1111111109L, "Sha256", 32, "68084774")]
    [InlineData(2000000000L, "Sha256", 32, "90698825")]
    // SHA512, 64-byte seed
    [InlineData(59L, "Sha512", 64, "90693936")]
    [InlineData(1111111109L, "Sha512", 64, "25091201")]
    [InlineData(2000000000L, "Sha512", 64, "38618901")]
    public void The_rfc_6238_vectors_are_reproduced_exactly(
        long unixSeconds, string algorithm, int seedLength, string expected)
    {
        // Named rather than typed, so the enum stays internal: a test is not a
        // reason to widen an API.
        var code = Totp.Generate(
            Seed(seedLength),
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
            digits: 8,
            periodSeconds: 30,
            algorithm: Enum.Parse<TotpAlgorithm>(algorithm));

        Assert.Equal(expected, code);
    }

    /// <summary>
    /// The six-digit case every real provider uses, and the padding that goes
    /// with it: a code whose numeric value is short is still six characters,
    /// and a provider comparing strings would reject "12345".
    /// </summary>
    [Fact]
    public void A_six_digit_code_is_padded_to_six_characters()
    {
        // Walk enough steps to find a code that needs padding; with a uniform
        // distribution one in ten does.
        var secret = Seed(20);
        var padded = 0;

        for (var step = 0; step < 200; step++)
        {
            var code = Totp.Generate(secret, DateTimeOffset.FromUnixTimeSeconds(step * 30L));

            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.InRange(c, '0', '9'));

            if (code[0] == '0') padded++;
        }

        Assert.True(padded > 0, "200 steps produced no code with a leading zero, which is statistically impossible");
    }

    /// <summary>
    /// The code holds for the whole step and changes at the boundary. This is
    /// what makes a stored seed usable at all: the connector and the provider
    /// only agree because both floor the clock the same way.
    /// </summary>
    [Fact]
    public void A_code_holds_for_its_step_and_changes_at_the_boundary()
    {
        var secret = Seed(20);

        // 1_700_000_010 is exactly divisible by 30, so it IS a step boundary
        // and its step runs to ...039 inclusive.
        var atStart = Totp.Generate(secret, DateTimeOffset.FromUnixTimeSeconds(1_700_000_010));
        var atEnd = Totp.Generate(secret, DateTimeOffset.FromUnixTimeSeconds(1_700_000_039));
        var afterwards = Totp.Generate(secret, DateTimeOffset.FromUnixTimeSeconds(1_700_000_040));

        Assert.Equal(atStart, atEnd);
        Assert.NotEqual(atStart, afterwards);
    }

    // ---- what the user actually pastes ---------------------------------------

    [Fact]
    public void A_bare_base32_secret_is_read_with_spaces_and_any_casing()
    {
        // The same secret written the three ways providers print it.
        var plain = TotpSecretReader.Read("JBSWY3DPEHPK3PXP");
        var spaced = TotpSecretReader.Read("jbsw y3dp ehpk 3pxp");
        var padded = TotpSecretReader.Read("JBSWY3DPEHPK3PXP=");

        Assert.Equal(plain.Secret, spaced.Secret);
        Assert.Equal(plain.Secret, padded.Secret);

        Assert.Equal(6, plain.Digits);
        Assert.Equal(30, plain.PeriodSeconds);
        Assert.Equal(TotpAlgorithm.Sha1, plain.Algorithm);
    }

    [Fact]
    public void An_otpauth_uri_carries_its_own_parameters()
    {
        var read = TotpSecretReader.Read(
            "otpauth://totp/Example:me@example.test?secret=JBSWY3DPEHPK3PXP&issuer=Example&digits=8&period=60&algorithm=SHA256");

        Assert.Equal(TotpSecretReader.Read("JBSWY3DPEHPK3PXP").Secret, read.Secret);
        Assert.Equal(8, read.Digits);
        Assert.Equal(60, read.PeriodSeconds);
        Assert.Equal(TotpAlgorithm.Sha256, read.Algorithm);
        Assert.Equal("Example", read.Issuer);
    }

    /// <summary>
    /// Counter-based codes have no clock to derive from, and a stored counter
    /// would desynchronise on the first retry. Refused by name rather than
    /// silently treated as time-based, which would generate wrong codes
    /// forever and look like a bad secret.
    /// </summary>
    [Fact]
    public void A_counter_based_uri_is_refused_by_name()
    {
        var error = Assert.Throws<ConnectorException>(
            () => TotpSecretReader.Read("otpauth://hotp/Example:me?secret=JBSWY3DPEHPK3PXP&counter=1"));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Contains("not time-based", error.Detail, StringComparison.Ordinal);
    }

    // ---- the export, and the line it must not cross --------------------------

    /// <summary>
    /// Google Authenticator will not show a user their secret; its export QR
    /// is the only way most people can get one out. So the format is read.
    /// </summary>
    [Fact]
    public void A_single_account_export_is_read()
    {
        var blob = Migration([("JBSWY3DPEHPK3PXP", "BKR Consumenten", 6)]);

        var read = TotpSecretReader.Read(blob);

        Assert.Equal(TotpSecretReader.Read("JBSWY3DPEHPK3PXP").Secret, read.Secret);
        Assert.Equal(6, read.Digits);
        Assert.Equal("BKR Consumenten", read.Issuer);
    }

    /// <summary>
    /// The line. That blob is the "transfer accounts" QR, and it carries
    /// every account the person ticked - their bank, their mail, their
    /// employer. A form that silently kept the one it wanted would have
    /// collected all of them.
    ///
    /// Refused BY COUNT and never by issuer: naming what else was in there
    /// would mean reading it, and this refuses precisely so that it is not
    /// read.
    /// </summary>
    [Fact]
    public void A_multi_account_export_is_refused_without_naming_what_else_was_in_it()
    {
        var blob = Migration(
        [
            ("JBSWY3DPEHPK3PXP", "BKR Consumenten", 6),
            ("KRSXG5CTMVRXEZLU", "A Bank", 6),
            ("MFRGGZDFMZTWQ2LK", "Somebody's Mail", 6),
        ]);

        var error = Assert.Throws<ConnectorException>(() => TotpSecretReader.Read(blob));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Contains("3 accounts", error.Detail, StringComparison.Ordinal);

        // The other issuers are not repeated back. Reading them out would be
        // the very thing being refused.
        Assert.DoesNotContain("A Bank", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Mail", error.Detail, StringComparison.Ordinal);

        // And it says what to do instead, or the refusal is just a wall.
        Assert.Contains("only this one account", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a Google Authenticator migration payload, so the reader is
    /// tested against the format rather than against a fixture somebody
    /// copied out of a real phone.
    /// </summary>
    private static string Migration(IReadOnlyList<(string Secret, string Issuer, int Digits)> accounts)
    {
        var payload = new List<byte>();

        foreach (var (secret, issuer, digits) in accounts)
        {
            var entry = new List<byte>();

            Bytes(entry, 1, TotpSecretReader_Base32(secret));
            Bytes(entry, 2, Encoding.UTF8.GetBytes($"user@{issuer.Replace(' ', '-')}.test"));
            Bytes(entry, 3, Encoding.UTF8.GetBytes(issuer.Replace(' ', '+')));
            Varint(entry, 4, 1);                       // SHA1
            Varint(entry, 5, digits == 8 ? 2ul : 1ul); // SIX / EIGHT
            Varint(entry, 6, 2);                       // TOTP

            Bytes(payload, 1, [.. entry]);
        }

        return "otpauth-migration://offline?data=" +
               Uri.EscapeDataString(Convert.ToBase64String([.. payload]));
    }

    private static byte[] TotpSecretReader_Base32(string secret) => Totp.FromBase32(secret);

    private static void Varint(List<byte> into, int field, ulong value)
    {
        into.Add((byte)((field << 3) | 0));
        Varint(into, value);
    }

    private static void Bytes(List<byte> into, int field, byte[] value)
    {
        into.Add((byte)((field << 3) | 2));
        Varint(into, (ulong)value.Length);
        into.AddRange(value);
    }

    private static void Varint(List<byte> into, ulong value)
    {
        while (value >= 0x80)
        {
            into.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        into.Add((byte)value);
    }
}
