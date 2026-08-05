using System.Globalization;
using System.Text;
using System.Web;
using Connector.Kit.Errors;

namespace RegistryConnector.Adapters.Support;

/// <summary>
/// Whatever the user pasted, turned into one authenticator entry.
///
/// Three shapes reach this, because three shapes are what the world hands
/// people:
///
///   1. A bare base32 secret. What a provider prints under "can't scan the
///      code?".
///   2. <c>otpauth://totp/...?secret=...</c> - one account, the thing a QR
///      code actually encodes.
///   3. <c>otpauth-migration://offline?data=...</c> - Google Authenticator's
///      export. A protobuf that may carry MANY accounts.
///
/// The third is the one worth thinking about. Accepting it is a real
/// kindness: Google Authenticator will not show a user their secret, and its
/// export QR is the only way most people can get one out. Accepting it
/// carelessly is a disaster: that blob can hold every second factor a person
/// owns, and a form that swallows it would collect somebody's bank, their
/// mail and their employer along with the one account we were asked about.
///
/// So a migration blob is accepted only when it contains EXACTLY ONE account.
/// More than one is refused, by count, with the reason - because the honest
/// response to "here is everything I have" is not to quietly keep the part we
/// wanted.
/// </summary>
internal static class TotpSecretReader
{
    private const string MigrationScheme = "otpauth-migration://";
    private const string UriScheme = "otpauth://";

    public static TotpSecret Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.InvalidRequest("totp: nothing to read");
        }

        var text = raw.Trim();

        if (text.StartsWith(MigrationScheme, StringComparison.OrdinalIgnoreCase)) return FromMigration(text);
        if (text.StartsWith(UriScheme, StringComparison.OrdinalIgnoreCase)) return FromUri(text);

        return new TotpSecret { Secret = Totp.FromBase32(text) };
    }

    /// <summary>
    /// <c>otpauth://totp/Issuer:account?secret=...&amp;digits=6&amp;period=30</c>
    /// </summary>
    private static TotpSecret FromUri(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw ConnectorException.InvalidRequest("totp: that otpauth address could not be read");
        }

        if (!string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase))
        {
            // hotp is counter-based: there is no clock to derive a code from
            // and a stored counter would desynchronise on the first retry.
            throw ConnectorException.InvalidRequest(
                $"totp: '{uri.Host}' codes are not time-based, so they cannot be generated here");
        }

        var query = HttpUtility.ParseQueryString(uri.Query);

        return new TotpSecret
        {
            Secret = Totp.FromBase32(query["secret"]),
            Digits = Number(query["digits"], Totp.DefaultDigits),
            PeriodSeconds = Number(query["period"], Totp.DefaultPeriodSeconds),
            Algorithm = Algorithm(query["algorithm"]),
            Issuer = query["issuer"],
        };
    }

    /// <summary>
    /// Google Authenticator's export, which is a protobuf and not a document
    /// anybody published a schema for. The field numbers below are the ones
    /// the format has always used and are stable across every version anyone
    /// has seen:
    ///
    /// <code>
    /// MigrationPayload { repeated OtpParameters otp_parameters = 1; ... }
    /// OtpParameters {
    ///   bytes  secret    = 1;
    ///   string name      = 2;
    ///   string issuer    = 3;
    ///   enum   algorithm = 4;   1=SHA1 2=SHA256 3=SHA512 4=MD5
    ///   enum   digits    = 5;   1=SIX  2=EIGHT
    ///   enum   type      = 6;   1=HOTP 2=TOTP
    /// }
    /// </code>
    /// </summary>
    private static TotpSecret FromMigration(string text)
    {
        var query = HttpUtility.ParseQueryString(text[(text.IndexOf('?', StringComparison.Ordinal) + 1)..]);
        var data = query["data"];

        if (string.IsNullOrWhiteSpace(data))
        {
            throw ConnectorException.InvalidRequest("totp: that export carries no data");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(data.Replace(' ', '+'));
        }
        catch (FormatException)
        {
            throw ConnectorException.InvalidRequest("totp: that export is not readable base64");
        }

        var accounts = new List<TotpSecret>();

        foreach (var (field, value) in Fields(payload))
        {
            if (field != 1 || value is not byte[] entry) continue;

            var parsed = Account(entry);
            if (parsed is not null) accounts.Add(parsed);
        }

        if (accounts.Count == 0)
        {
            throw ConnectorException.InvalidRequest(
                "totp: that export contains no time-based accounts");
        }

        if (accounts.Count > 1)
        {
            // Named by count and never by issuer: listing what else the person
            // exported would mean reading it, and this refuses precisely so
            // that it is not read.
            throw ConnectorException.InvalidRequest(
                $"totp: that export contains {accounts.Count} accounts, and this asks for one. " +
                "It is the QR from your authenticator's 'transfer accounts' screen, which carries every " +
                "account you selected - not just this provider's. Export only this one account and paste " +
                "that, or paste the provider's own setup key instead.");
        }

        return accounts[0];
    }

    private static TotpSecret? Account(byte[] entry)
    {
        byte[]? secret = null;
        string? issuer = null;
        var algorithm = TotpAlgorithm.Sha1;
        var digits = Totp.DefaultDigits;
        var timeBased = true;

        foreach (var (field, value) in Fields(entry))
        {
            switch (field)
            {
                case 1 when value is byte[] bytes: secret = bytes; break;
                case 3 when value is byte[] bytes: issuer = Encoding.UTF8.GetString(bytes); break;
                case 4 when value is ulong number:
                    algorithm = number switch
                    {
                        2 => TotpAlgorithm.Sha256,
                        3 => TotpAlgorithm.Sha512,
                        _ => TotpAlgorithm.Sha1,
                    };
                    break;
                case 5 when value is ulong number: digits = number == 2 ? 8 : 6; break;

                // 1 is HOTP, which is counter-based and cannot be generated
                // from a clock. Skipped rather than refused: an export holding
                // one HOTP entry beside one TOTP entry should still work.
                case 6 when value is ulong number: timeBased = number != 1; break;
                default: break;
            }
        }

        if (secret is null || secret.Length == 0 || !timeBased) return null;

        return new TotpSecret
        {
            Secret = secret,
            Digits = digits,
            Algorithm = algorithm,

            // The export URL-encodes spaces as '+', so an issuer arrives as
            // "BKR+Consumenten". Decoded because it is shown to a human.
            Issuer = issuer?.Replace('+', ' '),
        };
    }

    /// <summary>
    /// Enough protobuf to walk a message: varints and length-delimited
    /// fields. Groups and fixed-width fields are skipped rather than parsed -
    /// this format uses neither, and a decoder that guessed at them would be
    /// inventing structure.
    /// </summary>
    private static IEnumerable<(int Field, object Value)> Fields(byte[] buffer)
    {
        var index = 0;

        while (index < buffer.Length)
        {
            if (!TryVarint(buffer, ref index, out var key)) yield break;

            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            switch (wire)
            {
                case 0:
                    if (!TryVarint(buffer, ref index, out var number)) yield break;
                    yield return (field, number);
                    break;

                case 2:
                    if (!TryVarint(buffer, ref index, out var length)) yield break;
                    if (index + (int)length > buffer.Length) yield break;

                    yield return (field, buffer[index..(index + (int)length)]);
                    index += (int)length;
                    break;

                case 5: index += 4; break;
                case 1: index += 8; break;
                default: yield break;
            }
        }
    }

    private static bool TryVarint(byte[] buffer, ref int index, out ulong value)
    {
        value = 0;
        var shift = 0;

        while (index < buffer.Length)
        {
            var current = buffer[index++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0) return true;

            shift += 7;
            if (shift > 63) return false;
        }

        return false;
    }

    private static int Number(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static TotpAlgorithm Algorithm(string? raw) => raw?.ToUpperInvariant() switch
    {
        "SHA256" => TotpAlgorithm.Sha256,
        "SHA512" => TotpAlgorithm.Sha512,
        _ => TotpAlgorithm.Sha1,
    };
}
