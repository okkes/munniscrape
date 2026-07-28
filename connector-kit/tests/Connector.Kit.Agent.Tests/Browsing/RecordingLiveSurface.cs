using System.Globalization;
using Connector.Kit.Agent.Browsing;
using Connector.Kit.Challenges;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// A mouse and keyboard that write down what they were told to do instead of
/// doing it.
///
/// Everything the live replay decides is arithmetic and refusals, so a trace is
/// all a test needs to hold the two claims that matter: the event landed on the
/// pixel the human's fraction names, and an event that must not happen does not
/// happen AT ALL - which a stub that only counts cannot show.
///
/// Note that <see cref="PressAsync"/> records the ENUM. That is not laziness:
/// the seam takes the enum precisely so no relayed string can reach Playwright,
/// and a fake that accepted a string would quietly test a different interface
/// from the one that ships. The enum-to-literal switch is tested on its own.
/// </summary>
internal sealed class RecordingLiveSurface : ILiveSurface
{
    /// <summary>Everything, in order. The only way to say "moved BEFORE it pressed".</summary>
    public List<string> Trace { get; } = [];

    /// <summary>Where the pointer was told to go, in order.</summary>
    public List<(double X, double Y)> Moves { get; } = [];

    /// <summary>Keys pressed, in order.</summary>
    public List<LiveKey> Keys { get; } = [];

    /// <summary>Text inserted, in order. Only a test may hold this.</summary>
    public List<string> Texts { get; } = [];

    /// <summary>Scroll distances in page pixels, in order.</summary>
    public List<double> Scrolls { get; } = [];

    /// <summary>Set to throw from the next call of a given kind, to prove a batch stops.</summary>
    public string? FailOn { get; set; }

    public Task MoveAsync(double x, double y, CancellationToken ct)
    {
        Moves.Add((x, y));
        Trace.Add(FormattableString.Invariant($"move {x},{y}"));
        return Fail("move");
    }

    public Task DownAsync(CancellationToken ct)
    {
        Trace.Add("down");
        return Fail("down");
    }

    public Task UpAsync(CancellationToken ct)
    {
        Trace.Add("up");
        return Fail("up");
    }

    public Task ScrollAsync(double deltaY, CancellationToken ct)
    {
        Scrolls.Add(deltaY);
        Trace.Add(FormattableString.Invariant($"scroll {deltaY}"));
        return Fail("scroll");
    }

    public Task InsertTextAsync(string text, CancellationToken ct)
    {
        Texts.Add(text);

        // The length and never the value, so a failing test cannot print
        // somebody's password into a build log.
        Trace.Add(string.Create(CultureInfo.InvariantCulture, $"text[{text.Length}]"));
        return Fail("text");
    }

    public Task PressAsync(LiveKey key, CancellationToken ct)
    {
        Keys.Add(key);
        Trace.Add("key " + key);
        return Fail("key");
    }

    private Task Fail(string what) =>
        string.Equals(FailOn, what, StringComparison.Ordinal)
            ? Task.FromException(new Microsoft.Playwright.PlaywrightException("the page went away"))
            : Task.CompletedTask;
}
