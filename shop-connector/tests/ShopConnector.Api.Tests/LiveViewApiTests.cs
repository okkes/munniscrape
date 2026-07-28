using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Connector.Kit;
using Connector.Kit.Challenges;
using Connector.Kit.Hosting.Auth;
using Connector.Kit.Hosting.Challenges;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Hosting.Live;
using Connector.Kit.Jobs;
using Connector.Kit.Sessions;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// The streamed login's transport, driven through the real host.
///
/// A live view is the provider's own login page, photographed on the agent
/// several times a second and shown to the account's owner, who types their
/// password into the picture. The connector's whole job in that is to hold the
/// newest photograph and to carry the pointer and the keys back - and the
/// reason the feature exists at all is that neither of those things is ever
/// written down. A frame in a table would put the provider's authenticated
/// pixels at rest in the control plane, which is exactly the custody problem
/// streaming was built to remove, and a keystroke in a column would be the
/// password we went to this trouble not to have.
///
/// So these tests are about four properties, in this order of importance:
/// nothing reaches the database; nobody but the session's holder and the job's
/// leaseholder can reach either leg; the newest picture wins over a backlog of
/// stale ones; and a full input queue gives up stale pointer positions rather
/// than the click somebody just made.
///
/// The provider is an agent-tier one on purpose. <c>InlineJobRunner</c> serves
/// only manifests it can run in-process, so a job staged here for a browser
/// tier stays exactly where it was put instead of racing the in-process pump.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class LiveViewApiTests(ShopApiFactory factory)
{
    private const string Provider = "ah";

    /// <summary>JPEG's start-of-image marker. Bytes that survive the channel are bytes a phone can draw.</summary>
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    /// <summary>390x844 at deviceScaleFactor 1 - a phone, which is what the agent renders at.</summary>
    private const string PhoneSize = "390x844";

    // ------------------------------------------------------------------ frames

    [Fact]
    public async Task A_frame_the_agent_posts_is_served_to_the_consumer_with_its_sequence_and_its_shape()
    {
        var run = await ArrangeAsync();
        using var agent = AgentClient(run);
        using var http = factory.CreateAuthorizedClient();

        using (var posted = await PostFrameAsync(agent, run.JobId, sequence: 1, Frame(0x11)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        using var frame = await http.GetAsync(FrameUrl(run, after: 0));

        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        Assert.Equal("image/jpeg", frame.ContentType()?.MediaType);
        Assert.Equal(Frame(0x11), await frame.Content.ReadAsByteArrayAsync());

        // The shape travels with the bytes or the consumer cannot turn a tap on
        // its own screen back into a fraction of the picture.
        Assert.Equal("1", Assert.Single(frame.RawHeader(LiveHeaders.Sequence)));
        Assert.Equal(PhoneSize, Assert.Single(frame.RawHeader(LiveHeaders.Size)));

        // A photograph of somebody's login page is not something a proxy, a
        // browser or a disk cache has any business keeping.
        Assert.Equal("no-store", frame.Headers.CacheControl?.ToString());
    }

    /// <summary>
    /// One slot, not a queue, and 204 rather than a picture the consumer is
    /// already showing.
    ///
    /// Both halves are the same decision. A consumer whose connection stalled
    /// for a second wants the login as it is now - it is about to be typed
    /// into - and re-sending the frame it already has would spend its bandwidth
    /// to change nothing on its screen.
    /// </summary>
    [Fact]
    public async Task The_slot_holds_only_the_newest_picture_and_a_caller_that_has_seen_it_is_told_so()
    {
        var run = await ArrangeAsync();
        using var agent = AgentClient(run);
        using var http = factory.CreateAuthorizedClient();

        foreach (var sequence in new long[] { 1, 2, 3 })
        {
            using var posted = await PostFrameAsync(agent, run.JobId, sequence, Frame((byte)sequence));
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        using (var caught = await http.GetAsync(FrameUrl(run, after: 0)))
        {
            Assert.Equal(HttpStatusCode.OK, caught.StatusCode);
            Assert.Equal("3", Assert.Single(caught.RawHeader(LiveHeaders.Sequence)));
            Assert.Equal(Frame(3), await caught.Content.ReadAsByteArrayAsync());
        }

        // Nothing newer. This one really does hang for the poll window, which
        // is the behaviour being asserted: the wait is what makes the stream
        // arrive as it happens instead of on the consumer's timer.
        using var nothing = await http.GetAsync(FrameUrl(run, after: 3));
        Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode);
    }

    /// <summary>
    /// Two POSTs can overtake each other on a mobile uplink. A picture from the
    /// past replacing the present would show the human the login as it was -
    /// with the cursor, the error banner and the field contents of that moment -
    /// and they would act on it.
    /// </summary>
    [Fact]
    public async Task A_frame_that_arrived_out_of_order_never_replaces_a_newer_one()
    {
        var run = await ArrangeAsync();
        using var agent = AgentClient(run);
        using var http = factory.CreateAuthorizedClient();

        using (var newest = await PostFrameAsync(agent, run.JobId, sequence: 5, Frame(0x55)))
        {
            Assert.Equal(HttpStatusCode.OK, newest.StatusCode);
        }

        // Accepted, and discarded. A late frame is the network's doing, not the
        // agent's, and answering with an error would make it retry a photograph
        // that is already out of date.
        using (var late = await PostFrameAsync(agent, run.JobId, sequence: 4, Frame(0x44)))
        {
            Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        }

        using var frame = await http.GetAsync(FrameUrl(run, after: 0));

        Assert.Equal("5", Assert.Single(frame.RawHeader(LiveHeaders.Sequence)));
        Assert.Equal(Frame(0x55), await frame.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// A poll already in flight when the shutter fires gets that frame. Without
    /// it the first picture of every login waits for the consumer's next
    /// request, which is the difference between a live view and a slideshow.
    /// </summary>
    [Fact]
    public async Task A_poll_waiting_on_an_empty_slot_is_answered_by_the_frame_that_arrives_during_it()
    {
        var run = await ArrangeAsync();
        using var agent = AgentClient(run);
        using var http = factory.CreateAuthorizedClient();

        var waiting = http.GetAsync(FrameUrl(run, after: 0));
        await Task.Delay(150);

        using (var posted = await PostFrameAsync(agent, run.JobId, sequence: 9, Frame(0x99)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        using var frame = await waiting;

        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        Assert.Equal("9", Assert.Single(frame.RawHeader(LiveHeaders.Sequence)));
    }

    [Fact]
    public async Task A_frame_without_a_sequence_or_a_shape_is_refused_rather_than_guessed_at()
    {
        var run = await ArrangeAsync();
        using var agent = AgentClient(run);

        using (var noSequence = await PostRawFrameAsync(agent, run.JobId, sequence: null, PhoneSize, Jpeg))
        {
            await ErrorEnvelope.AssertAsync(noSequence, HttpStatusCode.BadRequest, "invalid_request");
        }

        using (var noSize = await PostRawFrameAsync(agent, run.JobId, "7", size: null, Jpeg))
        {
            await ErrorEnvelope.AssertAsync(noSize, HttpStatusCode.BadRequest, "invalid_request");
        }

        using (var nonsense = await PostRawFrameAsync(agent, run.JobId, "7", "three-nineties", Jpeg))
        {
            await ErrorEnvelope.AssertAsync(nonsense, HttpStatusCode.BadRequest, "invalid_request");
        }

        using (var empty = await PostRawFrameAsync(agent, run.JobId, "7", PhoneSize, []))
        {
            await ErrorEnvelope.AssertAsync(empty, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.Null(Channel.Frame(run.JobId));
    }

    // ------------------------------------------------------------------- input

    /// <summary>
    /// The events go one way, exactly once each, and a poll that says what it
    /// has already taken is not handed it again.
    /// </summary>
    [Fact]
    public async Task What_the_human_did_reaches_the_agent_and_a_later_poll_gets_only_what_came_after()
    {
        var run = await ArrangeAsync();
        using var agent = AgentClient(run);
        using var http = factory.CreateAuthorizedClient();

        using (var accepted = await PostInputAsync(http, run, MoveEvent(1, 0.25, 0.5), DownEvent(2, 0.25, 0.5)))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using (var collected = await agent.GetAsync(InputUrl(run.JobId, after: 0)))
        {
            Assert.Equal(HttpStatusCode.OK, collected.StatusCode);

            var events = (await collected.JsonAsync()).GetProperty("events").EnumerateArray().ToList();
            Assert.Equal(2, events.Count);

            // Order is information the human made and nothing downstream can
            // recover: a click replayed before the move that positioned it
            // lands wherever the pointer happened to be.
            Assert.Equal("move", events[0].Text("kind"));
            Assert.Equal("down", events[1].Text("kind"));
            Assert.Equal(0.25, events[0].GetProperty("x").GetDouble());
            Assert.Equal(2, events[1].GetProperty("sequence").GetInt64());
        }

        // A poll naming what it has, made before anything new happens, and then
        // the human types. It gets the keystroke and not the two events it has
        // already replayed into the page.
        var next = agent.GetAsync(InputUrl(run.JobId, after: 2));
        await Task.Delay(150);

        using (var typed = await PostInputAsync(http, run, TextEvent(3, "hunter2"), KeyEvent(4, "enter")))
        {
            Assert.Equal(HttpStatusCode.Accepted, typed.StatusCode);
        }

        using var following = await next;
        var later = (await following.JsonAsync()).GetProperty("events").EnumerateArray().ToList();

        Assert.Equal(2, later.Count);
        Assert.Equal("text", later[0].Text("kind"));
        Assert.Equal("hunter2", later[0].Text("text"));
        Assert.Equal("enter", later[1].Text("key"));
    }

    /// <summary>
    /// The batch is judged once, at the edge, and a batch that fails leaves
    /// nothing behind. Everything further in - the queue, the agent's poll, the
    /// dispatcher that touches a live browser - is entitled to assume what it
    /// holds is answerable, and a second opinion deeper in would be a second
    /// definition of what a legal gesture is.
    /// </summary>
    [Fact]
    public async Task A_batch_that_is_not_answerable_is_refused_whole_and_queues_nothing()
    {
        var run = await ArrangeAsync();
        using var http = factory.CreateAuthorizedClient();

        // Off the picture. Refused rather than clamped: a clamped tap is a
        // click somewhere the human did not choose.
        using (var offPicture = await PostInputAsync(http, run, new { kind = "move", x = 1.5, y = 0.5, sequence = 1 }))
        {
            await ErrorEnvelope.AssertAsync(offPicture, HttpStatusCode.BadRequest, "invalid_request");
        }

        // A control character is how "typed text" becomes something else - a
        // newline that submits a form the human was still filling in.
        using (var control = await PostInputAsync(http, run, new { kind = "text", text = "hunter2\n", sequence = 2 }))
        {
            await ErrorEnvelope.AssertAsync(control, HttpStatusCode.BadRequest, "invalid_request");
        }

        using (var nothing = await PostInputAsync(http, run))
        {
            await ErrorEnvelope.AssertAsync(nothing, HttpStatusCode.BadRequest, "invalid_request");
        }

        // One good event in a bad batch does not get through: a gesture is
        // dispatched whole or refused whole, never half.
        using (var mixed = await PostInputAsync(http, run,
                   DownEvent(3, 0.5, 0.5), new { kind = "move", x = -0.1, y = 0.5, sequence = 4 }))
        {
            await ErrorEnvelope.AssertAsync(mixed, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.Empty(Channel.Collect(run.JobId, long.MinValue).Events);
    }

    /// <summary>
    /// What gives way when the queue is full, and it is not symmetric.
    ///
    /// A pointer position the human has already moved past is worth nothing; a
    /// click is something they chose to do exactly once. Bounding the queue the
    /// obvious way - refuse the newest - would throw away the tap they just
    /// made in favour of thirty stale hover positions.
    /// </summary>
    [Fact]
    public async Task A_full_queue_gives_up_stale_pointer_moves_before_it_gives_up_a_click()
    {
        var run = await ArrangeAsync();
        using var agent = AgentClient(run);
        using var http = factory.CreateAuthorizedClient();

        // The click is the OLDEST event in the queue, so "drop the oldest"
        // would take it and this test would be the one to notice.
        using (var clicked = await PostInputAsync(http, run, DownEvent(1, 0.5, 0.5)))
        {
            Assert.Equal(HttpStatusCode.Accepted, clicked.StatusCode);
        }

        var sequence = 2L;
        var moves = LiveChannel.MaxQueuedEvents + LiveInputBatch.MaxEvents;

        while (sequence <= moves)
        {
            var batch = Enumerable.Range(0, LiveInputBatch.MaxEvents)
                .Select(_ => MoveEvent(sequence++, 0.5, 0.5))
                .ToArray();

            using var flood = await PostInputAsync(http, run, batch);
            Assert.Equal(HttpStatusCode.Accepted, flood.StatusCode);
        }

        var drained = await DrainAsync(agent, run.JobId, LiveChannel.MaxQueuedEvents);

        Assert.Equal(LiveChannel.MaxQueuedEvents, drained.Count);
        Assert.Equal(1, drained.Count(e => string.Equals(e.Text("kind"), "down", StringComparison.Ordinal)));
        Assert.Equal(1, drained.Single(e => string.Equals(e.Text("kind"), "down", StringComparison.Ordinal))
            .GetProperty("sequence").GetInt64());
    }

    // ----------------------------------------------------------- authorisation

    [Fact]
    public async Task A_challenge_that_is_not_a_live_view_is_refused_on_both_legs()
    {
        var run = await ArrangeAsync(ChallengeType.Image);
        using var http = factory.CreateAuthorizedClient();

        using (var frame = await http.GetAsync(FrameUrl(run, after: 0)))
        {
            await ErrorEnvelope.AssertAsync(frame, HttpStatusCode.BadRequest, "unsupported_resource");
        }

        using var input = await PostInputAsync(http, run, DownEvent(1, 0.5, 0.5));
        await ErrorEnvelope.AssertAsync(input, HttpStatusCode.BadRequest, "unsupported_resource");
    }

    /// <summary>
    /// A live view that is over serves no further pixels and takes no further
    /// keys - by the clock, or because the adapter's own success signal settled
    /// it. That second one is where the agent's latch stops being a property of
    /// one loop on one machine: after it, nothing gets through here whatever
    /// the shutter is still doing.
    /// </summary>
    [Fact]
    public async Task A_live_view_that_is_over_serves_nothing_and_accepts_nothing()
    {
        var expired = await ArrangeAsync();
        await ExpireAsync(expired.ChallengeId);

        using var http = factory.CreateAuthorizedClient();

        using (var frame = await http.GetAsync(FrameUrl(expired, after: 0)))
        {
            await ErrorEnvelope.AssertAsync(frame, HttpStatusCode.Gone, "challenge_expired");
        }

        using (var input = await PostInputAsync(http, expired, DownEvent(1, 0.5, 0.5)))
        {
            await ErrorEnvelope.AssertAsync(input, HttpStatusCode.Gone, "challenge_expired");
        }

        var settled = await ArrangeAsync();
        using (var agent = AgentClient(settled))
        using (var posted = await PostFrameAsync(agent, settled.JobId, sequence: 1, Frame(0x22)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        await SettleAsync(settled);

        using (var frame = await http.GetAsync(FrameUrl(settled, after: 0)))
        {
            await ErrorEnvelope.AssertAsync(frame, HttpStatusCode.Gone, "challenge_expired");
        }

        using var late = await PostInputAsync(http, settled, DownEvent(1, 0.5, 0.5));
        await ErrorEnvelope.AssertAsync(late, HttpStatusCode.Gone, "challenge_expired");
    }

    /// <summary>
    /// The id of somebody else's live view is not a way into it. A bug in a
    /// consumer that mixed two users' ids has to produce a refusal, not a
    /// screen-share of the other one's password entry.
    /// </summary>
    [Fact]
    public async Task A_live_view_belonging_to_another_session_is_simply_not_found()
    {
        var mine = await ArrangeAsync();
        var theirs = await ArrangeAsync();

        using var agent = AgentClient(theirs);
        using var http = factory.CreateAuthorizedClient();

        using (var posted = await PostFrameAsync(agent, theirs.JobId, sequence: 1, Frame(0x77)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var crossed = mine with { ChallengeId = theirs.ChallengeId };

        using (var frame = await http.GetAsync(FrameUrl(crossed, after: 0)))
        {
            await ErrorEnvelope.AssertAsync(frame, HttpStatusCode.BadRequest, "unsupported_resource");
        }

        using var input = await PostInputAsync(http, crossed, DownEvent(1, 0.5, 0.5));
        await ErrorEnvelope.AssertAsync(input, HttpStatusCode.BadRequest, "unsupported_resource");
    }

    /// <summary>
    /// The agent's legs sit behind the same lease check as every other
    /// job-scoped agent route, because a token that could photograph any job
    /// could photograph any user's login.
    /// </summary>
    [Fact]
    public async Task An_agent_that_does_not_hold_the_lease_can_neither_post_a_frame_nor_read_the_input()
    {
        var run = await ArrangeAsync();
        var stranger = await ArrangeAsync();

        using var wrongAgent = AgentClient(stranger);

        using (var frame = await PostFrameAsync(wrongAgent, run.JobId, sequence: 1, Frame(0x33)))
        {
            await ErrorEnvelope.AssertAsync(frame, HttpStatusCode.BadRequest, "unsupported_resource");
        }

        using (var input = await wrongAgent.GetAsync(InputUrl(run.JobId, after: 0)))
        {
            await ErrorEnvelope.AssertAsync(input, HttpStatusCode.BadRequest, "unsupported_resource");
        }

        // And no token at all reaches neither.
        using var anonymous = factory.CreateClient();

        using (var frame = await PostFrameAsync(anonymous, run.JobId, sequence: 1, Frame(0x33)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, frame.StatusCode);
        }

        using var poll = await anonymous.GetAsync(InputUrl(run.JobId, after: 0));
        Assert.Equal(HttpStatusCode.Unauthorized, poll.StatusCode);

        Assert.Null(Channel.Frame(run.JobId));
    }

    // ----------------------------------------------------------------- custody

    /// <summary>
    /// The claim the whole feature rests on: a streamed login puts no
    /// photograph and no keystroke at rest anywhere in the control plane.
    ///
    /// It needs a test rather than a habit. Every other picture in this system
    /// - the still relay's captcha - IS stored, in <c>ChallengeRow.ImageBytes</c>,
    /// and the natural way to build this would have been to reuse that column.
    /// </summary>
    [Fact]
    public async Task No_frame_and_no_keystroke_reaches_any_table()
    {
        const string typed = "correct-horse-battery-staple";

        var run = await ArrangeAsync();
        using var agent = AgentClient(run);
        using var http = factory.CreateAuthorizedClient();

        using (var posted = await PostFrameAsync(agent, run.JobId, sequence: 1, Frame(0xAB)))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        using (var accepted = await PostInputAsync(http, run, TextEvent(1, typed)))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // Both legs really did work, so this is a test about custody and not
        // about a feature that quietly did nothing.
        using (var frame = await http.GetAsync(FrameUrl(run, after: 0)))
        {
            Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        }

        var stored = Db.Read(factory, db => new
        {
            Images = db.Challenges.Count(c => c.ImageBytes != null),
            Text = db.Challenges
                .Where(c => c.AnswerValue != null && c.AnswerValue.Contains(typed))
                .Concat(db.Challenges.Where(c => c.PayloadJson.Contains(typed)))
                .Count(),
            Jobs = db.Jobs.Count(j =>
                (j.InputsJson != null && j.InputsJson.Contains(typed))
                || (j.MaterialJson != null && j.MaterialJson.Contains(typed))
                || j.ParamsJson.Contains(typed)
                || j.ConfigJson.Contains(typed)
                || (j.ErrorDetail != null && j.ErrorDetail.Contains(typed))),
            Sessions = db.Sessions.Count(s =>
                s.ConfigJson.Contains(typed)
                || (s.Label != null && s.Label.Contains(typed))
                || (s.ProviderAccountJson != null && s.ProviderAccountJson.Contains(typed))
                || (s.PendingBundle != null && s.PendingBundle.Contains(typed))),
            Results = db.Results.Count(r => r.PayloadJson.Contains(typed)),
        });

        Assert.Equal(0, stored.Images);
        Assert.Equal(0, stored.Text);
        Assert.Equal(0, stored.Jobs);
        Assert.Equal(0, stored.Sessions);
        Assert.Equal(0, stored.Results);
    }

    /// <summary>
    /// The frame bytes are excluded from body logging by construction rather
    /// than by anyone remembering.
    ///
    /// There is no body logger in this service today, and that is precisely why
    /// this has to be structural: the day somebody adds one - the framework's
    /// own, or anything that honours its metadata - the live routes are already
    /// out of it. Both legs are declared as route GROUPS carrying
    /// <c>HttpLoggingFields.None</c>, so a fifth live route inherits the
    /// refusal instead of needing a reviewer to catch it.
    ///
    /// No scrubber can find a password in a JPEG, which is the one place the
    /// streamed design is honestly worse than the form it replaces.
    /// </summary>
    [Fact]
    public void Every_live_route_is_declared_outside_body_logging()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("/live/", StringComparison.Ordinal) == true)
            .ToList();

        // The four routes of the fixed wire. If this number moves, the list
        // below moved with it and somebody has to look at the new one.
        Assert.Equal(4, endpoints.Count);

        foreach (var endpoint in endpoints)
        {
            var logging = endpoint.Metadata.GetMetadata<HttpLoggingAttribute>();

            Assert.True(
                logging is not null && logging.LoggingFields == HttpLoggingFields.None,
                $"'{endpoint.RoutePattern.RawText}' carries live pixels or live keystrokes and is not "
                + "excluded from body logging. Map it inside one of the live route groups in "
                + "LiveEndpoints rather than adding the metadata by hand.");
        }
    }

    // ---------------------------------------------------------------- helpers

    private LiveChannel Channel => factory.Services.GetRequiredService<LiveChannel>();

    private static string FrameUrl(LiveRun run, long after) =>
        $"/v1/{Provider}/login/{run.SessionId}/challenges/{run.ChallengeId}/live/frame?after={after}";

    private static string InputUrl(string jobId, long after) =>
        $"/agent/v1/jobs/{jobId}/live/input?after={after}";

    /// <summary>Distinguishable bytes, so "the newest frame" is a claim a test can check.</summary>
    private static byte[] Frame(byte tag) => [.. Jpeg, tag];

    private HttpClient AgentClient(LiveRun run)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", run.AgentToken);
        return client;
    }

    private static Task<HttpResponseMessage> PostFrameAsync(
        HttpClient agent, string jobId, long sequence, byte[] bytes) =>
        PostRawFrameAsync(agent, jobId, sequence.ToString(CultureInfo.InvariantCulture), PhoneSize, bytes);

    private static async Task<HttpResponseMessage> PostRawFrameAsync(
        HttpClient agent, string jobId, string? sequence, string? size, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/agent/v1/jobs/{jobId}/live/frame")
        {
            Content = new ByteArrayContent(bytes),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        if (sequence is not null) request.AddHeader(LiveHeaders.Sequence, sequence);
        if (size is not null) request.AddHeader(LiveHeaders.Size, size);

        return await agent.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostInputAsync(HttpClient http, LiveRun run, params object[] events)
    {
        using var request = Wire.Post(
            $"/v1/{Provider}/login/{run.SessionId}/challenges/{run.ChallengeId}/live/input",
            new { events });

        return await http.SendAsync(request);
    }

    /// <summary>
    /// Everything the agent can still collect, one bounded batch at a time.
    /// Each poll acknowledges the last sequence it saw, which is how the queue
    /// learns what it may forget.
    ///
    /// Bounded by what the caller expects rather than by polling until empty:
    /// an empty queue is a thirty-second long poll, which is right for an agent
    /// and wrong for a test that already knows how much there is.
    /// </summary>
    private static async Task<List<JsonElement>> DrainAsync(HttpClient agent, string jobId, int expected)
    {
        var drained = new List<JsonElement>();
        var after = long.MinValue;

        while (drained.Count < expected)
        {
            using var response = await agent.GetAsync(InputUrl(jobId, after));
            if (response.StatusCode != HttpStatusCode.OK) return drained;

            var events = (await response.JsonAsync()).GetProperty("events").EnumerateArray().ToList();
            if (events.Count == 0) return drained;

            drained.AddRange(events);
            after = events[^1].GetProperty("sequence").GetInt64();
        }

        return drained;
    }

    private static object MoveEvent(long sequence, double x, double y) => new { kind = "move", x, y, sequence };

    private static object DownEvent(long sequence, double x, double y) => new { kind = "down", x, y, sequence };

    private static object TextEvent(long sequence, string text) => new { kind = "text", text, sequence };

    private static object KeyEvent(long sequence, string key) => new { kind = "key", key, sequence };

    /// <summary>
    /// A session with a run parked on a live view, and the agent holding its
    /// lease.
    ///
    /// Staged into the tables rather than driven through POST /login: no
    /// shipped provider raises a live view yet, and the four routes under test
    /// care about a session, a leased job and a challenge - not about how those
    /// three came to exist.
    /// </summary>
    private async Task<LiveRun> ArrangeAsync(ChallengeType type = ChallengeType.LiveView)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();
        var challenges = scope.ServiceProvider.GetRequiredService<ChallengeService>();

        var now = DateTimeOffset.UtcNow;
        var subject = $"u_live_{Guid.NewGuid():N}";
        var token = AgentAuth.NewToken();

        db.Agents.Add(new AgentRow
        {
            Id = Ids.New(Ids.Agent),
            Name = "live-view-test",
            OwnerSubject = subject,
            TokenHash = AgentAuth.Hash(token),
            LastHeartbeatAt = now,
            CreatedAt = now,
        });

        var session = new SessionRow
        {
            Id = Ids.New(Ids.Session),
            ProviderId = Provider,
            Subject = subject,
            State = SessionState.Queued,
            ExpiresAt = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(CancellationToken.None);

        var agentId = await db.Agents.Where(a => a.OwnerSubject == subject).Select(a => a.Id).FirstAsync();

        var job = await queue.EnqueueAsync(new NewJob
        {
            SessionId = session.Id,
            ProviderId = Provider,
            Kind = JobKind.Login,
        }, CancellationToken.None);

        // A live view is raised in the middle of a run the agent is holding.
        // The lease is what both agent routes check, so the fixture has to be a
        // leased job rather than merely a queued one.
        var row = await db.Jobs.FirstAsync(j => j.Id == job.Id);
        row.State = JobState.Leased;
        row.LeaseOwner = agentId;
        row.LeaseExpiresAt = now.AddMinutes(10);
        await db.SaveChangesAsync(CancellationToken.None);

        var challenge = await challenges.RaiseAsync(job.Id, new PendingChallenge
        {
            Type = type,
            ExpiresAt = now.AddMinutes(5),
            Payload = new ChallengePayload { PromptKey = "connect.challenge.live_login" },
        }, CancellationToken.None);

        return new LiveRun(session.Id, job.Id, challenge.Id, token);
    }

    /// <summary>Winds a live view past its deadline. RaiseAsync refuses to create one already expired, and should.</summary>
    private async Task ExpireAsync(string challengeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();

        await db.Challenges
            .Where(c => c.Id == challengeId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    /// <summary>The terminal marker a settled live view carries, as the adapter's success signal writes it.</summary>
    private async Task SettleAsync(LiveRun run)
    {
        using var scope = factory.Services.CreateScope();
        var challenges = scope.ServiceProvider.GetRequiredService<ChallengeService>();

        await challenges.AnswerAsync(run.ChallengeId, run.JobId, string.Empty, CancellationToken.None);
    }

    private sealed record LiveRun(string SessionId, string JobId, string ChallengeId, string AgentToken);
}
