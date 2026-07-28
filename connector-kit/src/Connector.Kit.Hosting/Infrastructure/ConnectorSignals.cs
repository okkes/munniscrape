using System.Collections.Concurrent;

namespace Connector.Kit.Hosting.Infrastructure;

/// <summary>
/// A nudge, not a message bus.
///
/// Every waiter in this service - the login wait, the agent's lease
/// long-poll, the agent's answer long-poll, the SSE streams - re-reads the
/// database and decides for itself. This class only shortens the interval
/// between a change and that re-read on the instance where the change
/// happened. Deliberately not a delivery guarantee: every caller also has a
/// fallback poll, so a missed signal costs latency and nothing else, and the
/// design survives a second instance without becoming a distributed queue.
/// </summary>
public sealed class ConnectorSignals
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters =
        new(StringComparer.Ordinal);

    public static string Session(string sessionId) => "ses:" + sessionId;

    public static string Job(string jobId) => "job:" + jobId;

    /// <summary>Any queued job appeared. Wakes every parked agent lease poll.</summary>
    public const string Queue = "queue";

    public void Signal(string key)
    {
        if (_waiters.TryRemove(key, out var waiter)) waiter.TrySetResult();
    }

    /// <summary>
    /// Waits for a signal on <paramref name="key"/>, or for
    /// <paramref name="window"/> to elapse. Returns true when it was a signal,
    /// which callers use only to decide whether to re-read immediately.
    /// </summary>
    public async Task<bool> WaitAsync(string key, TimeSpan window, CancellationToken ct)
    {
        var waiter = _waiters.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(window, timeout.Token);
        var finished = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);

        if (finished == waiter.Task)
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            return true;
        }

        // Nobody signalled: drop our registration so a key that is never
        // signalled cannot accumulate for the life of the process.
        _waiters.TryRemove(new KeyValuePair<string, TaskCompletionSource>(key, waiter));
        return false;
    }
}
