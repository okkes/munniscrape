using Connector.Kit.Agent.Execution;
using Connector.Kit.Challenges;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// What the platform does with a <see cref="ChallengeType.LiveView"/>, at the
/// one place it decides: <c>AgentJobContext.AskAsync</c>.
///
/// The adapter is not consulted about any of this. It raises a challenge and
/// waits, exactly as it would for an SMS code, and the stream is opened and
/// closed around it - which is the point, because an adapter author who never
/// touches the stream cannot forget to stop it.
/// </summary>
public class LiveViewChallengeTests
{
    /// <summary>
    /// Not one live-view pixel reaches the challenge row.
    ///
    /// The raise payload's image is stored for the length of the job, and
    /// "a live view frame is never stored" is what makes the custody claim
    /// true rather than aspirational. Asserted with an adapter that OFFERS an
    /// image, because the interesting failure is the polite one: an adapter
    /// helpfully attaching a first frame so the human has something to look at
    /// while the stream opens.
    /// </summary>
    [Fact]
    public async Task A_live_view_never_carries_a_stored_image()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges());
        await using var context = rig.Context(TestRig.Login(budgetSeconds: 30));

        rig.Control.Answer(string.Empty);

        await context.AskAsync(
            new Challenge
            {
                Type = ChallengeType.LiveView,
                PromptKey = "connect.challenge.live_login",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
                Image = [1, 2, 3, 4],
                Crop = new CropRegion(0, 0, 390, 844),
            },
            CancellationToken.None);

        Assert.Null(rig.Control.Raised!.ImageBase64);
    }

    /// <summary>
    /// The still relay is untouched by all of this: an image challenge still
    /// carries the adapter's bytes. Here so that the refusal above is read as
    /// "for a live view" and not as "for everything".
    /// </summary>
    [Fact]
    public async Task An_image_challenge_still_carries_its_picture()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges());
        await using var context = rig.Context(TestRig.Login(budgetSeconds: 30));

        rig.Control.Answer("solved");

        await context.AskAsync(
            new Challenge
            {
                Type = ChallengeType.Image,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
                Image = [1, 2, 3, 4],
            },
            CancellationToken.None);

        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), rig.Control.Raised!.ImageBase64);
    }

    /// <summary>
    /// A live view raised with no browser running is a challenge that waits,
    /// not a job that fails.
    ///
    /// The stream is an optimisation on top of a passive challenge, so every
    /// way of failing to open one has to degrade to the wait the consumer would
    /// have got anyway. A live view that could take a job down with it would be
    /// a frame-pipeline defect turning into a provider outage.
    /// </summary>
    [Fact]
    public async Task A_live_view_with_no_browser_waits_instead_of_failing()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges());
        await using var context = rig.Context(TestRig.Login(budgetSeconds: 30));

        rig.Control.Answer(string.Empty);

        var answer = await context.AskAsync(
            new Challenge
            {
                Type = ChallengeType.LiveView,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
            },
            CancellationToken.None);

        Assert.Equal("chl_test", answer.ChallengeId);
        Assert.Empty(rig.Control.Frames);
    }

    /// <summary>
    /// Two live views in one job, and one frame count between them.
    ///
    /// This is the only place the defect was ever visible, because it is the
    /// only place a second session is built: <c>AskAsync</c> opens a new one
    /// every time it is handed a live view, and the counter used to start inside
    /// it. The wire says the sequence is monotonic per JOB, and the connector
    /// enforces that by discarding any frame not numbered higher than the one in
    /// its slot - while answering 200, so the agent carried on posting into a
    /// bin. What the human saw was the last picture of the previous step,
    /// indefinitely, and what they did with it was type a password into a stale
    /// login page.
    ///
    /// A real browser, because the frame has to be real: the counter only means
    /// anything on a picture that was actually taken and actually posted.
    /// </summary>
    [Fact]
    public async Task Two_live_views_in_one_job_share_one_frame_count()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges());
        await using var context = rig.Context(TestRig.Login(budgetSeconds: 60));

        // A real navigation to a real host, fulfilled locally. The stream pins
        // to the origin the page is on when it opens, and about:blank has none.
        var page = await context.Browser.PageAsync(CancellationToken.None);
        await page.RouteAsync("**/*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html",
            Body = LiveFrameTests.LoginForm(),
        }));

        await page.GotoAsync("https://provider.test/login");

        await OneLiveViewAsync(rig, context);
        var afterFirst = rig.Control.Frames.Count;
        await OneLiveViewAsync(rig, context);

        Assert.True(rig.Control.Frames.Count > afterFirst, "the second live view posted nothing at all");

        // The whole assertion. Not "the numbers look plausible" - the connector's
        // own rule, applied to what actually arrived.
        Assert.Equal(0, rig.Control.DiscardedFrames);
    }

    /// <summary>
    /// Raises one live view, waits for it to photograph something, and answers
    /// it - which is how the platform closes a stream.
    /// </summary>
    private static async Task OneLiveViewAsync(TestRig rig, AgentJobContext context)
    {
        var already = rig.Control.Frames.Count;
        rig.Control.SaysNothingYet();

        var asked = Task.Run(() => context.AskAsync(
            new Challenge
            {
                Type = ChallengeType.LiveView,
                PromptKey = "connect.challenge.live_login",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60),
            },
            CancellationToken.None));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (rig.Control.Frames.Count <= already)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "timed out waiting for a live frame");
            await Task.Delay(25);
        }

        rig.Control.Answer(string.Empty);
        await asked;
    }
}
