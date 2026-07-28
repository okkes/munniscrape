using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent.Transport;

/// <summary>
/// The enrollment state file.
///
/// It exists so that an agent enrolls exactly once: the enrollment code is
/// one-time and HMAC-signed, so a restart that re-enrolled would leave a BYO
/// user unable to bring their agent back without asking for a new code.
/// </summary>
public sealed class AgentStateStore
{
    private readonly string _path;
    private readonly ILogger<AgentStateStore> _logger;
    private readonly Lock _writeLock = new();

    public AgentStateStore(string path, ILogger<AgentStateStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        _path = Path.GetFullPath(path);
        _logger = logger;
    }

    public string FilePath => _path;

    /// <summary>
    /// Returns the stored enrollment, or null when there is none, it is
    /// unreadable, or it belongs to a different control plane. A corrupt file
    /// is treated as "not enrolled" rather than fatal - the agent can always
    /// re-enroll with a fresh code, and refusing to boot helps nobody.
    /// </summary>
    public AgentEnrollment? Load(string controlPlane)
    {
        if (!File.Exists(_path)) return null;

        AgentEnrollment? state;
        try
        {
            state = JsonSerializer.Deserialize<AgentEnrollment>(File.ReadAllText(_path), AgentJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "agent state at {Path} is unreadable; treating the agent as unenrolled", _path);
            return null;
        }

        if (state is null) return null;

        if (!string.Equals(state.ControlPlane, controlPlane, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "agent state at {Path} was issued by {Stored}, not {Configured}; re-enrollment is required",
                _path, state.ControlPlane, controlPlane);
            return null;
        }

        return state;
    }

    public void Save(AgentEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);

        lock (_writeLock)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Written whole then moved into place: a torn state file would look
            // like a lost enrollment and burn the user's one-time code.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(enrollment, AgentJson.Options));
            RestrictToOwner(temp);
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_writeLock)
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "could not delete agent state at {Path}; the revoked token is still on disk", _path);
            }
        }
    }

    /// <summary>
    /// The file holds a bearer token, so it is owner-only where the platform
    /// has a notion of that. On Windows the inherited ACL of a per-user data
    /// directory is the equivalent guarantee.
    /// </summary>
    private void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "could not restrict permissions on {Path}", path);
        }
    }
}
