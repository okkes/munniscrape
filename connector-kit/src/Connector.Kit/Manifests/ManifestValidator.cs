using System.Text.RegularExpressions;

namespace Connector.Kit.Manifests;

/// <summary>
/// Manifests are the public contract, so they are validated at startup and
/// a bad one refuses to boot. A manifest that lies - claiming unattended
/// operation it cannot deliver, or a TTL longer than the session really
/// lasts - makes the consuming app promise things to users and then fail,
/// which is worse than not supporting the provider at all.
/// </summary>
public static partial class ManifestValidator
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern { get; }

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern { get; }

    public static void Validate(ProviderManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var errors = new List<string>();

        if (!IdPattern.IsMatch(m.Id)) errors.Add($"id '{m.Id}' must be lowercase kebab-case");
        if (!CountryPattern.IsMatch(m.Country)) errors.Add($"country '{m.Country}' must be ISO-3166 alpha-2 uppercase");
        if (m.ManifestVersion < 1) errors.Add("manifest_version must be >= 1");
        if (m.Resources.Count == 0) errors.Add("at least one resource is required");

        ValidateCustody(m, errors);
        ValidateAgent(m, errors);
        ValidateAuth(m, errors);
        ValidateResources(m, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"provider manifest '{m.Id}' is invalid:{Environment.NewLine}- " +
                string.Join(Environment.NewLine + "- ", errors));
        }
    }

    private static void ValidateCustody(ProviderManifest m, List<string> errors)
    {
        // Storing a secret that cannot be used without a human present buys
        // risk and no feature, so the combination is refused outright.
        if (m.SecretCustody == SecretCustody.Server && !m.UnattendedFetch)
        {
            errors.Add("secret_custody 'server' requires unattended_fetch: true - " +
                       "storing a secret that needs a human to use is pure risk");
        }

        // Agent custody means the control plane holds nothing at all, which
        // only makes sense when a specific machine holds the session.
        if (m.SecretCustody == SecretCustody.Agent && m.Runtime != ProviderRuntime.BrowserPersistent)
        {
            errors.Add("secret_custody 'agent' requires runtime 'browser_persistent'");
        }

        if (m.SecretCustody == SecretCustody.Agent && m.Agent.Class != AgentClass.Byo)
        {
            errors.Add("secret_custody 'agent' requires agent.class 'byo'");
        }
    }

    private static void ValidateAgent(ProviderManifest m, List<string> errors)
    {
        // A browser cannot run in the control plane: it has no browser
        // binaries, and running one there would put provider traffic on the
        // wrong egress entirely.
        if (m.Runtime != ProviderRuntime.Http && !m.Agent.Required)
        {
            errors.Add($"runtime '{m.Runtime}' needs an agent; only 'http' may run inline");
        }

        if (m.Agent is { Required: false, Class: not AgentClass.Inline })
        {
            errors.Add("agent.required false implies agent.class 'inline'");
        }

        if (m.Runtime == ProviderRuntime.BrowserPersistent && m.Agent.Class != AgentClass.Byo)
        {
            errors.Add("runtime 'browser_persistent' is BYO-only: a persistent login " +
                       "belongs on hardware the user controls");
        }

        // "A human must be at the browser" is meaningless where there is no
        // browser. Refused at boot rather than left to read as a mysterious
        // routing constraint on a provider that only ever speaks HTTP.
        if (m.LoginNeedsHeadedAgent && m.Runtime == ProviderRuntime.Http)
        {
            errors.Add("login_needs_headed_agent has no meaning on runtime 'http': " +
                       "there is no browser for anyone to sit at");
        }
    }

    private static void ValidateAuth(ProviderManifest m, List<string> errors)
    {
        var auth = m.Auth;

        if (auth.Session.TtlSeconds <= 0) errors.Add("auth.session.ttl_seconds must be positive");

        // device_persistent has no credential step - the human authenticated
        // once, directly into a profile on their own machine.
        if (auth.Flow == AuthFlow.DevicePersistent)
        {
            if (m.Runtime != ProviderRuntime.BrowserPersistent)
            {
                errors.Add("flow 'device_persistent' requires runtime 'browser_persistent'");
            }
        }
        else if (auth.Flow == AuthFlow.RemoteBrowser)
        {
            // The inverse rule, and the reason this flow is worth having as a
            // flow rather than a convention: a streamed login must be INCAPABLE
            // of asking for a credential. A field here would mean a form
            // somewhere, and a form means a password crossing the wire and
            // resting in a job's inputs - the whole thing this exists to stop.
            // Refusing it at boot makes that structural instead of a promise.
            if (auth.AllFields().Any())
            {
                errors.Add("flow 'remote_browser' must declare no fields: the human types into the " +
                           "provider's own page, so a field here would be a credential this platform " +
                           "collects and then claims not to hold");
            }

            if (m.Runtime == ProviderRuntime.Http)
            {
                errors.Add("flow 'remote_browser' needs a browser to stream, so runtime 'http' cannot serve it");
            }
        }
        else if (auth.Steps.Count == 0)
        {
            errors.Add($"flow '{auth.Flow}' requires at least one auth step");
        }

        if (m.UnattendedFetch && !auth.Session.Refreshable && auth.Flow != AuthFlow.DevicePersistent)
        {
            errors.Add("unattended_fetch: true requires a refreshable session - " +
                       "without one a human is needed every time by definition");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in auth.AllFields())
        {
            if (!keys.Add(field.Key)) errors.Add($"duplicate field key '{field.Key}'");

            if (field.Type == FieldType.Select && (field.Options is null || field.Options.Count == 0))
            {
                errors.Add($"field '{field.Key}' is a select but has no options");
            }

            if (field.Pattern is not null && !IsValidRegex(field.Pattern))
            {
                errors.Add($"field '{field.Key}' has an invalid pattern");
            }
        }

        // A password that is not marked secret would be logged and
        // screenshotted, because redaction keys off exactly this flag.
        foreach (var field in auth.AllFields().Where(f => f.Type == FieldType.Password && !f.Secret))
        {
            errors.Add($"field '{field.Key}' is a password but is not marked secret");
        }
    }

    private static void ValidateResources(ProviderManifest m, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in m.Resources)
        {
            if (!IdPattern.IsMatch(resource.Id)) errors.Add($"resource id '{resource.Id}' must be kebab-case");
            if (!ids.Add(resource.Id)) errors.Add($"duplicate resource id '{resource.Id}'");
            if (resource.MaxRecordsPerFetch <= 0) errors.Add($"resource '{resource.Id}' max records must be positive");

            foreach (var p in resource.Params)
            {
                if (p.Type == ParamType.Enum && (p.Values is null || p.Values.Count == 0))
                {
                    errors.Add($"param '{resource.Id}.{p.Key}' is an enum but lists no values");
                }

                if (p is { Required: true, Internal: true })
                {
                    errors.Add($"param '{resource.Id}.{p.Key}' cannot be both required and internal");
                }
            }
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = Regex.Match(string.Empty, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
