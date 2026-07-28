using System.Net;

namespace Connector.Kit.Agent.Networking;

/// <summary>
/// The handler every adapter's outbound provider traffic goes through.
///
/// It is installed on the <see cref="Adapters.IJobContext.Http"/> client the
/// job hands the adapter, which is why that client is the only legitimate one:
/// an adapter that news up its own <c>HttpClient</c> silently opts out of the
/// per-provider rate limit the whole posture section of the design rests on.
/// </summary>
public sealed class PolitenessLimiter : DelegatingHandler
{
    /// <summary>Used when a provider says "slow down" without saying how much.</summary>
    private static readonly TimeSpan UnspecifiedPenalty = TimeSpan.FromSeconds(30);

    private readonly PolitenessGate _gate;
    private readonly string _providerId;
    private readonly TimeSpan _minGap;
    private readonly TimeProvider _time;

    public PolitenessLimiter(PolitenessGate gate, string providerId, TimeSpan minGap, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        _gate = gate;
        _providerId = providerId;
        _minGap = minGap;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (await _gate.EnterAsync(_providerId, _minGap, cancellationToken).ConfigureAwait(false))
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                _gate.Penalise(_providerId, RetryAfter(response) ?? UnspecifiedPenalty);
            }

            return response;
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) return date - _time.GetUtcNow();
        return null;
    }
}
