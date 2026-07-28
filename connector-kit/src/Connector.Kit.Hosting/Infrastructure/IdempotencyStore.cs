using System.Collections.Concurrent;

namespace Connector.Kit.Hosting.Infrastructure;

/// <summary>
/// Collapses a retried mutating call onto the thing it already created.
///
/// This matters most on <c>POST /login</c>, and for the same reason
/// everything else in this platform is careful about logins: a caller that
/// times out and retries would otherwise start a second run, submit the same
/// credential a second time, and hand the provider two failed attempts where
/// the user made one. The idempotency key is the caller's statement that the
/// second request is the first request again.
///
/// In memory, with a TTL. A restart forgets, which costs a duplicate session
/// in a window measured in seconds - the alternative is a table whose only
/// purpose is to remember requests, which is the opposite of staying a pipe.
/// </summary>
public interface IIdempotencyStore
{
    bool TryGet(string scope, string key, out string value);

    /// <summary>Records the outcome. First writer wins, so a genuine race collapses too.</summary>
    string Remember(string scope, string key, string value);

    int Sweep();
}

public sealed class InMemoryIdempotencyStore(TimeProvider time) : IIdempotencyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string scope, string key, out string value)
    {
        value = string.Empty;
        if (!_entries.TryGetValue(Compose(scope, key), out var entry)) return false;

        if (entry.ExpiresAt <= time.GetUtcNow())
        {
            _entries.TryRemove(Compose(scope, key), out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    public string Remember(string scope, string key, string value)
    {
        var entry = _entries.GetOrAdd(Compose(scope, key), new Entry(value, time.GetUtcNow() + Retention));
        return entry.Value;
    }

    public int Sweep()
    {
        var now = time.GetUtcNow();
        var removed = 0;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now && _entries.TryRemove(key, out _)) removed++;
        }

        return removed;
    }

    /// <summary>
    /// ASCII unit separator, written as a numeric escape rather than a
    /// literal so the source carries no invisible control character.
    /// </summary>
    private const char Separator = (char)0x1F;

    private static string Compose(string scope, string key) => scope + Separator + key;

    private readonly record struct Entry(string Value, DateTimeOffset ExpiresAt);
}
