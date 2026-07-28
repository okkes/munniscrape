namespace ShopConnector.Adapters.Support;

/// <summary>
/// Every string an adapter puts in front of a human is a key here.
///
/// A connector never emits user-facing prose: it cannot know the reader's
/// language, and a literal that reaches a screen becomes an untranslatable
/// de-facto API the moment a consumer matches on it. The consuming app owns
/// the copy in every language it ships.
/// </summary>
internal static class MessageKeys
{
    public const string PasteRedirect = "connect.ah.paste_redirect";
    public const string SmsCode = "connect.challenge.sms_code";
    public const string Captcha = "connect.challenge.captcha";

    /// <summary>
    /// A one-time code, whichever way it arrived.
    ///
    /// Deliberately not <see cref="SmsCode"/>. A provider that lets a user
    /// sign in with either a phone number or an e-mail address sends the code
    /// the way it can reach them, so a prompt that says "the code we texted
    /// you" is wrong for half of them - and it is wrong in the one direction
    /// a user cannot recover from, because they go and stare at a phone that
    /// will never ring. The challenge's <c>delivery</c> field carries which
    /// it was; the copy behind this key is what the consumer renders around it.
    /// </summary>
    public const string VerificationCode = "connect.challenge.verification_code";

    /// <summary>
    /// The wall nobody can relay: an interactive widget is passed in the
    /// browser window the agent opened, by the person sitting in front of it,
    /// and there is nothing to type back at us.
    /// </summary>
    public const string CaptchaInBrowser = "connect.challenge.captcha_in_browser";

    public const string FieldEmail = "connect.field.email";
    public const string FieldPassword = "connect.field.password";

    /// <summary>
    /// "E-mail address or mobile number" - one box that takes either.
    ///
    /// Replaces Lidl's old phone-only key. A label that names only one of the
    /// two shapes the field accepts is how the repo owner ended up unable to
    /// connect an account they sign into every week.
    /// </summary>
    public const string FieldEmailOrPhone = "connect.field.email_or_phone";

    public const string LidlCountry = "connect.lidl.country";
    public const string LidlLanguage = "connect.lidl.language";

    public const string StepCredentials = "connect.step.credentials";
    public const string StepRedirect = "connect.step.redirect";

    /// <summary>
    /// Prompt for a redirect challenge. Provider-neutral: more than one
    /// provider now hands the sign-in to the human's own browser, and a key
    /// naming one of them would read as the wrong brand in the other's UI.
    /// </summary>
    public const string SignInOnProviderSite = "connect.challenge.sign_in_on_site";

    public const string JumboNotes = "connect.jumbo.notes";
    public const string AhNotes = "connect.ah.notes";
    public const string LidlNotes = "connect.lidl.notes";
    public const string MockNotes = "connect.mock.notes";
}
