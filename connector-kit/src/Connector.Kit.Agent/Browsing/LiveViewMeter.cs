using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// What one second of a live view cost, summarised.
///
/// Every figure is milliseconds except the counts and the byte size, and the
/// shape is deliberately the shape of a decision: <see cref="Fps"/> says
/// whether this is a live view or a slideshow, <see cref="ShutterP95"/> says
/// whether the browser or the network is the reason, and
/// <see cref="Suppressed"/> says whether the change detection that the whole
/// bandwidth argument rests on is doing anything at all on a real page.
/// </summary>
internal readonly record struct LiveViewWindow
{
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Frames that reached the control plane.</summary>
    public required int Posted { get; init; }

    /// <summary>Shutters whose bytes were identical to the last posted frame.</summary>
    public required int Suppressed { get; init; }

    /// <summary>Shutters that produced no image at all.</summary>
    public required int Missed { get; init; }

    /// <summary>Frames the control plane would not take.</summary>
    public required int Failed { get; init; }

    public required double ShutterMedian { get; init; }

    public required double ShutterP95 { get; init; }

    public required double PostMedian { get; init; }

    public required double PostP95 { get; init; }

    /// <summary>Mean encoded size of the frames posted in this window.</summary>
    public required int MeanBytes { get; init; }

    /// <summary>
    /// Frames per second actually delivered - posted frames only, because a
    /// suppressed frame is one the viewer does not receive. This is the number
    /// the feature is judged on and it is deliberately the pessimistic reading:
    /// a static form legitimately delivers nothing.
    /// </summary>
    public double Fps => Elapsed.TotalSeconds <= 0 ? 0 : Posted / Elapsed.TotalSeconds;

    /// <summary>
    /// How many shutters ran per second, delivered or not. Read beside
    /// <see cref="Fps"/> this is the ceiling the cadence could reach if every
    /// frame differed.
    /// </summary>
    public double ShutterHz =>
        Elapsed.TotalSeconds <= 0 ? 0 : (Posted + Suppressed + Missed + Failed) / Elapsed.TotalSeconds;

    public double KilobytesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Posted * MeanBytes / Elapsed.TotalSeconds / 1024;

    /// <summary>
    /// Whether any shutter at all ran in this window. A window of nothing is
    /// what the last call of a session that had just reported gets, and logging
    /// a line of zeroes under it would say "the stream delivered nothing",
    /// which is the opposite of what happened.
    /// </summary>
    public bool HasSamples => Posted + Suppressed + Missed + Failed > 0;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Fps:0.0} fps delivered of {ShutterHz:0.0} shutters/s, " +
        $"shutter {ShutterMedian:0.0}/{ShutterP95:0.0} ms (median/p95), " +
        $"post {PostMedian:0.0}/{PostP95:0.0} ms, " +
        $"{MeanBytes} B/frame, {KilobytesPerSecond:0.0} KB/s, " +
        $"{Posted} posted, {Suppressed} unchanged, {Missed} no-image, {Failed} refused");
}

/// <summary>
/// The stopwatch on the shutter, because nobody has ever measured this and
/// every number in the design scales with it.
///
/// It reports at least once a second while a stream is running rather than at
/// the end, for two reasons: a session that is killed by an expiry or a
/// navigation would otherwise report nothing at all, which is precisely the run
/// you most want the numbers from; and a cadence that degrades halfway through
/// - because a provider started animating something, or because the agent's
/// browser is now sharing a NAS with three other jobs - is invisible in a
/// single average over ninety seconds.
///
/// Capture and encode are one number and cannot be separated here: Playwright's
/// <c>ScreenshotAsync</c> prepares the page, takes the raster and returns
/// encoded bytes in one round trip, so "shutter" is all three. What CAN be
/// separated is the post, and separating those two is what distinguishes a slow
/// browser from a slow link - which are fixed by completely different people.
///
/// NOT thread-safe, and does not need to be: one shutter loop writes it, and
/// the session reads the last window only after that loop has finished. A lock
/// here would be measuring the cost of measuring.
/// </summary>
internal sealed class LiveViewMeter(TimeProvider time, TimeSpan interval)
{
    private readonly List<double> _shutters = [];
    private readonly List<double> _posts = [];
    private readonly long _openedAt = time.GetTimestamp();

    private long _windowStartedAt = time.GetTimestamp();
    private int _posted;
    private int _suppressed;
    private int _missed;
    private int _failed;
    private long _bytes;

    private int _totalPosted;
    private int _totalSuppressed;
    private long _totalBytes;

    public void Posted(TimeSpan shutter, TimeSpan post, int bytes)
    {
        _shutters.Add(shutter.TotalMilliseconds);
        _posts.Add(post.TotalMilliseconds);
        _posted++;
        _totalPosted++;
        _bytes += bytes;
        _totalBytes += bytes;
    }

    /// <summary>A shutter whose bytes matched the last posted frame, so nothing was sent.</summary>
    public void Suppressed(TimeSpan shutter)
    {
        _shutters.Add(shutter.TotalMilliseconds);
        _suppressed++;
        _totalSuppressed++;
    }

    /// <summary>A shutter that produced no image.</summary>
    public void Missed(TimeSpan shutter)
    {
        _shutters.Add(shutter.TotalMilliseconds);
        _missed++;
    }

    /// <summary>A frame the control plane would not take.</summary>
    public void Failed(TimeSpan shutter)
    {
        _shutters.Add(shutter.TotalMilliseconds);
        _failed++;
    }

    /// <summary>
    /// The window just closed, or null when it has not. Reading it RESETS the
    /// window, so a caller that reads and does not log has thrown the numbers
    /// away - which is why the session logs unconditionally on a non-null.
    /// </summary>
    public LiveViewWindow? TryClose()
    {
        var elapsed = time.GetElapsedTime(_windowStartedAt);
        if (elapsed < interval) return null;

        return Close(elapsed);
    }

    /// <summary>
    /// The window as it stands, whatever the clock says. For the last line a
    /// session writes, where a partial window is the only one left.
    /// </summary>
    public LiveViewWindow Close() => Close(time.GetElapsedTime(_windowStartedAt));

    /// <summary>
    /// Everything since the stream opened, for the one line that says whether
    /// this login was watchable.
    /// </summary>
    public string Total() => string.Create(
        CultureInfo.InvariantCulture,
        $"{_totalPosted} frame(s) over {time.GetElapsedTime(_openedAt).TotalSeconds:0.0} s, " +
        $"{_totalSuppressed} unchanged and not sent, {_totalBytes / 1024} KB total");

    private LiveViewWindow Close(TimeSpan elapsed)
    {
        var window = new LiveViewWindow
        {
            Elapsed = elapsed,
            Posted = _posted,
            Suppressed = _suppressed,
            Missed = _missed,
            Failed = _failed,
            ShutterMedian = Percentile(_shutters, 0.5),
            ShutterP95 = Percentile(_shutters, 0.95),
            PostMedian = Percentile(_posts, 0.5),
            PostP95 = Percentile(_posts, 0.95),
            MeanBytes = _posted == 0 ? 0 : (int)(_bytes / _posted),
        };

        _shutters.Clear();
        _posts.Clear();
        _posted = 0;
        _suppressed = 0;
        _missed = 0;
        _failed = 0;
        _bytes = 0;
        _windowStartedAt = time.GetTimestamp();

        return window;
    }

    /// <summary>
    /// Nearest-rank, and sorting a copy rather than the list.
    ///
    /// A window holds at most a couple of dozen samples, so the median of a
    /// sorted copy is free and an approximation would be a needless second way
    /// to be wrong about the one number this class exists to produce.
    /// </summary>
    private static double Percentile(List<double> samples, double fraction)
    {
        if (samples.Count == 0) return 0;

        var sorted = samples.ToArray();
        Array.Sort(sorted);

        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>Logs a window under the one message every live view uses.</summary>
internal static class LiveViewMeterLog
{
    public static void Report(this ILogger logger, string jobId, LiveViewWindow window) =>
        logger.LogInformation("job {JobId}: live view {Window}", jobId, window.ToString());
}
