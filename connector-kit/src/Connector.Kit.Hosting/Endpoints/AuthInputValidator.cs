using System.Text.RegularExpressions;
using Connector.Kit.Errors;
using Connector.Kit.Manifests;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// Validates a login payload against the manifest that described the form.
///
/// The consumer already validated client-side from the same
/// <see cref="FieldSpec"/>, and that is exactly why this exists: a
/// client-side check is a courtesy to the user, not a guarantee to us. A
/// field arriving that the manifest never declared means the caller is
/// working from a different contract, and passing it through to an adapter
/// would make the manifest advisory.
/// </summary>
public static class AuthInputValidator
{
    /// <summary>A pattern is a validator, not a puzzle; a runaway one must not become a stall.</summary>
    private static readonly TimeSpan PatternBudget = TimeSpan.FromMilliseconds(100);

    public static void ValidateInputs(ProviderManifest manifest, IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(inputs);

        // device_persistent has no credential step at all: the human
        // authenticated once, directly into a profile on their own machine.
        if (manifest.Auth.Flow == AuthFlow.DevicePersistent && inputs.Count > 0)
        {
            throw ConnectorException.InvalidRequest("this provider's login takes no inputs");
        }

        var declared = manifest.Auth.Steps.SelectMany(s => s.Fields).ToList();
        Validate(declared, inputs, "input");
    }

    public static void ValidateConfig(ProviderManifest manifest, IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(config);
        Validate(manifest.Auth.Config, config, "config");
    }

    private static void Validate(IReadOnlyList<FieldSpec> declared, IReadOnlyDictionary<string, string> supplied, string what)
    {
        var byKey = declared.ToDictionary(f => f.Key, StringComparer.Ordinal);

        foreach (var key in supplied.Keys)
        {
            if (!byKey.ContainsKey(key))
            {
                throw ConnectorException.InvalidRequest($"unknown {what} '{key}'");
            }
        }

        foreach (var field in declared)
        {
            if (!supplied.TryGetValue(field.Key, out var value) || string.IsNullOrEmpty(value))
            {
                if (field.Required) throw ConnectorException.InvalidRequest($"{what} '{field.Key}' is required");
                continue;
            }

            if (field.Type == FieldType.Select && field.Options is { } options &&
                !options.Contains(value, StringComparer.Ordinal))
            {
                // The value itself is echoed only for a select, where the
                // options are public and the value cannot be a secret.
                throw ConnectorException.InvalidRequest($"{what} '{field.Key}' does not accept '{value}'");
            }

            if (field.Pattern is { } pattern && !Matches(pattern, value))
            {
                // Never the value: this field may be a password, and an error
                // message is the most-logged string in any system.
                throw ConnectorException.InvalidRequest($"{what} '{field.Key}' does not match its declared pattern");
            }
        }
    }

    private static bool Matches(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, PatternBudget);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // The validator rejects a bad pattern at startup, so reaching here
            // means the manifest changed underneath us. Refusing the input is
            // the safe reading.
            return false;
        }
    }
}
