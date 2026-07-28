using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Auth;

/// <summary>
/// Who may talk to the control plane at all.
///
/// Nobody but the consuming server, and the design says why: clients never
/// hold a connector hostname, never a connector credential, never a CORS
/// grant. Production stacks two independent layers - a client certificate
/// pinned by thumbprint, and an audience-checked M2M token - because either
/// one alone is a weak answer. The certificate is what survives a compromised
/// container on the same network; the token is what survives a stolen
/// certificate.
///
/// Development mode collapses that to one shared header so a local run needs
/// no PKI. It is unreachable in a deployed configuration: the platform
/// refuses to start in production without the real thing.
/// </summary>
public sealed class ConnectorAuth(IOptions<ConnectorOptions> options, ILogger<ConnectorAuth> logger)
{
    public const string DevelopmentHeader = "X-Connector-Key";

    private readonly ConnectorOptions _options = options.Value;

    /// <summary>
    /// Every rejection is the same <see cref="ErrorCode.InvalidRequest"/> with
    /// no detail on the wire: an unauthenticated caller learns whether the
    /// service exists and nothing else.
    /// </summary>
    public async ValueTask<bool> AuthenticateAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return _options.IsProduction
            ? await ProductionAsync(http)
            : Development(http);
    }

    private bool Development(HttpContext http)
    {
        var expected = _options.Auth.SharedSecret;
        if (string.IsNullOrEmpty(expected)) return true;   // no secret configured: a bare local run

        var presented = http.Request.Headers[DevelopmentHeader].ToString();
        return FixedTimeEquals(presented, expected);
    }

    private async ValueTask<bool> ProductionAsync(HttpContext http)
    {
        if (!ClientCertificateAllowed(http))
        {
            logger.LogWarning("rejected a call with no allowlisted client certificate");
            return false;
        }

        var result = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            logger.LogWarning("rejected a call with no valid bearer token");
            return false;
        }

        http.User = result.Principal;
        return true;
    }

    /// <summary>
    /// Accepts either a certificate on the connection or a thumbprint the
    /// terminating proxy verified and forwarded. The forwarded form exists
    /// because TLS frequently ends at the reverse proxy; it is only trusted
    /// because nothing but that proxy can reach the port.
    /// </summary>
    private bool ClientCertificateAllowed(HttpContext http)
    {
        var allowed = _options.Auth.ClientCertificateThumbprints;
        if (allowed.Count == 0) return false;

        var presented = http.Connection.ClientCertificate?.Thumbprint
                        ?? http.Request.Headers[_options.Auth.ClientCertificateHeader].ToString();

        if (string.IsNullOrWhiteSpace(presented)) return false;

        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate.Replace(":", string.Empty, StringComparison.Ordinal),
                    presented.Replace(":", string.Empty, StringComparison.Ordinal),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Constant time, because a shared secret compared with <c>==</c> leaks itself one byte at a time.</summary>
    internal static bool FixedTimeEquals(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}

/// <summary>Applied to the whole <c>/v1</c> group, so no route can be added unprotected by omission.</summary>
public sealed class ConnectorAuthFilter(ConnectorAuth auth) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!await auth.AuthenticateAsync(context.HttpContext))
        {
            return ConnectorResults.Error(new ConnectorHttpException(
                StatusCodes.Status401Unauthorized,
                ConnectorException.InvalidRequest("unauthorized")));
        }

        return await next(context);
    }
}
