namespace RegistryConnector.Adapters.Tests;

/// <summary>
/// A clock that does not move. TOTP is a function of the time, so a test that
/// asserted a code against the real clock would pass for thirty seconds and
/// then fail for ever.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
