using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Security;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// The sealed bundle is the only thing standing between "the user's device
/// holds their credentials" and "anyone holding the blob holds the
/// credentials". Every rejection rule here is load-bearing, and the
/// rotation property is what keeps a key change from being a mass
/// re-login event.
/// </summary>
public sealed class SealedBundleCodecTests
{
    private const string Provider = "jumbo";
    private const string Subject = "u_7Kf3aQ9zXbN2mR5vTc";
    private const int ManifestVersion = 3;

    private static readonly BundleBinding Binding = new(Provider, Subject, ManifestVersion);

    // Derived from the kid rather than random, so a failure is reproducible
    // and a test never depends on which bytes the CSPRNG handed out.
    private static byte[] KeyFor(string kid) => SHA256.HashData(Encoding.UTF8.GetBytes("connector.kit.tests:" + kid));

    private static BundleKeyRing Ring(string currentKid, params string[] kids)
    {
        var keys = kids.ToDictionary(k => k, KeyFor, StringComparer.Ordinal);
        return new BundleKeyRing(currentKid, keys);
    }

    private static (SealedBundleCodec Codec, TestTime Time) Codec(string currentKid = "k1", params string[] kids)
    {
        var time = TestTime.AtAnchor();
        return (new SealedBundleCodec(Ring(currentKid, kids.Length == 0 ? [currentKid] : kids), time), time);
    }

    private static BundlePayload Payload(DateTimeOffset? expiresAt = null) => new()
    {
        SessionId = Make.SessionId,
        Provider = Provider,
        IssuedAt = TestTime.Anchor,
        ExpiresAt = expiresAt ?? TestTime.Anchor.AddDays(30),
        Material = new SessionMaterial
        {
            AccessToken = "at_live_9f2c",
            RefreshToken = "rt_live_71ab",
            StorageState = """{"cookies":[]}""",
            DeviceId = "dev_1234",
        },
        Config = new Dictionary<string, string>(StringComparer.Ordinal) { ["country"] = "NL", ["language"] = "nl" },
        Accounts =
        [
            new ReachableAccount { ExternalId = "NL91INGB0417164300", Type = "savings", DisplayName = "Oranje" },
        ],
    };

    [Fact]
    public void Round_trips_every_field()
    {
        var (codec, _) = Codec();
        var payload = Payload();

        var opened = codec.Open(codec.Seal(payload, Binding), Binding);

        Assert.Equal(1, opened.V);
        Assert.Equal(payload.SessionId, opened.SessionId);
        Assert.Equal(payload.Provider, opened.Provider);
        Assert.Equal(payload.IssuedAt, opened.IssuedAt);
        Assert.Equal(payload.ExpiresAt, opened.ExpiresAt);
        Assert.Equal("at_live_9f2c", opened.Material.AccessToken);
        Assert.Equal("rt_live_71ab", opened.Material.RefreshToken);
        Assert.Equal("dev_1234", opened.Material.DeviceId);
        Assert.Equal("NL", opened.Config["country"]);
        Assert.Equal("NL91INGB0417164300", Assert.Single(opened.Accounts).ExternalId);
    }

    [Fact]
    public void Seals_to_the_documented_wire_shape()
    {
        var (codec, _) = Codec("k3", "k3");

        var parts = codec.Seal(Payload(), Binding).Split('.');

        Assert.Equal(5, parts.Length);
        Assert.Equal("sb_v1", parts[0]);
        Assert.Equal("k3", parts[1]);
        Assert.Equal(12, Base64Url.DecodeFromChars(parts[2]).Length);   // AES-GCM nonce
        Assert.Equal(16, Base64Url.DecodeFromChars(parts[4]).Length);   // AES-GCM tag
    }

    [Fact]
    public void A_bundle_carries_no_plaintext_credential()
    {
        var (codec, _) = Codec();

        var bundle = codec.Seal(Payload(), Binding);

        // Not a formality: the bundle is handed to munni and then to a
        // device, both of which log request bodies somewhere.
        Assert.DoesNotContain("rt_live_71ab", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("at_live_9f2c", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain(Make.SessionId, bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_pointer_bundle_holds_no_secret_at_all()
    {
        var (codec, _) = Codec();
        var payload = Payload() with { Material = SessionMaterial.ForAgent("agt_1", "prf_1") };

        var opened = codec.Open(codec.Seal(payload, Binding), Binding);

        Assert.True(opened.Material.IsAgentPointer);
        Assert.Null(opened.Material.AccessToken);
        Assert.Null(opened.Material.StorageState);
    }

    [Fact]
    public void Rejects_a_bundle_sealed_for_another_subject()
    {
        var (codec, _) = Codec();
        var bundle = codec.Seal(Payload(), Binding);

        var ex = Assert.Throws<ConnectorException>(
            () => codec.Open(bundle, Binding with { Subject = "u_someoneElse00000000" }));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Fact]
    public void Rejects_a_bundle_sealed_for_another_provider()
    {
        var (codec, _) = Codec();
        var bundle = codec.Seal(Payload(), Binding);

        var ex = Assert.Throws<ConnectorException>(() => codec.Open(bundle, Binding with { ProviderId = "lidl" }));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Fact]
    public void Rejects_a_bundle_minted_before_a_breaking_manifest_change()
    {
        var (codec, _) = Codec();
        var bundle = codec.Seal(Payload(), Binding);

        // The adapter's material shape moved, so the manifest version was
        // bumped. Opening this would misinterpret the old shape.
        var ex = Assert.Throws<ConnectorException>(
            () => codec.Open(bundle, Binding with { ManifestVersion = ManifestVersion + 1 }));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Fact]
    public void Rejects_an_unknown_key_id()
    {
        var (minted, _) = Codec("k1", "k1");
        var bundle = minted.Seal(Payload(), Binding);

        var elsewhere = new SealedBundleCodec(Ring("k9", "k9"), TestTime.AtAnchor());

        var ex = Assert.Throws<ConnectorException>(() => elsewhere.Open(bundle, Binding));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Theory]
    [InlineData(2)]   // nonce
    [InlineData(3)]   // ciphertext
    [InlineData(4)]   // tag
    public void Rejects_a_tampered_bundle(int segment)
    {
        var (codec, _) = Codec();
        var parts = codec.Seal(Payload(), Binding).Split('.');

        var bytes = Base64Url.DecodeFromChars(parts[segment]);
        bytes[0] ^= 0xFF;
        parts[segment] = Base64Url.EncodeToString(bytes);

        var ex = Assert.Throws<ConnectorException>(() => codec.Open(string.Join('.', parts), Binding));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Fact]
    public void Rejects_an_expired_payload_even_though_the_ciphertext_is_valid()
    {
        var (codec, time) = Codec();
        var bundle = codec.Seal(Payload(expiresAt: TestTime.Anchor.AddHours(1)), Binding);

        Assert.Equal(Make.SessionId, codec.Open(bundle, Binding).SessionId);

        time.Now = TestTime.Anchor.AddHours(1).AddTicks(1);

        var ex = Assert.Throws<ConnectorException>(() => codec.Open(bundle, Binding));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Fact]
    public void Expiry_is_exclusive_at_the_boundary()
    {
        var (codec, time) = Codec();
        var expiry = TestTime.Anchor.AddHours(1);
        var bundle = codec.Seal(Payload(expiresAt: expiry), Binding);

        time.Now = expiry.AddTicks(-1);
        Assert.Equal(Make.SessionId, codec.Open(bundle, Binding).SessionId);

        // A bundle that expires "now" is expired - a lifetime that includes
        // its own end is a lifetime nobody can reason about.
        time.Now = expiry;
        Assert.Throws<ConnectorException>(() => codec.Open(bundle, Binding));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-bundle")]
    [InlineData("sb_v1.k1.aaa.bbb")]                    // four segments
    [InlineData("sb_v2.k1.aaa.bbb.ccc")]                // wrong version prefix
    [InlineData("sb_v1.k1.!!!!.!!!!.!!!!")]             // not base64url
    public void Rejects_a_malformed_bundle(string bundle)
    {
        var (codec, _) = Codec();

        var ex = Assert.Throws<ConnectorException>(() => codec.Open(bundle, Binding));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Fact]
    public void Rejects_a_short_nonce_without_letting_the_cipher_decide()
    {
        var (codec, _) = Codec();
        var parts = codec.Seal(Payload(), Binding).Split('.');
        parts[2] = Base64Url.EncodeToString(new byte[8]);

        var ex = Assert.Throws<ConnectorException>(() => codec.Open(string.Join('.', parts), Binding));

        Assert.Equal(ErrorCode.SessionExpired, ex.Code);
    }

    [Fact]
    public void Every_rejection_looks_identical_to_a_prober()
    {
        var (codec, time) = Codec();
        var good = codec.Seal(Payload(expiresAt: TestTime.Anchor.AddHours(1)), Binding);
        var tampered = Tamper(good);

        var attempts = new List<Func<BundlePayload>>
        {
            () => codec.Open("garbage", Binding),
            () => codec.Open(good, Binding with { Subject = "u_other" }),
            () => codec.Open(good, Binding with { ProviderId = "lidl" }),
            () => codec.Open(good, Binding with { ManifestVersion = 99 }),
            () => codec.Open(tampered, Binding),
            () => new SealedBundleCodec(Ring("k9", "k9"), time).Open(good, Binding),
        };

        var codes = attempts.Select(a => Assert.Throws<ConnectorException>(() => a()).Code).Distinct().ToList();

        // Distinguishable failures would tell an attacker holding a stolen
        // blob which of the three bindings they got wrong.
        Assert.Equal(new[] { ErrorCode.SessionExpired }, codes);

        time.Now = TestTime.Anchor.AddDays(1);
        Assert.Equal(ErrorCode.SessionExpired, Assert.Throws<ConnectorException>(() => codec.Open(good, Binding)).Code);
    }

    [Fact]
    public void Rotation_seals_with_the_new_key_and_still_opens_the_old_one()
    {
        var time = TestTime.AtAnchor();
        var payload = Payload();

        var before = new SealedBundleCodec(Ring("k1", "k1"), time);
        var minted = before.Seal(payload, Binding);
        Assert.StartsWith("sb_v1.k1.", minted, StringComparison.Ordinal);

        // The rotation: k2 becomes current, k1 stays in the ring.
        var after = new SealedBundleCodec(Ring("k2", "k1", "k2"), time);

        Assert.StartsWith("sb_v1.k2.", after.Seal(payload, Binding), StringComparison.Ordinal);

        // The whole point of a kid: every bundle already on a user's device
        // keeps working, so rotating a key is not a mass re-login event.
        Assert.Equal(payload.SessionId, after.Open(minted, Binding).SessionId);
    }

    [Fact]
    public void Retiring_a_key_is_what_finally_invalidates_its_bundles()
    {
        var time = TestTime.AtAnchor();
        var minted = new SealedBundleCodec(Ring("k1", "k1"), time).Seal(Payload(), Binding);

        // k1 dropped from the ring once every bundle sealed under it has
        // outlived the provider's TTL.
        var retired = new SealedBundleCodec(Ring("k2", "k2"), time);

        Assert.Equal(
            ErrorCode.SessionExpired,
            Assert.Throws<ConnectorException>(() => retired.Open(minted, Binding)).Code);
    }

    [Fact]
    public void A_ring_refuses_a_key_that_is_not_aes_256()
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["k1"] = new byte[16] };

        Assert.Throws<ArgumentException>(() => new BundleKeyRing("k1", keys));
    }

    [Fact]
    public void A_ring_refuses_a_current_kid_it_does_not_hold()
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["k1"] = KeyFor("k1") };

        Assert.Throws<ArgumentException>(() => new BundleKeyRing("k2", keys));
    }

    [Fact]
    public void An_ephemeral_ring_is_usable_but_isolated()
    {
        var time = TestTime.AtAnchor();
        var one = new SealedBundleCodec(BundleKeyRing.Ephemeral(), time);
        var other = new SealedBundleCodec(BundleKeyRing.Ephemeral(), time);

        var bundle = one.Seal(Payload(), Binding);
        Assert.Equal(Make.SessionId, one.Open(bundle, Binding).SessionId);

        // Two processes with ephemeral rings share nothing - which is why a
        // deployed host must refuse to start on one.
        Assert.Throws<ConnectorException>(() => other.Open(bundle, Binding));
    }

    [Fact]
    public void Base64_from_a_ring_configured_as_text_matches_the_raw_bytes()
    {
        var raw = KeyFor("k1");
        var fromBytes = new BundleKeyRing("k1", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["k1"] = raw });
        var fromText = BundleKeyRing.FromBase64(
            "k1",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["k1"] = Convert.ToBase64String(raw) });

        var time = TestTime.AtAnchor();
        var bundle = new SealedBundleCodec(fromBytes, time).Seal(Payload(), Binding);

        Assert.Equal(Make.SessionId, new SealedBundleCodec(fromText, time).Open(bundle, Binding).SessionId);
    }

    private static string Tamper(string bundle)
    {
        var parts = bundle.Split('.');
        var bytes = Base64Url.DecodeFromChars(parts[3]);
        bytes[0] ^= 0xFF;
        parts[3] = Base64Url.EncodeToString(bytes);
        return string.Join('.', parts);
    }
}
