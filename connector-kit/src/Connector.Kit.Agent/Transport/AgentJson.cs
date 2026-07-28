using System.Text.Json;

namespace Connector.Kit.Agent.Transport;

/// <summary>
/// The wire encoding for every agent call.
///
/// Delegates to <see cref="ConnectorWireJson"/> rather than rebuilding it.
/// This file used to construct its own options and claim in a comment that
/// they were "deliberately identical" to the control plane's. They were not,
/// and the divergence broke every agent-served fetch on the platform while
/// leaving logins working - see the remarks on <see cref="ConnectorWireJson"/>.
/// A comment cannot keep two definitions in sync; a reference can.
/// </summary>
internal static class AgentJson
{
    public static JsonSerializerOptions Options => ConnectorWireJson.Options;
}
