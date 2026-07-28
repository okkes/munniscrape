using System.Net;

namespace Connector.Kit.Agent.Transport;

/// <summary>
/// A control-plane call did not go through. Distinct from
/// <see cref="Errors.ConnectorException"/> on purpose: that one describes what
/// a PROVIDER did and travels to the caller, this one describes our own link
/// to the control plane and never leaves the agent.
/// </summary>
public sealed class ControlPlaneException : Exception
{
    public ControlPlaneException(HttpStatusCode? status, string message, Exception? inner = null)
        : base(message, inner) => Status = status;

    /// <summary>Null when the call never produced a response at all.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>Our token is gone. An agent that sees this stops rather than hammering.</summary>
    public bool IsAuthFailure => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>The control plane no longer believes we hold this job.</summary>
    public bool IsLeaseLost => Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone;

    /// <summary>Worth trying again; anything else means the request itself was wrong.</summary>
    public bool IsTransient =>
        Status is null or HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError;
}
