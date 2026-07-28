using Connector.Kit.Challenges;

namespace Connector.Kit.Hosting.Live;

/// <summary>
/// The live view's entire memory: the newest picture of each streamed login,
/// and the events the human made that the agent has not collected yet.
///
/// Nothing here ever reaches a table, and that is the feature rather than an
/// optimisation. A frame is a photograph of somebody's provider login page -
/// their e-mail address in plain text, their password as dots, whatever the
/// provider happens to be showing - and it is worthless a tenth of a second
/// later. Writing it to Postgres would put the provider's authenticated pixels
/// at rest in the control plane, which is the exact custody problem a streamed
/// login exists to remove. The same goes for the events coming back: a
/// keystroke that landed in a column would be the password we just went to
/// this much trouble not to have.
///
/// Three bounds, because "in memory" without them is a leak with a nicer name:
///
/// <list type="bullet">
/// <item>ONE frame per job. A new one REPLACES the old rather than queueing
/// behind it - a consumer that fell behind wants the login as it is, not a
/// slideshow of how it was.</item>
/// <item>A bounded event queue per job. <see cref="Enqueue"/> says what gives
/// way when it fills.</item>
/// <item>A bounded number of jobs, and an idle window. A login abandoned
/// mid-stream - the tab closed, the phone back in a pocket - stops being
/// written to, and <see cref="Sweep"/> drops it. If no sweep ever runs the job
/// cap still evicts the least recently written entry, so the worst case is
/// <see cref="MaxJobs"/> x (<see cref="MaxFrameBytes"/> +
/// <see cref="MaxQueuedEvents"/> events) ~= 40 MB whatever anybody forgets to
/// call.</item>
/// </list>
///
/// Instance-local, deliberately and for now. A job's frames and its input have
/// to reach the same process for the life of the stream, which is the first
/// session-scoped affinity in the platform; today the shop connector runs one
/// replica and this costs nothing. Frames in a shared cache would be the same
/// custody mistake as frames in a table, so the answer when a second replica
/// arrives is routing, not storage.
/// </summary>
public sealed class LiveChannel(TimeProvider time)
{
    /// <summary>
    /// A refusal, not a tuning knob. A phone-sized JPEG of a login page is
    /// tens of kilobytes; anything near a megabyte is an agent sending
    /// something that is not a frame, and a frame slot is not a place to find
    /// that out later.
    /// </summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>
    /// Four full batches' worth. Enough that a drag across the screen survives
    /// an agent that is a poll behind, small enough that a consumer cannot
    /// make the connector hold its gestures indefinitely.
    /// </summary>
    public const int MaxQueuedEvents = 4 * LiveInputBatch.MaxEvents;

    /// <summary>
    /// How many logins may be streaming at once before the least recently
    /// written one is dropped. Far above what a fleet running one browser slot
    /// can actually serve; it exists so that memory is bounded by construction
    /// rather than by the sweeper being wired up.
    /// </summary>
    public const int MaxJobs = 32;

    /// <summary>
    /// How long an entry survives with nothing written to it in either
    /// direction. A streamed login writes several times a second, so silence
    /// this long means the stream is over however it ended.
    /// </summary>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// One lock for the whole map rather than one per entry. Every critical
    /// section here is a reference swap or a walk of a list bounded by
    /// <see cref="MaxQueuedEvents"/> - the bytes are read off the wire and
    /// handed over as an already-built array - so there is nothing to gain
    /// from finer locking and a great deal to lose from getting it wrong.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Stores the newest picture of a job, and says whether it was kept.
    ///
    /// False means the frame was older than the one already held. Two POSTs
    /// can overtake each other on a mobile uplink, and a picture from the past
    /// replacing the present would show the human their login as it was a
    /// moment ago - with the cursor, the error banner and the field contents
    /// of that moment - which is worse than dropping it, because they would
    /// act on it.
    /// </summary>
    public bool Publish(string jobId, LiveFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            var entry = Admit(jobId);
            if (entry.Frame is { } held && frame.Sequence <= held.Sequence) return false;

            entry.Frame = frame;
            entry.WrittenAt = time.GetUtcNow();
            return true;
        }
    }

    /// <summary>The newest picture of a job, or null when none has arrived yet.</summary>
    public LiveFrame? Frame(string jobId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(jobId, out var entry) ? entry.Frame : null;
        }
    }

    /// <summary>
    /// Takes what the human just did, and says how many older events had to go
    /// to make room.
    ///
    /// The batch is taken WHOLE - a gesture is refused whole or dispatched
    /// whole, never half - and then the queue is trimmed back to
    /// <see cref="MaxQueuedEvents"/> from the front, oldest
    /// <see cref="LiveInputKind.Move"/> first. That ordering is the whole
    /// policy and it is not symmetric: a pointer position the human has
    /// already moved past is worth nothing, while a click, a keystroke or a
    /// typed character is something they chose to do exactly once. Refusing
    /// the NEWEST events instead - the obvious way to bound a queue - would
    /// throw away the tap they just made in favour of thirty stale hover
    /// positions.
    ///
    /// The count comes back so a caller can log how many events were dropped.
    /// Never what they were: an event carries the characters of somebody's
    /// password, and no log line in this platform may be able to hold one.
    /// </summary>
    public int Enqueue(string jobId, LiveInputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        lock (_gate)
        {
            var entry = Admit(jobId);
            entry.WrittenAt = time.GetUtcNow();
            entry.Pending.AddRange(batch.Events);

            var dropped = 0;
            while (entry.Pending.Count > MaxQueuedEvents)
            {
                var victim = entry.Pending.FindIndex(e => e.Kind is LiveInputKind.Move);
                entry.Pending.RemoveAt(victim < 0 ? 0 : victim);
                dropped++;
            }

            return dropped;
        }
    }

    /// <summary>
    /// Hands the agent everything the human did after <paramref name="after"/>,
    /// in the order they did it.
    ///
    /// <paramref name="after"/> is the CONSUMER's own counter -
    /// <see cref="LiveInput.Sequence"/> - and not a cursor of the connector's,
    /// because the batch on the wire carries no field a cursor could ride back
    /// on: the only number the agent can learn from a response is the sequence
    /// of the events in it, so that is the only number it can ask about next.
    /// A consumer whose counter does not advance therefore starves itself,
    /// which is the same monotonicity the agent's own de-duplication already
    /// requires of it.
    ///
    /// Asking clears the acknowledgement: everything at or below
    /// <paramref name="after"/> has demonstrably arrived and is dropped. What
    /// is above it stays queued until a later poll says otherwise, so a
    /// response lost between here and the agent is re-delivered rather than
    /// silently gone.
    ///
    /// Never more than <see cref="LiveInputBatch.MaxEvents"/> at a time, so
    /// what comes back is itself a well-formed batch; the rest waits for the
    /// next poll, which is one round trip away.
    /// </summary>
    public LiveInputBatch Collect(string jobId, long after)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(jobId, out var entry)) return Empty;

            entry.Pending.RemoveAll(e => e.Sequence <= after);
            if (entry.Pending.Count == 0) return Empty;

            return new LiveInputBatch
            {
                Events = [.. entry.Pending.Take(LiveInputBatch.MaxEvents)],
            };
        }
    }

    /// <summary>
    /// Forgets a job's frame and its queued input, now.
    ///
    /// Called when a job reaches a terminal state. That is the prompt path and
    /// not the guarantee - a job can also die inside the queue on an expired
    /// lease, without passing through the outcome service - which is why
    /// <see cref="Sweep"/> exists and why the entry count is capped.
    /// </summary>
    public bool Drop(string jobId)
    {
        lock (_gate)
        {
            return _entries.Remove(jobId);
        }
    }

    /// <summary>
    /// Drops every entry nothing has been written to for
    /// <see cref="IdleWindow"/>, and answers how many went.
    ///
    /// This is what closes the abandoned login: the human shut the tab, the
    /// agent's job will eventually time out, and in between there is a
    /// photograph of a half-typed login form sitting in memory with nobody
    /// coming for it. Reads deliberately do not count as traffic - a consumer
    /// polling a stream whose agent stopped posting is looking at something
    /// that is already over.
    /// </summary>
    public int Sweep()
    {
        var cutoff = time.GetUtcNow() - IdleWindow;

        lock (_gate)
        {
            var stale = _entries.Where(e => e.Value.WrittenAt <= cutoff).Select(e => e.Key).ToList();
            foreach (var jobId in stale) _entries.Remove(jobId);
            return stale.Count;
        }
    }

    /// <summary>Entries currently held. For tests and for a health figure; never the contents.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private Entry Admit(string jobId)
    {
        if (_entries.TryGetValue(jobId, out var existing)) return existing;

        // The cap is a backstop, so it evicts rather than refuses: a new
        // login is the one that has a human in front of it, and the entry it
        // displaces is the one nothing has been written to for longest.
        while (_entries.Count >= MaxJobs)
        {
            var oldest = _entries.MinBy(e => e.Value.WrittenAt).Key;
            _entries.Remove(oldest);
        }

        var entry = new Entry { WrittenAt = time.GetUtcNow() };
        _entries[jobId] = entry;
        return entry;
    }

    private static readonly LiveInputBatch Empty = new();

    /// <summary>
    /// One streamed login's worth of memory. A list rather than a queue
    /// because the drop policy removes from the middle - the oldest MOVE, not
    /// simply the oldest event.
    /// </summary>
    private sealed class Entry
    {
        public LiveFrame? Frame { get; set; }

        public List<LiveInput> Pending { get; } = [];

        /// <summary>Last write in either direction. See <see cref="Sweep"/>.</summary>
        public DateTimeOffset WrittenAt { get; set; }
    }
}
