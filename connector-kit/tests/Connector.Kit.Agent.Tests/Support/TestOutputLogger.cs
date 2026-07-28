using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// A logger that writes into the test's output.
///
/// It exists for one reason: the live view's per-second cost is written down by
/// a log line, and a measurement nobody can read is not a measurement. Running
/// the session under <c>NullLogger</c> would throw away the only number the
/// feature is judged on.
/// </summary>
internal sealed class TestOutputLogger(ITestOutputHelper output) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(level)) return;

        try
        {
            output.WriteLine($"{level,-11} {formatter(state, exception)}");
        }
        catch (InvalidOperationException)
        {
            // A background loop that logs after the test has finished is not a
            // test failure, and an exception from here would surface as one in
            // whatever unrelated test is running next.
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
