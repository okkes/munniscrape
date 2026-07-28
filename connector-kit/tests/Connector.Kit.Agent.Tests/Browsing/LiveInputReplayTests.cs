using Connector.Kit.Agent.Browsing;
using Connector.Kit.Challenges;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// Where a relayed event lands, and everything that must NOT reach the page.
///
/// These stand between a human touching a picture of a login form on their
/// phone and this agent doing something else entirely to a live browser holding
/// somebody's session. The arithmetic is one line; every other test here is
/// about a refusal.
/// </summary>
public class LiveInputReplayTests
{
    /// <summary>A phone-sized frame, which is the shape the agent really streams.</summary>
    private static readonly (int Width, int Height) Frame = (390, 844);

    [Fact]
    public async Task A_fraction_multiplies_by_the_frame_it_was_measured_against()
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await ReplayAsync(surface, [Move(0.5, 0.25)]);

        Assert.Equal(1, dispatched);
        Assert.Equal((195d, 211d), Assert.Single(surface.Moves));
    }

    [Fact]
    public async Task The_corners_of_the_picture_are_the_corners_of_the_page()
    {
        var surface = new RecordingLiveSurface();

        await ReplayAsync(surface, [Move(0, 0), Move(1, 0), Move(0, 1), Move(1, 1)]);

        Assert.Equal([(0d, 0d), (390d, 0d), (0d, 844d), (390d, 844d)], surface.Moves);
    }

    /// <summary>
    /// The bug this exists to catch: Playwright's mouse has no coordinates on
    /// press or release - they act wherever the pointer already is - so a press
    /// dispatched without moving first lands wherever the last event left it,
    /// which after any pause is somewhere the human is not looking.
    /// </summary>
    [Fact]
    public async Task A_press_moves_to_the_point_before_it_presses()
    {
        var surface = new RecordingLiveSurface();

        await ReplayAsync(surface, [Down(0.2, 0.8), Up(0.2, 0.8)]);

        Assert.Equal(["move 78,675.2", "down", "move 78,675.2", "up"], surface.Trace);
    }

    /// <summary>
    /// <c>WheelAsync</c> takes no position either, so the same rule applies -
    /// and getting it wrong scrolls whatever the last click was over, which on
    /// a login page is the field the human was typing in.
    /// </summary>
    [Fact]
    public async Task A_scroll_moves_first_and_travels_in_fractions_of_the_frame()
    {
        var surface = new RecordingLiveSurface();

        await ReplayAsync(surface, [Scroll(0.5, 0.5, deltaY: 0.25)]);

        Assert.Equal(["move 195,422", "scroll 211"], surface.Trace);
    }

    /// <summary>
    /// A distance, not a position, so it is clamped rather than refused - and
    /// clamped before it can become <c>float.Infinity</c> halfway through a
    /// batch, which is the outcome the whole-or-nothing rule exists to prevent.
    /// </summary>
    [Fact]
    public async Task An_absurd_scroll_distance_is_clamped_rather_than_dispatched_as_infinity()
    {
        var surface = new RecordingLiveSurface();

        await ReplayAsync(surface, [Scroll(0.5, 0.5, deltaY: 1e18)]);

        var scrolled = Assert.Single(surface.Scrolls);
        Assert.Equal(20 * Frame.Height, scrolled);
        Assert.True(float.IsFinite((float)scrolled));
    }

    [Fact]
    public async Task Text_goes_in_as_literal_characters_and_never_as_a_key()
    {
        var surface = new RecordingLiveSurface();

        // The exact string PressAsync's own documentation says it reads as a
        // chord. It must arrive as nine characters of text and press nothing.
        await ReplayAsync(surface, [Text("Control+o")]);

        Assert.Equal("Control+o", Assert.Single(surface.Texts));
        Assert.Empty(surface.Keys);
    }

    [Fact]
    public async Task Every_key_in_the_grammar_maps_to_a_literal_that_names_exactly_one_key()
    {
        foreach (var key in Enum.GetValues<LiveKey>())
        {
            var literal = LiveKeys.LiteralFor(key);

            Assert.NotNull(literal);

            // The whole hazard in one assertion. PressAsync's own documentation
            // says "Control+o"-style shortcuts are supported, so a literal that
            // could carry a separator would be a way to reach a file dialog
            // from a key channel.
            Assert.DoesNotContain("+", literal, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", literal, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A value nobody wrote an arm for is a refusal, never a fallback. This is
    /// what a key from a newer contract, or an integer a deserialiser cast into
    /// the enum, arrives as.
    /// </summary>
    [Fact]
    public void A_key_this_build_does_not_know_maps_to_nothing()
    {
        Assert.Null(LiveKeys.LiteralFor((LiveKey)999));
    }

    [Fact]
    public async Task A_key_this_build_does_not_know_dispatches_nothing_at_all()
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await ReplayAsync(surface,
            [Move(0.5, 0.5), new LiveInput { Kind = LiveInputKind.Key, Key = (LiveKey)999 }, Move(0.6, 0.6)]);

        Assert.Equal(0, dispatched);
        Assert.Empty(surface.Trace);
    }

    /// <summary>
    /// An event kind this build has no word for never reaches the page, and the
    /// batch carrying it is refused whole.
    ///
    /// The half of the fix that is a proof of unreachability: the contract's
    /// <c>IsWellFormed</c> now asks <c>Enum.IsDefined</c> first, so the
    /// dispatcher's own refusal below is belt and braces. Worth pinning here,
    /// because the enum converter reads an integer as happily as a name and this
    /// is the only thing that says so.
    /// </summary>
    [Fact]
    public async Task An_event_kind_this_build_does_not_know_never_reaches_the_page()
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await ReplayAsync(surface,
            [Down(0.5, 0.5), new LiveInput { Kind = (LiveInputKind)99, X = 0.5, Y = 0.5 }, Up(0.5, 0.5)]);

        Assert.Equal(0, dispatched);
        Assert.Empty(surface.Trace);
    }

    /// <summary>
    /// And if one ever did reach the dispatcher, it costs that one event.
    ///
    /// This is the arm that was commented "unreachable" and was not: it threw an
    /// <c>ArgumentOutOfRangeException</c>, which matched no catch filter around
    /// the call, escaped the replay loop and faulted the input task for the rest
    /// of the login - frames still arriving, nothing the human did working
    /// again, one debug line at disposal. Called directly, because the only
    /// route to it from outside is a hole that has since been closed, and
    /// "unreachable" is exactly the claim that failed the first time.
    /// </summary>
    [Fact]
    public async Task A_kind_that_reaches_the_dispatcher_anyway_is_refused_rather_than_thrown()
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await LiveInputReplay.DispatchAsync(
            new LiveInput { Kind = (LiveInputKind)99, X = 0.5, Y = 0.5 },
            (195, 422),
            scroll: 0,
            surface,
            CancellationToken.None);

        Assert.False(dispatched);
        Assert.Empty(surface.Trace);
    }

    /// <summary>
    /// The same for the two arms that used to trust a nullable: a key with no
    /// literal, and a text event with no text, are refusals at the dispatcher
    /// and not exceptions out of it.
    ///
    /// <c>PressAsync</c>'s one real implementation throws on a key it has no
    /// word for, and <c>input.Text!</c> was a null-forgiving operator standing
    /// in for a check that lived somewhere else entirely.
    /// </summary>
    [Theory]
    [InlineData(LiveInputKind.Key)]
    [InlineData(LiveInputKind.Text)]
    public async Task An_arm_whose_payload_is_missing_refuses_rather_than_throws(LiveInputKind kind)
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await LiveInputReplay.DispatchAsync(
            new LiveInput { Kind = kind, Key = kind is LiveInputKind.Key ? (LiveKey)999 : null },
            (195, 422),
            scroll: 0,
            surface,
            CancellationToken.None);

        Assert.False(dispatched);
        Assert.Empty(surface.Trace);
    }

    /// <summary>
    /// The batch is the unit, and a half-dispatched batch is worse than none:
    /// refusing only the bad event in the middle of Down/Move/Up leaves the
    /// button held down on a page the human is still looking at, with no event
    /// left to release it.
    /// </summary>
    [Fact]
    public async Task One_malformed_event_refuses_the_whole_batch()
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await ReplayAsync(surface,
            [Down(0.5, 0.5), Move(0.5, 0.5), Move(1.4, 0.5), Up(0.5, 0.5)]);

        Assert.Equal(0, dispatched);
        Assert.Empty(surface.Trace);
    }

    [Theory]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    [InlineData(-0.0001, 0.5)]
    [InlineData(0.5, 1.0001)]
    public async Task A_coordinate_off_the_picture_is_refused_and_never_clamped(double x, double y)
    {
        var surface = new RecordingLiveSurface();

        Assert.Equal(0, await ReplayAsync(surface, [Move(x, y)]));
        Assert.Empty(surface.Moves);
    }

    [Fact]
    public async Task Empty_text_and_over_long_text_are_both_refused()
    {
        var surface = new RecordingLiveSurface();

        Assert.Equal(0, await ReplayAsync(surface, [Text(string.Empty)]));
        Assert.Equal(0, await ReplayAsync(surface, [Text(new string('a', LiveInput.MaxTextLength + 1))]));
        Assert.Empty(surface.Texts);
    }

    /// <summary>
    /// Enter is available as a key and says so; a newline smuggled inside a
    /// "typed" string submits a form the human was still filling in.
    /// </summary>
    [Fact]
    public async Task Text_carrying_a_control_character_is_refused()
    {
        var surface = new RecordingLiveSurface();

        Assert.Equal(0, await ReplayAsync(surface, [Text("hunter2\n")]));
        Assert.Empty(surface.Texts);
    }

    /// <summary>
    /// The redactor's refusal arriving here, exactly as it arrives in the tap
    /// relay: if no frame ever left this machine then nobody saw anything, and
    /// these events cannot be answers to it.
    /// </summary>
    [Fact]
    public async Task Input_that_answers_no_picture_is_refused()
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await LiveInputReplay.ReplayAsync(
            [Move(0.5, 0.5)], (0, 0), surface, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, dispatched);
        Assert.Empty(surface.Trace);
    }

    /// <summary>
    /// A failure mid-batch stops the batch rather than carrying on into a page
    /// that has just proved it is not there.
    /// </summary>
    [Fact]
    public async Task A_dispatch_that_fails_stops_the_rest_of_the_batch()
    {
        var surface = new RecordingLiveSurface { FailOn = "down" };

        var dispatched = await ReplayAsync(surface, [Down(0.5, 0.5), Move(0.6, 0.6), Key(LiveKey.Enter)]);

        Assert.Equal(0, dispatched);
        Assert.Equal(["move 195,422", "down"], surface.Trace);
        Assert.Empty(surface.Keys);
    }

    [Fact]
    public async Task Events_are_dispatched_in_the_order_the_human_made_them()
    {
        var surface = new RecordingLiveSurface();

        var dispatched = await ReplayAsync(surface,
            [Down(0, 0), Up(0, 0), Text("ah"), Key(LiveKey.Tab), Text("b"), Key(LiveKey.Enter)]);

        Assert.Equal(6, dispatched);
        Assert.Equal(["move 0,0", "down", "move 0,0", "up", "text[2]", "key Tab", "text[1]", "key Enter"],
            surface.Trace);
    }

    private static Task<int> ReplayAsync(ILiveSurface surface, IReadOnlyList<LiveInput> events) =>
        LiveInputReplay.ReplayAsync(events, Frame, surface, NullLogger.Instance, CancellationToken.None);

    private static LiveInput Move(double x, double y) => new() { Kind = LiveInputKind.Move, X = x, Y = y };

    private static LiveInput Down(double x, double y) => new() { Kind = LiveInputKind.Down, X = x, Y = y };

    private static LiveInput Up(double x, double y) => new() { Kind = LiveInputKind.Up, X = x, Y = y };

    private static LiveInput Scroll(double x, double y, double deltaY) =>
        new() { Kind = LiveInputKind.Scroll, X = x, Y = y, DeltaY = deltaY };

    private static LiveInput Text(string text) => new() { Kind = LiveInputKind.Text, Text = text };

    private static LiveInput Key(LiveKey key) => new() { Kind = LiveInputKind.Key, Key = key };
}
