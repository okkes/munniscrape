using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Jobs;

/// <summary>
/// A minimum gap between outbound provider calls, installed on the client
/// every adapter is handed.
///
/// It lives in the handler rather than in adapter discipline because
/// politeness is a platform non-negotiable: only the authenticated user's own
/// data, at a human-plausible cadence, never a firehose. An adapter that
/// wants to go faster has to reach for a client it was not given, which is a
/// visible act rather than an oversight.
/// </summary>
public sealed class PolitenessHandler(IOptions<ConnectorOptions> options) : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minimumGap = TimeSpan.FromMilliseconds(options.Value.Timeouts.PolitenessMs);
    private long _lastTicks;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Environment.TickCount64;
            var elapsed = TimeSpan.FromMilliseconds(now - _lastTicks);
            if (_lastTicks != 0 && elapsed < _minimumGap)
            {
                await Task.Delay(_minimumGap - elapsed, cancellationToken).ConfigureAwait(false);
            }

            _lastTicks = Environment.TickCount64;
        }
        finally
        {
            _gate.Release();
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }
}
