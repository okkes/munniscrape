using System.Globalization;
using System.Text.Json;
using Connector.Kit.Challenges;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// The contract that lets a challenge be answered with taps instead of text.
///
/// An image-grid captcha cannot be answered in words, so the picture is
/// relayed to the human's own device and the taps come back. Everything about
/// that is a chance for the two ends to disagree, and every disagreement ends
/// the same way: a click at a point no human chose, on a live page, in
/// someone's real account. So the encoding is pinned here rather than
/// described - coordinates are fractions of the image and never pixels, the
/// order is the human's, "done" is said explicitly, and a text answer is
/// never mistaken for a tap one.
/// </summary>
public sealed class ChallengeAnswerKindTests
{
    private static Challenge Grid() => new()
    {
        Type = ChallengeType.Image,
        AnswerKind = ChallengeAnswerKind.Taps,
        ExpiresAt = TestTime.Anchor.AddMinutes(5),
    };

    [Fact]
    public void A_challenge_is_answered_in_text_unless_it_says_otherwise()
    {
        // The default is what makes this addition invisible to every adapter
        // and every consumer that came before it.
        var mfa = new Challenge { Type = ChallengeType.MfaCode, ExpiresAt = TestTime.Anchor.AddMinutes(3) };

        Assert.Equal(ChallengeAnswerKind.Text, mfa.AnswerKind);
        Assert.Equal(ChallengeAnswerKind.Taps, Grid().AnswerKind);
    }

    [Fact]
    public void A_payload_that_never_heard_of_answer_kinds_is_a_text_challenge()
    {
        var json = """{"type":"image","expires_at":"2026-07-27T10:00:00+00:00"}""";

        var challenge = JsonSerializer.Deserialize<Challenge>(json, ConnectorWireJson.Options);

        Assert.NotNull(challenge);
        Assert.Equal(ChallengeAnswerKind.Text, challenge.AnswerKind);
    }

    [Fact]
    public void The_answer_kind_crosses_the_wire_by_name()
    {
        // A client renders a tappable image off this field, so it is part of
        // the published surface and not an internal hint.
        var json = JsonSerializer.Serialize(Grid(), ConnectorWireJson.Options);

        Assert.Contains("\"answer_kind\":\"taps\"", json, StringComparison.Ordinal);
        Assert.Equal(
            ChallengeAnswerKind.Taps,
            JsonSerializer.Deserialize<Challenge>(json, ConnectorWireJson.Options)?.AnswerKind);
    }

    [Fact]
    public void A_text_challenge_behaves_exactly_as_it_did_before()
    {
        var mfa = new Challenge
        {
            Type = ChallengeType.MfaCode,
            Delivery = "sms",
            Length = 6,
            ExpiresAt = TestTime.Anchor.AddMinutes(3),
        };

        var answer = new ChallengeAnswer { ChallengeId = "chl_1", Value = "492013" };

        Assert.False(mfa.IsPassive);
        Assert.False(mfa.IsExpired(TestTime.Anchor));
        Assert.Equal("492013", answer.Value);

        // And the one new way it could go wrong: a code is not taps.
        Assert.False(TapAnswer.TryParse(answer.Value, out _));
    }

    [Fact]
    public void A_passive_challenge_still_answers_with_nothing_at_all()
    {
        var approval = new Challenge { Type = ChallengeType.AppApproval, ExpiresAt = TestTime.Anchor.AddMinutes(3) };

        Assert.True(approval.IsPassive);
        Assert.Equal(ChallengeAnswerKind.Text, approval.AnswerKind);
        Assert.Equal(string.Empty, new ChallengeAnswer { ChallengeId = "chl_1" }.Value);
    }

    [Fact]
    public void Taps_round_trip_in_the_order_the_human_made_them()
    {
        // Order is information the client has and cannot be recovered once
        // dropped: some grids ask for tiles in sequence.
        var answered = new TapAnswer
        {
            Taps = [new Tap(0.8333, 0.1667), new Tap(0.1667, 0.5), new Tap(0.5, 0.8333)],
            Submit = true,
        };

        Assert.True(TapAnswer.TryParse(answered.Format(), out var back));
        Assert.Equal(answered.Taps, back.Taps);
        Assert.True(back.Submit);
        Assert.Equal(answered.Format(), back.Format());
    }

    [Fact]
    public void The_encoding_is_the_one_both_ends_were_told_about()
    {
        // Pinned literally: the client formats this string without ever
        // linking the kit, so a change here is a change to a published format.
        Assert.Equal(
            "tap.v1:0.25,0.5;submit",
            new TapAnswer { Taps = [new Tap(0.25, 0.5)], Submit = true }.Format());

        Assert.Equal("tap.v1:0.25,0.5", new TapAnswer { Taps = [new Tap(0.25, 0.5)] }.Format());
    }

    [Theory]
    [InlineData(1.0001)]
    [InlineData(-0.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_coordinate_outside_the_image_is_not_a_coordinate(double bad)
    {
        // Pixels arriving where fractions belong look exactly like this - a
        // phone reporting 412 for x - so it must fail loudly at the type
        // rather than clip to an edge and click the wrong tile.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tap(bad, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tap(0.5, bad));
    }

    [Fact]
    public void The_edges_of_the_image_are_inside_it()
    {
        var topLeft = new Tap(0, 0);
        var bottomRight = new Tap(1, 1);

        Assert.Equal(0, topLeft.X);
        Assert.Equal(1, bottomRight.Y);
        Assert.True(TapAnswer.TryParse("tap.v1:0,0;1,1", out var parsed));
        Assert.Equal([topLeft, bottomRight], parsed.Taps);
    }

    [Theory]
    [InlineData("tap.v1:1.5,0.5")]
    [InlineData("tap.v1:0.5,-0.2")]
    [InlineData("tap.v1:412,733")]       // pixels, the mistake this exists to catch
    [InlineData("tap.v1:NaN,0.5")]
    [InlineData("tap.v1:Infinity,0.5")]
    public void A_coordinate_outside_the_image_is_refused_on_the_way_in(string encoded)
    {
        Assert.False(TapAnswer.TryParse(encoded, out var answer));
        Assert.Null(answer);
    }

    [Fact]
    public void An_empty_tap_list_is_not_the_same_as_no_answer()
    {
        // A grid where nothing matches is answered by pressing verify with
        // nothing selected. That is an answer; an empty Value is silence.
        Assert.False(TapAnswer.TryParse(string.Empty, out _));

        Assert.True(TapAnswer.TryParse(new TapAnswer { Submit = true }.Format(), out var verified));
        Assert.Empty(verified.Taps);
        Assert.True(verified.Submit);

        Assert.True(TapAnswer.TryParse(TapAnswer.Prefix, out var nothingYet));
        Assert.Empty(nothingYet.Taps);
        Assert.False(nothingYet.Submit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("492013")]
    [InlineData("0.5,0.5")]                              // taps without the prefix are not taps
    [InlineData("appie://login-exit?code=abc123")]
    [InlineData("TAP.V1:0.5,0.5")]                       // ordinal, never case-insensitive
    [InlineData("tap.v2:0.5,0.5")]                       // a grammar we do not speak
    [InlineData("tap.v1:0.5")]
    [InlineData("tap.v1:0.5,0.5,0.5")]
    [InlineData("tap.v1:0.5,0.5;")]
    [InlineData("tap.v1:0.5;0.5")]
    [InlineData("tap.v1:0,5;0,5")]                       // a Dutch decimal comma, split as a pair
    public void Nothing_that_is_not_a_tap_answer_parses_as_one(string? value)
    {
        // This is the fallback that makes ChallengeAnswerKind safe to ignore:
        // a consumer that showed a text box returns text, and text is refused.
        Assert.False(TapAnswer.TryParse(value, out var answer));
        Assert.Null(answer);
    }

    [Fact]
    public void Submit_is_terminal()
    {
        // Taps after the marker could never be delivered - the page has moved
        // on - so an answer carrying them is a producer that disagrees about
        // the format, and disagreements are refused rather than half-obeyed.
        Assert.False(TapAnswer.TryParse("tap.v1:submit;0.5,0.5", out _));
        Assert.False(TapAnswer.TryParse("tap.v1:0.5,0.5;submit;submit", out _));
        Assert.True(TapAnswer.TryParse("tap.v1:0.5,0.5;submit", out _));
    }

    [Fact]
    public void Surrounding_whitespace_is_forgiven_and_nothing_else_is()
    {
        // A client that appends a newline is untidy; one that invents a
        // separator is wrong.
        Assert.True(TapAnswer.TryParse("  tap.v1: 0.25,0.5 ; submit \n", out var answer));
        Assert.Equal([new Tap(0.25, 0.5)], answer.Taps);
        Assert.True(answer.Submit);
    }

    [Fact]
    public void Formatting_does_not_depend_on_the_machine_that_did_it()
    {
        // An agent in Amsterdam formats 0.5 as "0,5" under its own locale,
        // and the comma is the pair separator. That is a tap in a different
        // square, not a parse failure.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");

            Assert.Equal("tap.v1:0.25,0.5", new TapAnswer { Taps = [new Tap(0.25, 0.5)] }.Format());
            Assert.True(TapAnswer.TryParse("tap.v1:0.25,0.5", out var answer));
            Assert.Equal([new Tap(0.25, 0.5)], answer.Taps);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_coordinate_carries_only_the_precision_the_wire_holds()
    {
        // Otherwise a tap would not equal the tap it was parsed from, and the
        // two ends would be "agreeing" on numbers that differ.
        var tap = new Tap(0.123456789, 0.5);

        Assert.Equal(0.1235, tap.X);
        Assert.True(TapAnswer.TryParse(new TapAnswer { Taps = [tap] }.Format(), out var back));
        Assert.Equal([tap], back.Taps);
    }

    [Fact]
    public void A_tap_maps_back_into_the_region_the_picture_came_from()
    {
        // The crop's origin is part of the answer's meaning: the same
        // fraction is a different pixel depending on where the image was cut
        // out of the page, and only the agent knows that.
        var crop = new CropRegion(100, 200, 400, 300);

        var (x, y) = new Tap(0.5, 0.5).ToPagePixels(crop);
        Assert.Equal(300d, x, 6);
        Assert.Equal(350d, y, 6);

        var (left, top) = new Tap(0, 0).ToPagePixels(crop);
        Assert.Equal(100d, left, 6);
        Assert.Equal(200d, top, 6);

        var (right, bottom) = new Tap(1, 1).ToPagePixels(crop);
        Assert.Equal(500d, right, 6);
        Assert.Equal(500d, bottom, 6);
    }

    [Fact]
    public void The_same_fraction_survives_a_client_that_scaled_the_picture()
    {
        // The point of normalising: the agent cropped 400x300 css pixels, the
        // phone drew them 360 wide, and the tap still lands in the same tile.
        var crop = new CropRegion(0, 0, 400, 300);
        const double DrawnWidth = 360d;
        const double DrawnHeight = 270d;

        // What a client computes from a touch 120pt across its own rendering.
        var tap = new Tap(120d / DrawnWidth, 90d / DrawnHeight);

        var (x, y) = tap.ToPagePixels(crop);

        // Within a tenth of a pixel of the point they touched, on tiles about
        // 133px wide. The wire's four decimals cost 0.04px of that - the whole
        // error budget, and nowhere near a tile boundary.
        Assert.Equal(400d / 3d, x, 1);
        Assert.Equal(100d, y, 1);
    }
}
