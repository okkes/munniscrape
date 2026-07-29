using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Auth;

/// <summary>
/// Agents are a separate identity class, and that is the whole point.
///
/// A per-agent bearer token, scoped to <c>/agent/v1/*</c> and nothing else,
/// revocable individually. It cannot read a catalogue, cannot resume a
/// session, cannot see another agent - so a user's own machine, running on
/// their line and holding their browser profiles, is never also a way into
/// the control plane.
///
/// Only the token's hash is stored. The token itself is shown once, at
/// enrollment, and is unrecoverable afterwards.
/// </summary>
public sealed class AgentAuth(ConnectorDbContext db, IOptions<ConnectorOptions> options, TimeProvider time)
{
    private const string EnrollmentAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConnectorOptions _options = options.Value;

    public static string Hash(string token) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Resolves the agent behind a request, or null. Revoked agents resolve to null.</summary>
    public async Task<AgentRow?> ResolveAsync(HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = Hash(header["Bearer ".Length..].Trim());
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.TokenHash == hash, ct);
        return agent is null || agent.Revoked ? null : agent;
    }

    /// <summary>
    /// Mints an enrollment code. Short-lived, single-use, and carrying the
    /// subject - which is what stops a BYO agent ever serving anyone but the
    /// user who enrolled it.
    ///
    /// The code is HMAC'd with a per-stack key before it is stored, so a read
    /// of this table yields nothing anyone can redeem.
    /// </summary>
    public async Task<AgentEnrollmentCode> MintEnrollmentAsync(string subject, string name, CancellationToken ct)
    {
        var code = string.Create(14, 0, static (span, _) =>
        {
            "AGNT-".CopyTo(span);
            for (var i = 5; i < span.Length; i++)
            {
                span[i] = i == 9
                    ? '-'
                    : EnrollmentAlphabet[RandomNumberGenerator.GetInt32(EnrollmentAlphabet.Length)];
            }
        });

        var now = time.GetUtcNow();
        var row = new EnrollmentRow
        {
            CodeHash = Sign(code),
            Subject = subject,
            Name = name,
            ExpiresAt = now.AddSeconds(_options.Timeouts.EnrollmentCodeSeconds),
        };

        db.Enrollments.Add(row);
        await db.SaveChangesAsync(ct);

        return new AgentEnrollmentCode(code, row.ExpiresAt);
    }

    /// <summary>
    /// Development only: makes <see cref="ConnectorOptions.DevEnrollmentCode"/>
    /// redeemable, creating the row or resetting the one already there.
    ///
    /// Resetting rather than creating-if-absent is deliberate. The agent's
    /// state file and the control plane's database are two volumes, and
    /// anything that clears one without the other - deleting a stuck agent
    /// state, rebuilding the agent image, `docker compose down` on a stack
    /// whose database volume outlives it - would otherwise leave the code
    /// permanently spent and the agent permanently unable to come back. In a
    /// development stack the useful invariant is "this code always works",
    /// not "this code works once".
    ///
    /// Callers must have checked the mode; this method does not, so that the
    /// refusal lives at start-up where it is loud.
    /// </summary>
    public async Task SeedDevEnrollmentAsync(string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var hash = Sign(code);
        var expiresAt = time.GetUtcNow().AddYears(10);
        var row = await db.Enrollments.FirstOrDefaultAsync(e => e.CodeHash == hash, ct);

        if (row is null)
        {
            db.Enrollments.Add(new EnrollmentRow
            {
                CodeHash = hash,
                Subject = ConnectorOptions.DevFleetSubject,
                Name = "development",
                ExpiresAt = expiresAt,
            });
        }
        else
        {
            row.RedeemedAt = null;
            row.ExpiresAt = expiresAt;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Redeems a code exactly once and returns the subject it was minted for.
    /// A second redemption of the same code fails, which is what makes it a
    /// code rather than a password.
    /// </summary>
    public async Task<EnrollmentRow> RedeemEnrollmentAsync(string code, CancellationToken ct)
    {
        var hash = Sign(code);
        var row = await db.Enrollments.FirstOrDefaultAsync(e => e.CodeHash == hash, ct);

        if (row is null || row.RedeemedAt is not null || row.ExpiresAt <= time.GetUtcNow())
        {
            throw ConnectorException.InvalidRequest("enrollment code is unknown, used or expired");
        }

        row.RedeemedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>Generates the long-lived agent token. Returned once; only its hash is kept.</summary>
    public static string NewToken() =>
        "agtk_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private string Sign(string code)
    {
        var key = _options.EnrollmentHmacKey;
        if (string.IsNullOrEmpty(key))
        {
            // Development only; production start-up refuses a missing key.
            return Hash(code);
        }

        return "hmac:" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(code)));
    }
}

public readonly record struct AgentEnrollmentCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Applied to the whole <c>/agent/v1</c> group except enrollment, which is
/// authenticated by the one-time code instead.
/// </summary>
public sealed class AgentAuthFilter : IEndpointFilter
{
    /// <summary>Where the resolved agent lands for the handlers behind this filter.</summary>
    public const string ItemKey = "connector.agent";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var auth = context.HttpContext.RequestServices.GetRequiredService<AgentAuth>();
        var agent = await auth.ResolveAsync(context.HttpContext, context.HttpContext.RequestAborted);

        if (agent is null)
        {
            return ConnectorResults.Error(new ConnectorHttpException(
                StatusCodes.Status401Unauthorized,
                ConnectorException.InvalidRequest("unknown or revoked agent")));
        }

        context.HttpContext.Items[ItemKey] = agent;
        return await next(context);
    }
}

public static class AgentContextExtensions
{
    /// <summary>The agent behind the current request. Only reachable behind <see cref="AgentAuthFilter"/>.</summary>
    public static AgentRow RequireAgent(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[AgentAuthFilter.ItemKey] as AgentRow
               ?? throw ConnectorException.InvalidRequest("no agent on this request");
    }
}
