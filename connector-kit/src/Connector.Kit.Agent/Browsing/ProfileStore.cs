using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// The T4 persistent-profile registry: one directory per connection, and the
/// health the heartbeat reports so the control plane can route a session back
/// to the machine that actually holds it.
///
/// Health is persisted rather than kept in memory, because "your ASN sync is
/// stale because your VM has been off for three days" is only a real message
/// if the agent still knows when the profile last worked after a restart.
/// </summary>
public sealed partial class ProfileStore
{
    private const string HealthFileName = "profiles.json";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeIdPattern { get; }

    private readonly string _root;
    private readonly ILogger<ProfileStore> _logger;
    private readonly ConcurrentDictionary<string, ProfileHealth> _health = new(StringComparer.Ordinal);
    private readonly Lock _writeLock = new();

    public ProfileStore(string rootDirectory, ILogger<ProfileStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _root = Path.GetFullPath(rootDirectory);
        _logger = logger;

        Directory.CreateDirectory(_root);
        Restore();
    }

    public string RootDirectory => _root;

    /// <summary>
    /// The directory this profile lives in, created on first use. The id comes
    /// off the wire and becomes a path segment, so it is validated rather than
    /// trusted - a profile id of <c>../../</c> would otherwise be a directory
    /// traversal handed to us by whoever can reach the lease endpoint.
    /// </summary>
    public string DirectoryFor(string profileId, string providerId)
    {
        EnsureSafeId(profileId);

        var directory = Path.Combine(_root, profileId);
        Directory.CreateDirectory(directory);

        _health.GetOrAdd(profileId, id => new ProfileHealth
        {
            Id = id,
            Provider = providerId,
            Healthy = true,
        });

        return directory;
    }

    public void MarkOk(string profileId, string providerId, DateTimeOffset at)
    {
        EnsureSafeId(profileId);
        _health[profileId] = new ProfileHealth
        {
            Id = profileId,
            Provider = providerId,
            Healthy = true,
            LastOk = at,
        };
        Persist();
    }

    /// <summary>
    /// The profile no longer authenticates. Kept on disk rather than deleted:
    /// only a human can re-authenticate a persistent profile, and throwing the
    /// directory away would also throw away everything that made that login
    /// cheap the next time they do.
    /// </summary>
    public void MarkUnhealthy(string profileId, string providerId)
    {
        EnsureSafeId(profileId);
        var previous = _health.TryGetValue(profileId, out var existing) ? existing.LastOk : null;
        _health[profileId] = new ProfileHealth
        {
            Id = profileId,
            Provider = providerId,
            Healthy = false,
            LastOk = previous,
        };
        Persist();
    }

    public IReadOnlyList<ProfileHealth> Snapshot() =>
        [.. _health.Values.OrderBy(p => p.Id, StringComparer.Ordinal)];

    /// <summary>
    /// What a revoked agent does. The profiles hold live provider sessions
    /// that the control plane can no longer route to, and leaving them on disk
    /// after the user has revoked the agent is exactly the residue this whole
    /// custody model exists to avoid.
    /// </summary>
    public void WipeAll()
    {
        lock (_writeLock)
        {
            _health.Clear();

            foreach (var directory in SafeEnumerate())
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "could not wipe profile directory {Directory}", directory);
                }
            }

            try
            {
                var file = Path.Combine(_root, HealthFileName);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "could not delete the profile health file");
            }
        }

        _logger.LogWarning("all persistent browser profiles have been wiped");
    }

    private IEnumerable<string> SafeEnumerate()
    {
        try
        {
            return Directory.EnumerateDirectories(_root).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "could not enumerate {Root}", _root);
            return [];
        }
    }

    private void Restore()
    {
        var file = Path.Combine(_root, HealthFileName);
        if (!File.Exists(file)) return;

        try
        {
            var stored = JsonSerializer.Deserialize<ProfileHealth[]>(File.ReadAllText(file), Transport.AgentJson.Options);
            foreach (var profile in stored ?? [])
            {
                if (SafeIdPattern.IsMatch(profile.Id)) _health[profile.Id] = profile;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "profile health at {File} is unreadable; profiles start unknown", file);
        }
    }

    private void Persist()
    {
        lock (_writeLock)
        {
            try
            {
                var file = Path.Combine(_root, HealthFileName);
                File.WriteAllText(file, JsonSerializer.Serialize(Snapshot(), Transport.AgentJson.Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "could not persist profile health");
            }
        }
    }

    private static void EnsureSafeId(string profileId)
    {
        if (!SafeIdPattern.IsMatch(profileId))
        {
            throw ConnectorException.InvalidRequest($"profile id '{profileId}' is not a legal path segment");
        }
    }
}
