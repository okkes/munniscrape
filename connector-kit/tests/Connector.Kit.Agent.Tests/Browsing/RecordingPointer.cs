using Connector.Kit.Agent.Browsing;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// A mouse that writes down what it was told to do instead of doing it.
///
/// The whole of tap replay is arithmetic and refusals, so this is all a test
/// needs to hold the two claims that matter: the click landed on the pixel the
/// human's fraction names, and a click that should not happen does not happen
/// at all - which cannot be shown by a stub that only counts.
/// </summary>
internal sealed class RecordingPointer : IPointerSurface
{
    /// <summary>Where the mouse was told to click, in order.</summary>
    public List<(double X, double Y)> Clicks { get; } = [];

    /// <summary>Every gap that was waited, in order.</summary>
    public List<TimeSpan> Pauses { get; } = [];

    /// <summary>
    /// Clicks and pauses interleaved. The only way to say "between the taps"
    /// rather than merely "as many pauses as taps, minus one".
    /// </summary>
    public List<string> Trace { get; } = [];

    public Task ClickAsync(double x, double y, CancellationToken ct)
    {
        Clicks.Add((x, y));
        Trace.Add(FormattableString.Invariant($"click {x},{y}"));
        return Task.CompletedTask;
    }

    public Task PauseAsync(TimeSpan gap, CancellationToken ct)
    {
        Pauses.Add(gap);
        Trace.Add(FormattableString.Invariant($"pause {gap.TotalMilliseconds}"));
        return Task.CompletedTask;
    }
}
