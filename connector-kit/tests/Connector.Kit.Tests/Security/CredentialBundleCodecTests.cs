using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Security;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// The credential bundle: what the human typed, sealed for their own device.
///
/// It rides the same codec, the same key and the same associated data as a
/// session bundle, and that is the point - the custody argument is identical.
/// What differs is what leaks if it escapes. A session bundle carries a token
/// that rotates and can be revoked upstream; this carries a password, which
/// does neither. So the two rules with teeth are that the AAD binds it to one
/// person, and that the two KINDS cannot be mistaken for each other.
/// </summary>
public sealed class CredentialBundleCodecTests
{
    private const string Provider = "jumbo";
    private const string Subject = "u_7Kf3aQ9zXbN2mR5vTc";
    private const int ManifestVersion = 3;

    private static readonly BundleBinding Binding = new(Provider, Subject, ManifestVersion);

    private static byte[] KeyFor(string kid) => SHA256.HashData(Encoding.UTF8.GetBytes("connector.kit.tests:" + kid));

    private static (SealedBundleCodec Codec, TestTime Time) Codec()
    {
        var time = TestTime.AtAnchor();
        var ring = new BundleKeyRing("k1", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["k1"] = KeyFor("k1") });
        return (new SealedBundleCodec(ring, time), time);
    }

    private static CredentialPayload Payload(DateTimeOffset? expiresAt = null) => new()
    {
        SessionId = Make.SessionId,
        Provider = Provider,
        IssuedAt = TestTime.Anchor,
        ExpiresAt = expiresAt ?? TestTime.Anchor.AddDays(30),
        Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "shopper@example.test",
            ["password"] = "correct-horse-battery-staple",
        },
    };

    [Fact]
    public void What_was_typed_survives_a_round_trip_exactly()
    {
        var (codec, _) = Codec();

        var sealedBundle = codec.SealCredentials(Payload(), Binding);
        var opened = codec.OpenCredentials(sealedBundle, Binding);

        Assert.Equal("shopper@example.test", opened.Inputs["username"]);
        Assert.Equal("correct-horse-battery-staple", opened.Inputs["password"]);
        Assert.Equal(Make.SessionId, opened.SessionId);
        Assert.Equal(Provider, opened.Provider);
    }

    /// <summary>
    /// The password must not be readable from the blob. Not a cryptographic
    /// proof - it is AES-GCM either way - but the one mistake this catches is
    /// the one that actually happens: a payload that stops being encrypted at
    /// all and nobody notices because the round trip still passes.
    /// </summary>
    [Fact]
    public void The_sealed_form_carries_no_readable_credential()
    {
        var (codec, _) = Codec();

        var sealedBundle = codec.SealCredentials(Payload(), Binding);

        Assert.StartsWith("cb_v1.", sealedBundle, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse-battery-staple", sealedBundle, StringComparison.Ordinal);
        Assert.DoesNotContain("shopper@example.test", sealedBundle, StringComparison.Ordinal);
    }

    // ---- the two kinds are not interchangeable ------------------------------

    /// <summary>
    /// They share a key and an AAD, so without the prefix a credential bundle
    /// handed to a session route would decrypt perfectly and then deserialise
    /// into a shape it is not. Refused as malformed, before any of that.
    /// </summary>
    [Fact]
    public void A_credential_bundle_cannot_be_opened_as_a_session_bundle()
    {
        var (codec, _) = Codec();

        var credentials = codec.SealCredentials(Payload(), Binding);

        var error = Assert.Throws<ConnectorException>(() => codec.Open(credentials, Binding));
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    [Fact]
    public void A_session_bundle_cannot_be_opened_as_a_credential_bundle()
    {
        var (codec, _) = Codec();

        var session = codec.Seal(
            new BundlePayload
            {
                SessionId = Make.SessionId,
                Provider = Provider,
                IssuedAt = TestTime.Anchor,
                ExpiresAt = TestTime.Anchor.AddDays(30),
                Material = new SessionMaterial { AccessToken = "at_live_9f2c" },
            },
            Binding);

        var error = Assert.Throws<ConnectorException>(() => codec.OpenCredentials(session, Binding));
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    // ---- who it is for ------------------------------------------------------

    [Theory]
    [InlineData("u_somebody_else", Provider, ManifestVersion)]
    [InlineData(Subject, "ah", ManifestVersion)]
    [InlineData(Subject, Provider, ManifestVersion + 1)]
    public void A_bundle_opens_for_nobody_but_the_person_it_was_sealed_for(
        string subject, string provider, int manifestVersion)
    {
        var (codec, _) = Codec();

        var sealedBundle = codec.SealCredentials(Payload(), Binding);

        // Every mismatch is the same answer, so a caller probing with somebody
        // else's blob learns nothing from which one it got wrong.
        var error = Assert.Throws<ConnectorException>(
            () => codec.OpenCredentials(sealedBundle, new BundleBinding(provider, subject, manifestVersion)));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    [Fact]
    public void A_bundle_stops_working_when_it_says_it_does()
    {
        var (codec, time) = Codec();

        var sealedBundle = codec.SealCredentials(Payload(TestTime.Anchor.AddDays(30)), Binding);

        time.Now = TestTime.Anchor + TimeSpan.FromDays(30).Add(TimeSpan.FromSeconds(1));

        var error = Assert.Throws<ConnectorException>(() => codec.OpenCredentials(sealedBundle, Binding));
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    [Fact]
    public void A_bundle_one_second_inside_its_life_still_opens()
    {
        var (codec, time) = Codec();

        var sealedBundle = codec.SealCredentials(Payload(TestTime.Anchor.AddDays(30)), Binding);

        time.Now = TestTime.Anchor + TimeSpan.FromDays(30).Subtract(TimeSpan.FromSeconds(1));

        Assert.Equal("correct-horse-battery-staple", codec.OpenCredentials(sealedBundle, Binding).Inputs["password"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cb_v1.k1.not-base64!.x.y")]
    [InlineData("cb_v1.k1.AAAA")]
    [InlineData("cb_v2.k1.AAAA.BBBB.CCCC")]
    public void A_blob_that_is_not_one_of_ours_is_refused_rather_than_read(string bundle)
    {
        var (codec, _) = Codec();

        var error = Assert.Throws<ConnectorException>(() => codec.OpenCredentials(bundle, Binding));
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }
}
