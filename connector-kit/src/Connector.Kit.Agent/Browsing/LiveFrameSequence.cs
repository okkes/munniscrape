namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// The number every live frame of one JOB carries, and the reason it is an
/// object rather than a local in the shutter loop.
///
/// The wire says the frame sequence is monotonic per job. The connector keeps
/// one slot per job and refuses a frame whose sequence is not higher than the
/// one it holds - and it answers 200 either way, so the agent is never told.
/// A counter that starts at zero for each <see cref="LiveViewSession"/> is
/// therefore fine for the first live view of a job and silently fatal for the
/// second: every frame is discarded at the connector while the consumer goes on
/// displaying the LAST picture of the previous step. That is a stale login page
/// with a human typing a password into it and no line anywhere saying so.
///
/// So the count outlives the session. One of these is held by
/// <see cref="Execution.AgentJobContext"/>, which is built once per job, and
/// handed to every session opened underneath it - which is what makes a second
/// view continue the count rather than restart it.
///
/// <b>Gaps are fine, going backwards is not.</b> A number is taken before the
/// POST and kept whether or not the POST lands, because a retried frame is a
/// DIFFERENT picture of a page that has moved on, and re-using its number would
/// be the one thing the connector refuses.
/// </summary>
internal sealed class LiveFrameSequence
{
    private long _last;

    /// <summary>The number the last frame took; zero before any frame at all.</summary>
    public long Last => Interlocked.Read(ref _last);

    /// <summary>The next number, starting at one. Safe from any thread.</summary>
    public long Next() => Interlocked.Increment(ref _last);
}
