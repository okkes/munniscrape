namespace ShopConnector.Adapters.Bol;

/// <summary>
/// bol.com's copy keys.
///
/// Deliberately its own file rather than three more lines in
/// <see cref="Support.MessageKeys"/>: that file is shared by every provider
/// in the assembly, and a provider being added should not need to touch a
/// file another provider is being added in. The rule it exists to enforce is
/// kept exactly - every string a human reads is a key the consuming app owns
/// and translates, and a connector never emits user-facing prose.
/// </summary>
internal static class BolMessageKeys
{
    public const string BolNotes = "connect.bol.notes";

    /// <summary>
    /// The one-time code bol sends when it does not recognise the device.
    ///
    /// Not the sms key: bol's credential is an e-mail address, so the code is
    /// far more likely to arrive in a mailbox than on a phone - and telling
    /// someone to watch a phone that will never ring is the failure they
    /// cannot recover from, because the challenge expires while they wait.
    /// Which way it went is carried on the challenge's <c>delivery</c> field,
    /// decided per login rather than declared once here.
    /// </summary>
    public const string VerificationCode = "connect.challenge.verification_code";
}
