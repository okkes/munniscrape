using System.Text.Json;

namespace DemoClient.Web.Relay;

/// <summary>
/// What a developer already typed once, read back out of a local file.
///
/// This exists so that testing a real provider does not mean retyping a
/// password twenty times, and it is <b>development only</b> - the endpoint that
/// serves it is 404 anywhere else. A relay able to hand out stored credentials
/// on request in production is a credential exfiltration endpoint with a
/// friendly name.
///
/// The file shape mirrors the relay's own routes:
/// <code>
/// {
///   "shop": {
///     "lidl": {
///       "config": { "country": "NL", "language": "nl" },
///       "inputs": { "phone": "+31600000000", "password": "…" }
///     }
///   },
///   "bank": { "asn": { "inputs": { "customer_number": "…" } } }
/// }
/// </code>
/// </summary>
public sealed class PrefillStore(DemoOptions demo, IHostEnvironment environment, ILogger<PrefillStore> logger)
{
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public PrefillEntry? Find(string service, string provider)
    {
        var path = Locate();
        if (path is null) return null;

        Dictionary<string, Dictionary<string, PrefillEntry>>? file;

        try
        {
            using var stream = File.OpenRead(path);
            file = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PrefillEntry>>>(stream, FileOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken credentials file must not take the relay down; the
            // affected form simply comes up empty.
            logger.LogWarning("could not read {Path}: {Reason}", path, ex.Message);
            return null;
        }

        if (file is null ||
            !TryGet(file, service, out var providers) ||
            !TryGet(providers, provider, out var entry))
        {
            return null;
        }

        // The guard, not decoration. A bundle in this file would mean somebody
        // taught the relay to hold a session across requests, which is the one
        // property this demo exists to keep true.
        foreach (var (key, value) in entry.Config) BundleGuard.RejectBundle($"prefill {service}/{provider} config.{key}", value);
        foreach (var (key, value) in entry.Inputs) BundleGuard.RejectBundle($"prefill {service}/{provider} inputs.{key}", value);

        // Keys only. The values are the whole reason the file is gitignored.
        logger.LogInformation("prefill for {Service}/{Provider}: config [{Config}], inputs [{Inputs}]",
            service, provider, string.Join(", ", entry.Config.Keys), string.Join(", ", entry.Inputs.Keys));

        return entry;
    }

    /// <summary>
    /// Content root first, then upwards. `dotnet run` from the project
    /// directory and a file kept beside the solution are both normal, and
    /// making the developer care which is not worth a config option.
    /// Re-read per call, so editing the file does not need a restart.
    /// </summary>
    private string? Locate()
    {
        if (Path.IsPathRooted(demo.CredentialsFile))
        {
            return File.Exists(demo.CredentialsFile) ? demo.CredentialsFile : null;
        }

        var directory = new DirectoryInfo(environment.ContentRootPath);

        for (var depth = 0; depth < 5 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, demo.CredentialsFile);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static bool TryGet<TValue>(
        Dictionary<string, TValue> source, string key, out TValue value)
    {
        foreach (var (candidate, found) in source)
        {
            if (!string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)) continue;

            value = found;
            return true;
        }

        value = default!;
        return false;
    }
}

/// <summary>One provider's remembered answers. Exactly the login body's two halves.</summary>
public sealed record PrefillEntry
{
    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
