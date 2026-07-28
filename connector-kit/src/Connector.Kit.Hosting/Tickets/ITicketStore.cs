using System.Collections.Concurrent;
using Connector.Kit.Security;

namespace Connector.Kit.Hosting.Tickets;

/// <summary>
/// What a redeemed bundle turns into.
///
/// The unsealed material lives here and nowhere else until a job is created
/// from it, which is the whole point of the ticket: the caller keeps the
/// user's requested <c>GET …?since=…</c> shape without a credential ever
/// riding a URL or an access log.
/// </summary>
public sealed record TicketGrant
{
    public required string Subject { get; init; }

    public required string SessionId { get; init; }

    public required string ProviderId { get; init; }

    public required SessionMaterial Material { get; init; }

    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Tickets never leave the consuming server: they are minted by
/// <c>sessions/resume</c>, presented on the next call, and expire on a hard
/// TTL. Nothing here is persisted - a restart invalidates every ticket, and
/// the caller simply resumes again from the bundle it already holds.
/// </summary>
public interface ITicketStore
{
    /// <summary>Returns the ticket id. The grant is not retrievable by anything else.</summary>
    string Mint(TicketGrant grant);

    /// <summary>
    /// Looks a ticket up and checks it belongs to the provider namespace it
    /// was presented under. The grant carries the subject it was minted for,
    /// so nothing downstream has to be told who the caller is - a ticket
    /// presented on another provider's route is simply not a ticket.
    /// </summary>
    bool TryRedeem(string? ticketId, string providerId, out TicketGrant grant);

    void Revoke(string ticketId);

    /// <summary>Drops every ticket for a session. Called on disconnect.</summary>
    void RevokeSession(string sessionId);

    int Sweep();
}

public sealed class InMemoryTicketStore(TimeProvider time) : ITicketStore
{
    private readonly ConcurrentDictionary<string, TicketGrant> _tickets = new(StringComparer.Ordinal);

    public string Mint(TicketGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var id = Ids.New(Ids.Ticket);
        _tickets[id] = grant;
        return id;
    }

    public bool TryRedeem(string? ticketId, string providerId, out TicketGrant grant)
    {
        grant = null!;
        if (string.IsNullOrWhiteSpace(ticketId)) return false;
        if (!_tickets.TryGetValue(ticketId, out var found)) return false;

        if (found.ExpiresAt <= time.GetUtcNow())
        {
            _tickets.TryRemove(ticketId, out _);
            return false;
        }

        if (!string.Equals(found.ProviderId, providerId, StringComparison.Ordinal)) return false;

        grant = found;
        return true;
    }

    public void Revoke(string ticketId) => _tickets.TryRemove(ticketId, out _);

    public void RevokeSession(string sessionId)
    {
        foreach (var (id, grant) in _tickets)
        {
            if (string.Equals(grant.SessionId, sessionId, StringComparison.Ordinal))
            {
                _tickets.TryRemove(id, out _);
            }
        }
    }

    public int Sweep()
    {
        var now = time.GetUtcNow();
        var removed = 0;
        foreach (var (id, grant) in _tickets)
        {
            if (grant.ExpiresAt <= now && _tickets.TryRemove(id, out _)) removed++;
        }

        return removed;
    }
}
