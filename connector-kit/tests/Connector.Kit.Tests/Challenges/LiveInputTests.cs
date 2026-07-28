using System.Reflection;
using Connector.Kit.Challenges;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// The live view's input grammar, which is the thing standing between a human
/// filling in a login form and a general remote-control channel into a browser
/// that is authenticated as them.
///
/// The relay this grows out of had a grammar narrow enough to be a guarantee:
/// fractions and a terminal marker, so "navigate" and "read the cookies" were
/// not refused, they were unsayable. A keyboard is strictly more than that, so
/// what remains has to be pinned rather than described.
/// </summary>
public sealed class LiveInputTests
{
    // ── the negative space, which is the whole argument ──────────────────

    /// <summary>
    /// The guarantee is what this record CANNOT say.
    ///
    /// Every dangerous thing an input channel could do to a browser needs a
    /// destination: a URL to navigate to, a selector to reach, a script to
    /// evaluate, a path to upload. None of those can be expressed here, and
    /// this test is what keeps it that way - adding a field is then a
    /// deliberate act with a failing test attached, rather than a convenience
    /// somebody adds on a Friday because a provider needed it.
    /// </summary>
    [Fact]
    public void An_event_cannot_name_a_destination()
    {
        var carried = typeof(LiveInput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            nameof(LiveInput.DeltaY),
            nameof(LiveInput.Key),
            nameof(LiveInput.Kind),
            nameof(LiveInput.Sequence),
            nameof(LiveInput.Text),
            nameof(LiveInput.X),
            nameof(LiveInput.Y),
        ];

        Assert.Equal(expected, carried);
    }

    /// <summary>
    /// A modifier is how a click becomes something else - Ctrl+click opens a
    /// tab, and a held modifier survives to be picked up by a later event. The
    /// set is closed so that cannot be said either.
    /// </summary>
    [Fact]
    public void No_key_is_a_modifier_and_none_can_be_held()
    {
        var keys = Enum.GetNames<LiveKey>();

        Assert.DoesNotContain("Control", keys);
        Assert.DoesNotContain("Shift", keys);
        Assert.DoesNotContain("Alt", keys);
        Assert.DoesNotContain("Meta", keys);

        // Nor is there a way to say "hold this". Down and Up are pointer-only.
        Assert.DoesNotContain("KeyDown", Enum.GetNames<LiveInputKind>());
        Assert.DoesNotContain("KeyUp", Enum.GetNames<LiveInputKind>());
    }

    // ── coordinates ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(1.01, 0.5)]
    [InlineData(0.5, -0.01)]
    [InlineData(0.5, 1.01)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    public void A_pointer_outside_the_picture_is_refused_rather_than_clamped(double x, double y)
    {
        // Clamping would be a click somewhere the human did not choose, on a
        // page that is at that moment authenticated as them.
        Assert.False(new LiveInput { Kind = LiveInputKind.Down, X = x, Y = y }.IsWellFormed());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(0.5, 0.5)]
    public void The_edges_of_the_picture_are_inside_it(double x, double y)
    {
        Assert.True(new LiveInput { Kind = LiveInputKind.Move, X = x, Y = y }.IsWellFormed());
    }

    // ── typed text ───────────────────────────────────────────────────────

    [Fact]
    public void Typed_text_may_be_long_enough_for_a_password_manager()
    {
        var pasted = new string('x', LiveInput.MaxTextLength);
        Assert.True(new LiveInput { Kind = LiveInputKind.Text, Text = pasted }.IsWellFormed());
        Assert.False(new LiveInput { Kind = LiveInputKind.Text, Text = pasted + "x" }.IsWellFormed());
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("ab")]
    [InlineData("a\0b")]
    public void Typed_text_carries_no_control_characters(string text)
    {
        // A newline inside "typed text" submits a form the human was still
        // filling in, and an escape sequence means something to whatever reads
        // the log downstream. Enter exists as a key and says so out loud.
        Assert.False(new LiveInput { Kind = LiveInputKind.Text, Text = text }.IsWellFormed());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Typing_nothing_is_not_an_event(string? text)
    {
        Assert.False(new LiveInput { Kind = LiveInputKind.Text, Text = text }.IsWellFormed());
    }

    [Fact]
    public void A_password_is_ordinary_text_to_this_grammar()
    {
        // The point of the whole design: this is where a credential lives now,
        // in transit for one request, instead of in a job's inputs at rest.
        Assert.True(new LiveInput { Kind = LiveInputKind.Text, Text = "correct horse battery staple" }
            .IsWellFormed());
    }

    // ── keys ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_key_must_be_one_of_the_named_ones()
    {
        Assert.True(new LiveInput { Kind = LiveInputKind.Key, Key = LiveKey.Enter }.IsWellFormed());
        Assert.False(new LiveInput { Kind = LiveInputKind.Key, Key = null }.IsWellFormed());
        Assert.False(new LiveInput { Kind = LiveInputKind.Key, Key = (LiveKey)9999 }.IsWellFormed());
    }

    // ── batches ──────────────────────────────────────────────────────────

    [Fact]
    public void A_batch_is_bounded_at_both_ends()
    {
        LiveInput Move() => new() { Kind = LiveInputKind.Move, X = 0.5, Y = 0.5 };

        Assert.False(new LiveInputBatch { Events = [] }.IsWellFormed());

        Assert.True(new LiveInputBatch
        {
            Events = [.. Enumerable.Range(0, LiveInputBatch.MaxEvents).Select(_ => Move())],
        }.IsWellFormed());

        // Otherwise a batch is a way to keep an agent replaying for as long as
        // the sender likes.
        Assert.False(new LiveInputBatch
        {
            Events = [.. Enumerable.Range(0, LiveInputBatch.MaxEvents + 1).Select(_ => Move())],
        }.IsWellFormed());
    }

    [Fact]
    public void One_bad_event_refuses_the_whole_batch()
    {
        // Not "drop the bad one and replay the rest": the events are ordered
        // and a drag with a hole in it lands somewhere nobody chose.
        var batch = new LiveInputBatch
        {
            Events =
            [
                new LiveInput { Kind = LiveInputKind.Down, X = 0.1, Y = 0.1 },
                new LiveInput { Kind = LiveInputKind.Move, X = 5.0, Y = 0.1 },
                new LiveInput { Kind = LiveInputKind.Up, X = 0.2, Y = 0.2 },
            ],
        };

        Assert.False(batch.IsWellFormed());
    }

    // ── the kind itself ──────────────────────────────────────────────────

    [Fact]
    public void A_kind_this_build_has_never_heard_of_is_refused()
    {
        // The hole this closes was reachable end to end and silent. The enum
        // converter accepts an INTEGER as well as a name, so an undefined kind
        // deserialised cleanly, passed validation because its coordinates were
        // fine, reached the agent's dispatch switch and threw out of a default
        // arm commented "unreachable". That throw faulted the replay loop for
        // the rest of the login: frames kept arriving, every tap and keystroke
        // did nothing, and one debug line at disposal was the only trace.
        //
        // An unknown NAME was refused all along. An unknown NUMBER was not,
        // which is why this asserts on the number.
        Assert.False(new LiveInput { Kind = (LiveInputKind)99, X = 0.5, Y = 0.5 }.IsWellFormed());
    }

    [Fact]
    public void An_unknown_kind_is_refused_before_its_own_fields_are_read()
    {
        // Belt and braces on the ordering: an undefined kind with a perfectly
        // good payload must still be refused, or the check is only running on
        // the paths that were already failing.
        Assert.False(new LiveInput { Kind = (LiveInputKind)7, Text = "hello" }.IsWellFormed());
        Assert.False(new LiveInput { Kind = (LiveInputKind)(-1), Key = LiveKey.Enter }.IsWellFormed());
    }

    // ── frames ───────────────────────────────────────────────────────────

    [Fact]
    public void A_frame_states_the_size_it_was_taken_at()
    {
        // The consumer needs it to turn a tap on its own screen back into a
        // fraction. A provider that re-renders at another size mid-login would
        // otherwise misplace every event with nothing to notice it by.
        var frame = new LiveFrame { Sequence = 12, Width = 390, Height = 844, Bytes = [1, 2, 3] };

        Assert.Equal(12, frame.Sequence);
        Assert.Equal(390, frame.Width);
        Assert.Equal(844, frame.Height);

        // It does NOT format its own header. It used to, as "12;390x844" - one
        // combined value matching neither X-Live-Sequence nor X-Live-Size, so
        // it was a trap with a test holding it in place: anyone who used it
        // would have produced a header no reader on either leg could parse.
        // The two headers are the wire and the wire is written down elsewhere.
        Assert.Null(typeof(LiveFrame).GetMethod("ToHeaderValue"));
    }
}
