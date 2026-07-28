namespace Connector.Kit.Tests;

/// <summary>
/// A clock that only moves when a test moves it.
///
/// Every expiry rule in the kit is a comparison against
/// <see cref="TimeProvider.GetUtcNow"/>. Testing those against the wall
/// clock means either sleeping or asserting a boundary you cannot reach,
/// so the boundary cases - "expires exactly now" - would go untested.
/// </summary>
internal sealed class TestTime : TimeProvider
{
    public TestTime(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>An arbitrary fixed instant, shared so fixtures line up.</summary>
    public static DateTimeOffset Anchor { get; } = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    public static TestTime AtAnchor() => new(Anchor);
}
