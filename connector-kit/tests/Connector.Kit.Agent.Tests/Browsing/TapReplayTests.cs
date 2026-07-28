using Connector.Kit.Agent.Browsing;
using Connector.Kit.Challenges;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// Where a relayed tap lands, and everything that must NOT be clicked.
///
/// These are the tests that stand between a human tapping a captcha tile on
/// their phone and this agent clicking somewhere else on a live login page. The
/// arithmetic is one line; every other test here is about a refusal.
/// </summary>
public class TapReplayTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(180);

    [Fact]
    public async Task A_region_at_the_origin_maps_a_fraction_to_the_same_fraction_of_the_page()
    {
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(0, 0, 300, 300));

        var clicks = await ReplayAsync(pointer, capture, [new Tap(0.5, 0.5)]);

        Assert.Equal(1, clicks);
        Assert.Equal((150d, 150d), Assert.Single(pointer.Clicks));
    }

    [Fact]
    public async Task A_region_at_an_offset_carries_its_origin_into_every_tap()
    {
        // The bug this exists to catch: a mapping that forgets the crop's
        // origin produces perfectly valid coordinates that click the top-left
        // of the viewport instead of the captcha.
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(120, 340, 300, 180));

        var clicks = await ReplayAsync(pointer, capture,
            [new Tap(0, 0), new Tap(0.5, 0.5), new Tap(1, 1)]);

        Assert.Equal(3, clicks);
        Assert.Equal([(120d, 340d), (270d, 430d), (420d, 520d)], pointer.Clicks);
    }

    [Fact]
    public async Task The_corners_of_the_image_are_the_corners_of_the_box()
    {
        var pointer = new RecordingPointer();
        var region = new CropRegion(17, 43, 400, 250);

        await ReplayAsync(pointer, RegionCapture.Of(Png, region),
            [new Tap(0, 0), new Tap(1, 0), new Tap(0, 1), new Tap(1, 1)]);

        Assert.Equal(
            [(17d, 43d), (417d, 43d), (17d, 293d), (417d, 293d)],
            pointer.Clicks);
    }

    [Fact]
    public async Task Taps_are_replayed_in_the_order_the_human_made_them()
    {
        // Some grids ask for tiles in sequence, and the order is information
        // the client already had and cannot be recovered once dropped.
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(0, 0, 100, 100));

        await ReplayAsync(pointer, capture,
            [new Tap(0.9, 0.1), new Tap(0.1, 0.1), new Tap(0.5, 0.9)]);

        Assert.Equal([(90d, 10d), (10d, 10d), (50d, 90d)], pointer.Clicks);
    }

    [Fact]
    public async Task A_gap_separates_the_taps_and_neither_opens_nor_closes_the_replay()
    {
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(0, 0, 100, 100));

        await ReplayAsync(pointer, capture,
            [new Tap(0.1, 0.1), new Tap(0.2, 0.2), new Tap(0.3, 0.3)]);

        Assert.Equal(
            ["click 10,10", "pause 180", "click 20,20", "pause 180", "click 30,30"],
            pointer.Trace);
        Assert.All(pointer.Pauses, gap => Assert.Equal(Gap, gap));
    }

    [Fact]
    public async Task One_tap_waits_for_nothing()
    {
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(0, 0, 100, 100));

        await ReplayAsync(pointer, capture, [new Tap(0.5, 0.5)]);

        Assert.Empty(pointer.Pauses);
    }

    [Fact]
    public async Task An_answer_with_no_taps_clicks_nothing()
    {
        // A legal answer: "tap.v1:" is an answer that contains no taps, not a
        // missing one, and it must not be turned into a click somewhere.
        Assert.True(TapAnswer.TryParse("tap.v1:", out var answer));

        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(0, 0, 100, 100));

        var clicks = await ReplayAsync(pointer, capture, answer.Taps);

        Assert.Equal(0, clicks);
        Assert.Empty(pointer.Trace);
    }

    [Fact]
    public async Task A_refused_capture_produces_no_clicks()
    {
        // The redactor's refusal arriving at the mouse. No image ever left this
        // machine, so nobody saw anything, so nothing here is an answer to it.
        var pointer = new RecordingPointer();

        var clicks = await ReplayAsync(pointer, RegionCapture.Refused, [new Tap(0.5, 0.5)]);

        Assert.Equal(0, clicks);
        Assert.Empty(pointer.Trace);
    }

    [Fact]
    public void A_refusal_is_not_a_black_image()
    {
        // Masking and refusing are different outcomes and must not be confused:
        // a masked capture is a real PNG with a real box that a human can still
        // answer around, and a refusal has no bytes at all.
        var refused = RegionCapture.Refused;
        Assert.False(refused.Captured);
        Assert.Empty(refused.Png);
        Assert.Equal(default, refused.Region);

        var masked = RegionCapture.Of(Png, new CropRegion(4, 8, 40, 20));
        Assert.True(masked.Captured);
        Assert.NotEmpty(masked.Png);
        Assert.Equal(new CropRegion(4, 8, 40, 20), masked.Region);
    }

    [Fact]
    public async Task A_coordinate_outside_the_image_never_becomes_a_tap()
    {
        // Rejected at the two places it could arrive, both of them before any
        // page pixel is computed: the type refuses to hold it, and the parser
        // refuses to produce one. What reaches the mouse is only ever a
        // fraction of the picture the human was actually shown.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tap(1.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tap(0.5, -0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tap(double.NaN, 0.5));

        Assert.False(TapAnswer.TryParse("tap.v1:1.5,0.5", out _));

        // And a consumer that ignored answer_kind and sent typed text is simply
        // not a tap answer, so nothing is clicked.
        Assert.False(TapAnswer.TryParse("7Gk2p", out var typed));
        Assert.Null(typed);

        var pointer = new RecordingPointer();
        await ReplayAsync(pointer, RegionCapture.Of(Png, new CropRegion(0, 0, 100, 100)), []);
        Assert.Empty(pointer.Trace);
    }

    [Fact]
    public async Task A_box_that_no_longer_fits_the_viewport_is_refused_whole()
    {
        // The page scrolled or re-laid itself since the capture. The taps are
        // answers to a picture of somewhere else, and the coordinates now point
        // at whatever has moved underneath them.
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(700, 10, 400, 200));

        var clicks = await ReplayAsync(pointer, capture,
            [new Tap(0.1, 0.1), new Tap(0.9, 0.9)], viewport: (800, 600));

        Assert.Equal(0, clicks);
        Assert.Empty(pointer.Trace);
    }

    [Fact]
    public async Task A_box_flush_with_the_viewport_edge_is_still_reachable()
    {
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(400, 300, 400, 300));

        var clicks = await ReplayAsync(pointer, capture, [new Tap(0.5, 0.5)], viewport: (800, 600));

        Assert.Equal(1, clicks);
        Assert.Equal((600d, 450d), Assert.Single(pointer.Clicks));
    }

    [Fact]
    public async Task No_viewport_means_no_viewport_check()
    {
        // A headed context can run at the window's own size and report none.
        // Inventing one would refuse taps that are perfectly reachable.
        var pointer = new RecordingPointer();
        var capture = RegionCapture.Of(Png, new CropRegion(1600, 900, 200, 200));

        var clicks = await ReplayAsync(pointer, capture, [new Tap(0.5, 0.5)]);

        Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task Cancellation_stops_the_replay_rather_than_finishing_it()
    {
        var pointer = new RecordingPointer();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReplayAsync(
            pointer,
            RegionCapture.Of(Png, new CropRegion(0, 0, 100, 100)),
            [new Tap(0.5, 0.5)],
            ct: cancelled.Token));

        Assert.Empty(pointer.Trace);
    }

    [Fact]
    public async Task A_lease_with_no_browser_photographs_nothing_and_clicks_nothing()
    {
        // The real class, on the path a job takes when no browser was ever
        // started: every answer is a refusal, and none of them is an error.
        await using var lease = new BrowserLease(
            new BrowserLeaseOptions(),
            new ScreenshotRedactor(TestRig.Manifest, NullLogger.Instance),
            NullLogger.Instance);

        Assert.False(lease.Started);
        Assert.False((await lease.CaptureRegionAsync(new CropRegion(0, 0, 10, 10), default)).Captured);
        Assert.False((await lease.CaptureElementAsync("iframe[src*='hcaptcha']", default)).Captured);
        Assert.Empty(await lease.ScreenshotAsync(null, default));
        Assert.Equal(0, await lease.ReplayTapsAsync([new Tap(0.5, 0.5)], RegionCapture.Refused, default));
    }

    [Fact]
    public void The_gap_between_taps_is_configurable_and_present_by_default()
    {
        Assert.Equal(TapReplay.DefaultGap, new BrowserLeaseOptions().TapGap);
        Assert.True(new BrowserLeaseOptions().TapGap > TimeSpan.Zero);
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            (new BrowserLeaseOptions() with { TapGap = TimeSpan.FromMilliseconds(50) }).TapGap);
    }

    private static Task<int> ReplayAsync(
        RecordingPointer pointer,
        RegionCapture capture,
        IReadOnlyList<Tap> taps,
        (int Width, int Height)? viewport = null,
        CancellationToken ct = default) =>
        TapReplay.ReplayAsync(taps, capture, viewport, pointer, Gap, NullLogger.Instance, ct);
}
