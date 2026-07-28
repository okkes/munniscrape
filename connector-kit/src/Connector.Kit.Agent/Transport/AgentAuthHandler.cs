using System.Net.Http.Headers;

namespace Connector.Kit.Agent.Transport;

/// <summary>
/// Attaches the per-agent token to every control-plane call. Enrollment runs
/// before there is one, which is why the header is conditional rather than
/// required.
/// </summary>
internal sealed class AgentAuthHandler : DelegatingHandler
{
    private readonly AgentIdentity _identity;

    public AgentAuthHandler(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null && _identity.Current is { } enrollment)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrollment.Token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
