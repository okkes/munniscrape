using System.Net;
using System.Net.Http.Json;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent.Transport;

/// <summary>
/// The agent's whole view of the control plane: nine calls, all outbound.
///
/// There is no inbound surface here by design - that is what lets an agent sit
/// behind NAT on a residential line or on a user's own machine with no port
/// open and no redesign for the bring-your-own case.
/// </summary>
public sealed class ControlPlaneClient
{
    public const string ClientName = "connector-agent-control-plane";

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<ControlPlaneClient> _logger;

    public ControlPlaneClient(IHttpClientFactory factory, ILogger<ControlPlaneClient> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _logger = logger;
    }

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var response = await PostAsync("agent/v1/enroll", request, ct).ConfigureAwait(false);
        EnsureSuccess(response, "enroll");
        return await ReadAsync<EnrollResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        using var response = await PostAsync("agent/v1/heartbeat", request, ct).ConfigureAwait(false);
        EnsureSuccess(response, "heartbeat");
        return await ReadAsync<HeartbeatResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Long-polls for work. Null means the poll came back empty and the agent
    /// should simply ask again - not an error, and not a reason to back off.
    /// </summary>
    public async Task<LeasedJob?> LeaseAsync(LeaseRequest request, CancellationToken ct)
    {
        using var response = await PostAsync("agent/v1/jobs/lease", request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        EnsureSuccess(response, "lease");
        return await ReadAsync<LeasedJob>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extends the lease on a running job. False means the control plane no
    /// longer believes we hold it, and the job must stop: continuing would let
    /// two agents drive the same login.
    /// </summary>
    public async Task<bool> RenewAsync(string jobId, CancellationToken ct)
    {
        var path = JobPath(jobId, "renew");
        var client = _factory.CreateClient(ClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(path, content: null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ControlPlaneException(ex.StatusCode, $"POST {path} did not reach the control plane", ex);
        }

        using var _ = response;

        if (response.IsSuccessStatusCode) return true;

        // A 4xx is a verdict; a 5xx is the control plane having a bad moment
        // and must not kill a job that is otherwise going fine.
        if ((int)response.StatusCode is >= 400 and < 500) return false;

        throw new ControlPlaneException(response.StatusCode, $"renew of job {jobId} failed");
    }

    public async Task ProgressAsync(string jobId, ProgressReport report, CancellationToken ct)
    {
        using var response = await PostAsync(JobPath(jobId, "progress"), report, ct).ConfigureAwait(false);
        EnsureSuccess(response, "progress");
    }

    public async Task<RaiseChallengeResponse> RaiseChallengeAsync(
        string jobId, RaiseChallengeRequest request, CancellationToken ct)
    {
        using var response = await PostAsync(JobPath(jobId, "challenge"), request, ct).ConfigureAwait(false);
        EnsureSuccess(response, "challenge");
        return await ReadAsync<RaiseChallengeResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Long-polls for the human's answer. Null means "not yet"; a control plane
    /// that has already expired the challenge answers with 408 or 410, which
    /// surfaces as <see cref="ErrorCode.ChallengeExpired"/> so the job fails
    /// cleanly instead of pinning a browser for the full job timeout.
    /// </summary>
    public async Task<ChallengeAnswer?> PollAnswerAsync(string jobId, CancellationToken ct)
    {
        var path = JobPath(jobId, "answer");
        var client = _factory.CreateClient(ClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(path, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ControlPlaneException(ex.StatusCode, $"GET {path} did not reach the control plane", ex);
        }

        using var _ = response;

        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Gone)
        {
            throw new ConnectorException(ErrorCode.ChallengeExpired,
                $"the control plane expired the challenge on job {jobId}");
        }

        EnsureSuccess(response, "answer");
        return await ReadAsync<ChallengeAnswer>(response, ct).ConfigureAwait(false);
    }

    public async Task ResultAsync(string jobId, JobResultRequest result, CancellationToken ct)
    {
        using var response = await PostAsync(JobPath(jobId, "result"), result, ct).ConfigureAwait(false);
        EnsureSuccess(response, "result");
    }

    public async Task FailAsync(string jobId, JobFailRequest failure, CancellationToken ct)
    {
        using var response = await PostAsync(JobPath(jobId, "fail"), failure, ct).ConfigureAwait(false);
        EnsureSuccess(response, "fail");
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        var client = _factory.CreateClient(ClientName);
        try
        {
            return await client.PostAsJsonAsync(path, body, AgentJson.Options, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ControlPlaneException(ex.StatusCode, $"POST {path} did not reach the control plane", ex);
        }
    }

    private static string JobPath(string jobId, string action) =>
        $"agent/v1/jobs/{Uri.EscapeDataString(jobId)}/{action}";

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode) return;
        throw new ControlPlaneException(response.StatusCode, $"{what} returned {(int)response.StatusCode}");
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(AgentJson.Options, ct).ConfigureAwait(false)
                   ?? throw new ControlPlaneException(response.StatusCode, $"empty {typeof(T).Name} body");
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(ex, "the control plane sent a {Type} this agent cannot read", typeof(T).Name);
            throw new ControlPlaneException(response.StatusCode, $"malformed {typeof(T).Name} body", ex);
        }
    }
}
