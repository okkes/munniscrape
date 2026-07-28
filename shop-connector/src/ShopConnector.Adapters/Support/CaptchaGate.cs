using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Microsoft.Playwright;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// The kinds of captcha, which are different problems: one is a picture with
/// a box beside it, one is a widget's grid that can be photographed out and
/// tapped back, and one can only be handed to somebody sitting at the browser.
/// </summary>
internal enum CaptchaKind
{
    None,

    /// <summary>A widget nothing here can carry to a human who is elsewhere.</summary>
    Interactive,

    /// <summary>A picture and a box to type the answer into.</summary>
    Image,

    /// <summary>
    /// A widget showing nothing but its "I am human" box.
    ///
    /// Its own kind because clicking it is a legitimate first move and
    /// relaying it is not: there is nothing to show a human yet. It is one
    /// real click on one real control, which is what the control is for -
    /// and it is frequently the whole of what the widget asks.
    /// </summary>
    Checkbox,

    /// <summary>
    /// A widget escalated to a grid of pictures.
    ///
    /// The one interactive wall a relay can carry, and only because the
    /// answer is not a string: the picture goes out, the taps come back, and
    /// the human is the one who solved it. See
    /// <see cref="CaptchaGate.RelayGridAsync"/>.
    /// </summary>
    Grid,
}

/// <summary>Which wall is standing, and where on the page it is.</summary>
internal readonly record struct CaptchaWall(CaptchaKind Kind, CropRegion? Crop);

/// <summary>
/// Where a relayed tap lands: a click on the page, at page pixels, dispatched
/// by the browser rather than resolved through a selector.
///
/// A selector cannot reach one. A widget draws its grid inside a cross-origin
/// iframe - a different document from a different origin - and nothing on our
/// side of that boundary can address a tile in it. The mouse is not on our
/// side of it: it is the browser's own input pipeline, and a click at a point
/// is delivered to whatever is under the point. That is the entire reason the
/// human's taps can be replayed at all.
/// </summary>
internal interface ITapSurface
{
    /// <summary>
    /// Clicks each point in turn, in page (CSS) pixels - the units
    /// <see cref="Tap.ToPagePixels"/> produces from the crop the relayed
    /// picture was taken of.
    ///
    /// Ordered, because a grid that asks for tiles in sequence cannot be
    /// answered out of sequence, and because replaying what the human did in
    /// the order they did it is the only thing here that needs no
    /// justification.
    /// </summary>
    Task TapAsync(IReadOnlyList<(double X, double Y)> points, CancellationToken ct);
}

/// <summary>
/// The live browser's mouse.
///
/// Deliberately without pauses, jitter or curved travel between points: those
/// exist to make a machine look like a person, and nothing here is pretending
/// to be one. A person really did choose these points; we are only carrying
/// them back to the page they were chosen from.
/// </summary>
internal sealed class MouseTapSurface : ITapSurface
{
    private readonly IPage _page;

    public MouseTapSurface(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        _page = page;
    }

    public async Task TapAsync(IReadOnlyList<(double X, double Y)> points, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(points);

        foreach (var (x, y) in points)
        {
            ct.ThrowIfCancellationRequested();
            await _page.Mouse.ClickAsync((float)x, (float)y).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// What came of meeting a wall: a finished login, a page still worth
/// watching, or nothing this agent can do about it.
/// </summary>
internal readonly record struct CaptchaOutcome
{
    /// <summary>Nothing here can be relayed to anyone; hand the login back.</summary>
    public static CaptchaOutcome Stuck { get; } = new();

    /// <summary>The human has been asked; the redirect is still the proof.</summary>
    public static CaptchaOutcome Asked { get; } = new() { Handled = true };

    /// <summary>The wall came down and the provider redirected while we watched.</summary>
    public static CaptchaOutcome Solved(string redirect) => new() { Handled = true, Redirect = redirect };

    public bool Handled { get; private init; }

    public string? Redirect { get; private init; }
}

/// <summary>
/// One provider's contribution to the policy below: its own selectors, its
/// own patience. Everything else about meeting a wall is the same wherever it
/// stands.
/// </summary>
internal sealed record CaptchaSpec
{
    public required string ProviderId { get; init; }

    /// <summary>hCaptcha, reCAPTCHA and friends. See <see cref="CaptchaGate.DetectAsync"/>.</summary>
    public required IReadOnlyList<string> Interactive { get; init; }

    /// <summary>
    /// The widget's grid, when a provider is known to draw one somewhere a
    /// screenshot can reach it.
    ///
    /// Empty by default, and that default is the old behaviour exactly: with
    /// no way to tell a grid from the widget around it, every widget is
    /// <see cref="CaptchaKind.Interactive"/> and is met the way it always was.
    /// A provider opts in by naming the two frames, which is a claim about
    /// that provider's markup and belongs with the rest of its selectors.
    /// </summary>
    public IReadOnlyList<string> Grid { get; init; } = [];

    /// <summary>The widget's "I am human" box, on its own.</summary>
    public IReadOnlyList<string> Checkbox { get; init; } = [];

    public IReadOnlyList<string> Image { get; init; } = [];

    /// <summary>Where a typed captcha's answer goes, and what decides the kind.</summary>
    public IReadOnlyList<string> Input { get; init; } = [];

    /// <summary>Short probes: a settle loop runs them on every pass.</summary>
    public int ProbeMs { get; init; } = 500;

    /// <summary>
    /// How long to let a widget finish drawing before photographing it.
    ///
    /// Three seconds because a captcha token ages while we wait and this is
    /// spent on every relayed round, but a puzzle image on a home line is
    /// routinely slower than one poll. Nothing depends on it being enough:
    /// when it runs out the picture is taken anyway.
    /// </summary>
    public TimeSpan SettleBudget { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many gestures the relay will carry for one wall before handing it
    /// back.
    ///
    /// A widget can ask more than once - a tick, then a grid, then another
    /// grid - and a human answering a grid in two messages costs a round as
    /// well. Bounded because the alternative is a loop whose exit condition
    /// belongs to somebody else's JavaScript.
    /// </summary>
    public int RelayRounds { get; init; } = 4;

    /// <summary>How long the human has to answer a relayed image captcha.</summary>
    public int ImageSeconds { get; init; } = 300;

    /// <summary>
    /// How long the human has on an interactive widget - whether it was
    /// relayed to them as a grid of pictures or left standing in the browser
    /// window we opened in front of them. Longer than a typed captcha on
    /// purpose: the wait has to cover someone noticing a prompt on a phone in
    /// another room, or walking back to their desk.
    /// </summary>
    public int InteractiveSeconds { get; init; } = 600;

    /// <summary>How long each pass waits on the redirect before looking again.</summary>
    public int PollSeconds { get; init; } = 5;
}

/// <summary>
/// Meets a wall the human owns. Never solves one: that is the line between
/// acting as a user's agent and abuse, and nothing below moves it. What moved
/// is WHERE the human is standing when they solve it.
///
/// A widget used to mean one of two things: somebody is sitting at this
/// browser, or nobody can pass this. Both are about the agent's chair, and
/// most people connecting an account are nowhere near one - they have a phone.
/// So a grid is now photographed out through the challenge protocol that
/// already existed, the human taps it in their own app, and the taps are
/// replayed onto the page they came from. We carry the picture and the
/// gestures; the human does the seeing and the choosing, which is the part
/// that was never ours.
///
/// Shared because the decision is not provider-specific and getting it wrong
/// twice would be twice as expensive. A live Albert Heijn run died of a job
/// budget here - the redactor refused to photograph a page whose password
/// field was still full, the relay saw no bytes, and nobody was ever asked
/// anything - and both halves of that fix live below.
/// </summary>
internal sealed class CaptchaGate
{
    /// <summary>What one relayed grid came to.</summary>
    private enum GridRound
    {
        /// <summary>Nothing could be relayed, or nothing readable came back.</summary>
        Lost,

        /// <summary>The taps were replayed and the human is not finished.</summary>
        More,

        /// <summary>The taps were replayed and the human says this one is done.</summary>
        Done,
    }

    private readonly CaptchaSpec _spec;
    private readonly TimeProvider _time;

    public CaptchaGate(CaptchaSpec spec, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(spec);

        _spec = spec;
        _time = time;
    }

    /// <summary>
    /// Which wall is standing, and where on the page it is.
    ///
    /// The distinction is the whole point, and it is a distinction about the
    /// ANSWER rather than about the picture. A plain image captcha is a
    /// picture and a text box. A widget's grid is a picture and a set of
    /// taps - carryable for the same reason, once the answer stopped having
    /// to be a string. A widget with neither showing is a token minted inside
    /// somebody else's JavaScript, and there is nothing to relay to anybody:
    /// no screenshot and no typed answer passes one, however faithfully the
    /// page is photographed.
    /// </summary>
    public async Task<CaptchaWall> DetectAsync(ILoginPage page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        // Most specific first, and the order is load-bearing: the three lists
        // overlap by construction. hCaptcha's two frames differ only in the
        // fragment on one URL, and the container the provider embeds matches
        // whatever is inside it - so probing for the widget in general before
        // the grid in particular would read every escalation as the generic
        // widget it also is, and relay nothing.
        if (await page.FindAsync(_spec.Grid, _spec.ProbeMs, ct).ConfigureAwait(false) is { } grid)
        {
            return new CaptchaWall(CaptchaKind.Grid, grid.Crop);
        }

        if (await page.FindAsync(_spec.Checkbox, _spec.ProbeMs, ct).ConfigureAwait(false) is { } checkbox)
        {
            return new CaptchaWall(CaptchaKind.Checkbox, checkbox.Crop);
        }

        // Then the widget in general: hCaptcha draws pictures of its own, so
        // probing for an image before it would call a widget a photograph.
        if (await page.FindAsync(_spec.Interactive, _spec.ProbeMs, ct).ConfigureAwait(false) is { } widget)
        {
            return new CaptchaWall(CaptchaKind.Interactive, widget.Crop);
        }

        if (await page.FindAsync(_spec.Image, _spec.ProbeMs, ct).ConfigureAwait(false) is not { } picture)
        {
            return new CaptchaWall(CaptchaKind.None, null);
        }

        // A picture with a box beside it is the only captcha a photograph and
        // a typed answer can carry. One with nowhere to type the answer is as
        // far out of our reach as a widget is, so it is treated as one rather
        // than relayed to a human who would have nothing to do with it.
        var input = await page.FindAsync(_spec.Input, _spec.ProbeMs, ct).ConfigureAwait(false);

        return new CaptchaWall(input is null ? CaptchaKind.Interactive : CaptchaKind.Image, picture.Crop);
    }

    /// <summary>
    /// Meets the wall <see cref="DetectAsync"/> found, with no way to replay
    /// taps - so a widget is met the only other way there is.
    /// </summary>
    public Task<CaptchaOutcome> FaceAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CaptchaWall wall, CancellationToken ct) =>
        FaceAsync(ctx, page, watcher, wall, taps: null, ct);

    /// <summary>
    /// Meets the wall <see cref="DetectAsync"/> found.
    /// </summary>
    /// <param name="taps">
    /// Where a relayed tap lands. Null when the caller has no page to click
    /// into, which is the same situation as a provider that named no grid:
    /// there is no relay, and a widget falls back to the human at the
    /// keyboard or to a refusal.
    /// </param>
    public async Task<CaptchaOutcome> FaceAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CaptchaWall wall, ITapSurface? taps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);

        // The credentials are already upstream, so the copy still sitting in
        // the form is nothing but a liability - and the redactor, rightly,
        // refuses to photograph a page holding one. That refusal is what
        // turned a relayable captcha into a job that waited on a human nobody
        // had asked; clearing the DOM is the fix, weakening the redactor is
        // not. Before every branch, because the runner may photograph this
        // page on the way out of the ones that fail, and because a widget that
        // escalates into a grid two rounds later must not have to remember to
        // do this first.
        await page.ClearSecretsAsync(ct).ConfigureAwait(false);

        if (wall.Kind is CaptchaKind.Image)
        {
            return await RelayImageAsync(ctx, page, wall.Crop, ct).ConfigureAwait(false);
        }

        // A widget whose parts we can name, and a page to click into: the
        // human answers this one wherever they are, which is the whole reason
        // any of this exists.
        if (wall.Kind is (CaptchaKind.Grid or CaptchaKind.Checkbox) && taps is not null)
        {
            return await RelayWidgetAsync(ctx, page, watcher, taps, wall, ct).ConfigureAwait(false);
        }

        // Nobody can reach a headless browser in a pool, so there is no one to
        // ask and nothing to wait for. Saying so immediately is worth far more
        // than a hang: it is what lets a consumer tell the user to connect
        // this one from a machine they are sitting at.
        if (!ctx.Attended) throw Unrelayable();

        return await AwaitSolvedAsync(ctx, watcher, ct).ConfigureAwait(false);
    }

    private ConnectorException Unrelayable() => ConnectorException.Blocked(
        $"{_spec.ProviderId}: an interactive captcha (hcaptcha/recaptcha) stands in the login, nothing about it " +
        "could be relayed to the account's owner, and this agent is unattended; a widget that shows no grid can " +
        "only be passed in a browser a human can see and touch");

    /// <summary>
    /// Asks the human at the keyboard to pass the widget in the window
    /// already open in front of them, and then waits.
    ///
    /// Passive on purpose: there is nothing for them to type back, because
    /// the widget hands its token to the provider and not to us. So the
    /// redirect is watched throughout rather than afterwards - their solving
    /// it is what lets the provider continue, and someone who solves it may
    /// never touch the consumer's UI at all. Whichever arrives first is the
    /// answer.
    /// </summary>
    private async Task<CaptchaOutcome> AwaitSolvedAsync(
        IJobContext ctx, IRedirectWaiter watcher, CancellationToken ct)
    {
        ctx.Progress(JobStep.AwaitingHuman);

        var expiresAt = _time.GetUtcNow().AddSeconds(_spec.InteractiveSeconds);
        var poll = TimeSpan.FromSeconds(_spec.PollSeconds);

        var asked = ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.AppApproval,
            PromptKey = MessageKeys.CaptchaInBrowser,
            ExpiresAt = expiresAt,
        }, ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is { } captured)
            {
                // Nobody will ever answer the challenge now. Its expiry is
                // observed and dropped here rather than left to surface later
                // as an unobserved failure on a job that succeeded.
                _ = asked.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

                ctx.Progress(JobStep.Authenticating);
                return CaptchaOutcome.Solved(captured);
            }

            if (asked.IsCompleted)
            {
                // The human says they are through. Awaited rather than
                // ignored: an expired challenge is the platform's own error
                // and it must end the job instead of being swallowed here.
                await asked.ConfigureAwait(false);

                ctx.Progress(JobStep.Authenticating);
                return CaptchaOutcome.Asked;
            }

            // The window is up and the widget was never passed. Nothing here
            // improves with more waiting.
            if (_time.GetUtcNow() >= expiresAt) return CaptchaOutcome.Stuck;
        }
    }

    /// <summary>
    /// Carries a widget to the human who owns the account, one gesture at a
    /// time, for as long as the widget keeps asking.
    ///
    /// This is the whole change of address: the wall is the same wall, and the
    /// human solving it is the same human, but they no longer have to be
    /// sitting at the machine the browser is running on. Nothing here decides
    /// anything about a picture - it photographs one, and it replays what came
    /// back.
    ///
    /// Attended or not, deliberately: a phone in another country is a better
    /// place to answer this than a chair in front of the agent, and the
    /// account's owner is the person who should be answering it either way.
    /// </summary>
    private async Task<CaptchaOutcome> RelayWidgetAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, ITapSurface taps, CaptchaWall wall,
        CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(_spec.PollSeconds);
        var current = wall;
        var ticked = false;

        for (var round = 0; round < _spec.RelayRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            if (round > 0)
            {
                // What the last gesture left standing, measured rather than
                // assumed: a tick becomes a grid, a grid becomes another grid,
                // and either can simply be gone.
                current = await DetectAsync(page, ct).ConfigureAwait(false);

                // The wall came down. The redirect has not arrived yet, so the
                // caller's own watch is where this belongs - not another
                // photograph of a page with nothing on it.
                if (current.Kind is CaptchaKind.None)
                {
                    ctx.Progress(JobStep.Authenticating);
                    return CaptchaOutcome.Asked;
                }

                // It turned into something no relay carries.
                if (current.Kind is not (CaptchaKind.Grid or CaptchaKind.Checkbox)) break;
            }

            // Whether the provider gets a moment to move on before we look
            // again. A human who is halfway through a grid has not given it
            // anything to move on from, so that round skips the wait.
            var settle = true;

            if (current.Kind is CaptchaKind.Grid)
            {
                var relayed = await RelayGridAsync(
                    ctx, page, taps, _spec.Grid, current.Crop, ct).ConfigureAwait(false);

                if (relayed is GridRound.Lost) return CaptchaOutcome.Stuck;
                settle = relayed is GridRound.Done;
            }
            else if (!ticked)
            {
                // One real click on one real control - the gesture the widget
                // exists to receive, and frequently the whole of what it asks.
                // Once, ever: the box stays on the page after it is ticked, so
                // a second pass would find it again and undo what the first
                // one achieved.
                if (!await page.ClickAsync(_spec.Checkbox, _spec.ProbeMs, ct).ConfigureAwait(false))
                {
                    return CaptchaOutcome.Stuck;
                }

                ticked = true;
            }
            else
            {
                // The tick did not take. A click on an element lands in the
                // middle of it, and the middle of hCaptcha's checkbox frame is
                // the words beside the box rather than the box - so this is a
                // miss we should expect rather than a surprise.
                //
                // The answer is not to guess how far left the box is: an
                // offset measured off one screenshot of one version of
                // somebody else's widget is wrong the week they restyle it,
                // and it is wrong invisibly. The frame is a picture like any
                // other, so it is relayed like any other and the human taps
                // their own box.
                var relayed = await RelayGridAsync(
                    ctx, page, taps, _spec.Checkbox, current.Crop, ct).ConfigureAwait(false);

                if (relayed is GridRound.Lost) return CaptchaOutcome.Stuck;
                settle = relayed is GridRound.Done;
            }

            // Every gesture may have been the last one the provider needed,
            // and the redirect is the only proof of that.
            if (settle && await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is { } captured)
            {
                ctx.Progress(JobStep.Authenticating);
                return CaptchaOutcome.Solved(captured);
            }
        }

        // The rounds ran out with the widget still standing. Whoever is at the
        // browser is the only one who can pass it now - and if nobody is,
        // saying so beats waiting for a redirect that is not coming.
        if (!ctx.Attended) throw Unrelayable();

        return await AwaitSolvedAsync(ctx, watcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One picture the widget drew: photographed out, tapped back, replayed
    /// onto the page.
    ///
    /// The picture is the widget's own, and so is the answer - we neither read
    /// it nor score it nor keep it. What happens here is bookkeeping about
    /// pixels: which rectangle was photographed, and where in that rectangle
    /// the human put their fingers.
    /// </summary>
    /// <param name="where">
    /// The selectors the rectangle is measured again with just before the taps
    /// are dispatched - the grid's, or the checkbox frame's when that is what
    /// was relayed. It must be the list <paramref name="crop"/> came from, or
    /// the answer is placed on a different rectangle from the one the human
    /// was looking at.
    /// </param>
    private async Task<GridRound> RelayGridAsync(
        IJobContext ctx, ILoginPage page, ITapSurface taps, IReadOnlyList<string> where, CropRegion? crop,
        CancellationToken ct)
    {
        // A grid with no measurable box cannot be photographed on its own and
        // its answer cannot be placed back on the page. Relaying the whole
        // viewport instead would put a question in front of a human that no
        // answer of theirs could be acted on, which is the one thing this gate
        // never does.
        if (crop is null) return GridRound.Lost;

        ctx.Progress(JobStep.AwaitingHuman);

        // Let the widget finish drawing before photographing it. A tap makes
        // hCaptcha clear its panel and fetch the next puzzle, and the picture
        // taken in that gap is of a spinner - which the account's owner is
        // then asked to solve, on a clock. Best effort by construction: this
        // returns either way and the shutter follows regardless, so a widget
        // that never settles costs one budget and nothing else.
        await page.SettleAsync(where, _spec.SettleBudget, ct).ConfigureAwait(false);

        // The crop is what lets the redactor drop the rest of the page rather
        // than merely obscure it - and here it is also the frame the answer
        // will be measured against, so the two cannot be allowed to differ.
        var png = await ctx.Browser.ScreenshotAsync(crop, ct).ConfigureAwait(false);

        // No bytes means the redactor refused - it declines whatever it cannot
        // verify, not only a page still holding a secret. There is no honest
        // way on from here: asking somebody to tap a picture they cannot see
        // is worse than admitting we are stuck, and clicking a grid nobody
        // looked at is the line this design does not cross.
        if (png.Length == 0)
        {
            throw ConnectorException.Blocked(
                $"{_spec.ProviderId}: the captcha could not be photographed, so there was nothing to show the " +
                "account's owner and nothing they could answer");
        }

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.Image,
            // The one field that makes this relayable. Everything else about
            // the challenge is what a picture captcha has always been; what
            // changed is that the answer is a set of points rather than a
            // string, and a consumer has to know that to draw the right thing.
            AnswerKind = ChallengeAnswerKind.Taps,
            // The same key as any other captcha, on purpose: this is not a
            // different question, it is the same question answered with a
            // finger. Which control to draw is answer_kind's job, and saying
            // it twice is how the two come to disagree.
            PromptKey = MessageKeys.Captcha,
            Image = png,
            Crop = crop,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_spec.InteractiveSeconds),
        }, ct).ConfigureAwait(false);

        // A consumer that ignored answer_kind drew a text box over the picture
        // and sent back whatever was typed into it. Nothing is clicked on a
        // string we cannot read: a tap inferred from prose is a click at a
        // point no human chose.
        if (!TapAnswer.TryParse(answer.Value, out var tapped)) return GridRound.Lost;

        // Measured again, now. The crop above was taken before the human spent
        // however long looking at it, and a widget that scrolled or re-laid
        // itself out in the meantime would take every tap at the difference -
        // which looks exactly like a coordinate bug and is not one.
        if ((await page.FindAsync(where, _spec.ProbeMs, ct).ConfigureAwait(false))?.Crop is not { } region)
        {
            // The grid was there when the picture was taken and is gone - or
            // no longer measurable - now, so the taps have nowhere to land and
            // the page has moved on without us.
            return GridRound.Lost;
        }

        await taps.TapAsync([.. tapped.Taps.Select(tap => tap.ToPagePixels(region))], ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Authenticating);

        // The marker, and the only thing it can honestly mean here: a widget
        // that draws its own verify button draws it inside the picture we
        // relayed, so the human's last tap is what presses it - there is no
        // control on our side of the iframe for us to press instead. What the
        // marker tells us is whether they are finished, which decides whether
        // the next round waits for the provider or goes straight back with
        // another picture.
        return tapped.Submit ? GridRound.Done : GridRound.More;
    }

    /// <summary>
    /// Relays the one captcha a relay can carry: a picture, and a box to type
    /// the answer into. Attended or not - a photograph and a typed answer
    /// reach the account's owner wherever they are.
    /// </summary>
    private async Task<CaptchaOutcome> RelayImageAsync(
        IJobContext ctx, ILoginPage page, CropRegion? crop, CancellationToken ct)
    {
        ctx.Progress(JobStep.AwaitingHuman);

        // The crop is what lets the redactor drop the rest of the page rather
        // than merely obscure it.
        var png = await ctx.Browser.ScreenshotAsync(crop, ct).ConfigureAwait(false);

        // No bytes means the redactor refused anyway - it declines whatever it
        // cannot verify, not only a page still holding a secret. Asking a
        // human to solve a picture they cannot see is worse than admitting we
        // are stuck.
        if (png.Length == 0) return CaptchaOutcome.Stuck;

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.Image,
            PromptKey = MessageKeys.Captcha,
            Image = png,
            Crop = crop,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_spec.ImageSeconds),
        }, ct).ConfigureAwait(false);

        // Only the captcha is answered. The credentials are deliberately not
        // resubmitted to make the form look answered: a second submission of a
        // password that may already have counted is how an account gets
        // locked.
        if (!await page.AnswerAsync(_spec.Input, answer.Value, _spec.ProbeMs, ct).ConfigureAwait(false))
        {
            // The box was there when the wall was classified and is gone now,
            // so the answer has nowhere to go and the page has moved on
            // without us.
            return CaptchaOutcome.Stuck;
        }

        ctx.Progress(JobStep.Authenticating);
        return CaptchaOutcome.Asked;
    }
}
