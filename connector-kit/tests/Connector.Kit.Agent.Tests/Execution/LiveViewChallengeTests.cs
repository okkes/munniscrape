using Connector.Kit.Challenges;

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
}
