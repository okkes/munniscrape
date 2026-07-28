using Connector.Kit.AgentProtocol;
using Connector.Kit.Manifests;

namespace Connector.Kit.Hosting.Jobs;

/// <summary>
/// Runs a job in the control plane's own process.
///
/// This is what lets a pure-HTTP provider need no agent at all: Albert
/// Heijn's whole runtime is a redirect challenge and a refresh token, and
/// standing up an agent fleet for it would be infrastructure with no purpose.
/// The adapter sees the identical <see cref="Adapters.IJobContext"/> it would
/// see on a real agent - progress, challenges, the credential latch and the
/// politeness limiter all behave the same - so nothing about an adapter
/// changes if its provider later needs a browser and moves out of process.
///
/// Only <see cref="AgentClass.Inline"/> providers are eligible, which the
/// manifest validator already restricts to <see cref="ProviderRuntime.Http"/>:
/// the control plane has no browser binaries and its egress is the wrong
/// address for provider traffic.
/// </summary>
public interface IInlineJobRunner
{
    bool CanRun(ProviderManifest manifest);

    /// <summary>Provider ids this process will run itself.</summary>
    IReadOnlyList<string> Providers { get; }

    /// <summary>What the in-process pump advertises when it asks the queue for work.</summary>
    AgentCapabilities Capabilities { get; }

    /// <summary>
    /// Nudges the in-process pump to look for work. Non-blocking and
    /// idempotent - the pump also polls, so a missed nudge costs latency and
    /// nothing else.
    /// </summary>
    void Dispatch();

    /// <summary>
    /// Runs one leased job to its terminal state. The pump calls this; a host
    /// that would rather drive jobs itself can call it directly.
    /// </summary>
    Task RunAsync(LeasedJob job, CancellationToken ct);
}
