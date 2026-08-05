namespace RegistryConnector.Adapters.Support;

/// <summary>
/// Keys, never prose. A connector emits an identifier and the consuming app
/// owns the words - so a Dutch build, a screen-reader build and a plain-text
/// build all read differently from the same connector.
/// </summary>
internal static class MessageKeys
{
    public const string StepCredentials = "connect.step.credentials";

    public const string FieldEmail = "connect.field.email";
    public const string FieldPassword = "connect.field.password";

    /// <summary>
    /// The streamed sign-in. Shared wording with the shop connectors on
    /// purpose: it is the same thing happening, and a person who has met it
    /// once should recognise it.
    /// </summary>
    public const string LiveLogin = "connect.challenge.live_login";

    /// <summary>Six digits from the authenticator, asked for rather than computed.</summary>
    public const string BkrCode = "connect.challenge.authenticator_code";

    /// <summary>
    /// The seed field's label, and the one string here that has to carry a
    /// warning rather than a name. What it is asking for is the secret behind
    /// every code the account will ever accept.
    /// </summary>
    public const string BkrTotpSecret = "connect.bkr.totp";

    public const string BkrNotes = "connect.bkr.notes";
}
