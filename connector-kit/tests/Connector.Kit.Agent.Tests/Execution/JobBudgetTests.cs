using Connector.Kit.Agent.Execution;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// The rule, in isolation: parked time is not spent time, and nothing else
/// buys an extension.
/// </summary>
public class JobBudgetTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task A_job_that_only_works_dies_when_its_budget_runs_out()
    {
        using var budget = new JobBudget(Budget, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var watching = budget.WatchAsync(stop.Token);

        await watching.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(budget.Expired);
        Assert.Equal(TimeSpan.Zero, budget.HumanWait);
    }

    [Fact]
    public async Task Time_parked_on_a_human_does_not_count_against_the_budget()
    {
        using var budget = new JobBudget(Budget, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var watching = budget.WatchAsync(stop.Token);

        using (budget.Park())
        {
            // Four times the whole budget, parked. A wall-clock deadline would
            // have killed this three times over.
            await Task.Delay(Budget * 4);
            Assert.False(budget.Expired);
        }

        await watching.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(budget.Expired);
        Assert.True(budget.HumanWait >= Budget * 3, $"human wait was only {budget.HumanWait}");
    }

    [Fact]
    public async Task The_budget_resumes_the_moment_the_answer_arrives()
    {
        using var budget = new JobBudget(TimeSpan.FromSeconds(30), TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var watching = budget.WatchAsync(stop.Token);

        using (budget.Park()) await Task.Delay(200);

        // Still alive, and now working again rather than parked: the reported
        // wait must stop growing once the scope is gone.
        var afterPark = budget.HumanWait;
        await Task.Delay(200);

        Assert.False(budget.Expired);
        Assert.Equal(afterPark, budget.HumanWait);

        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(budget.Expired);
    }

    [Fact]
    public async Task Overlapping_parks_cannot_leave_the_budget_suspended()
    {
        using var budget = new JobBudget(Budget, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var watching = budget.WatchAsync(stop.Token);

        var outer = budget.Park();
        var inner = budget.Park();
        inner.Dispose();

        // One scope still open, so the clock is still stopped.
        await Task.Delay(Budget * 2);
        Assert.False(budget.Expired);

        outer.Dispose();
        await watching.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(budget.Expired);
    }

    [Fact]
    public async Task A_job_that_ends_first_is_never_reported_as_a_timeout()
    {
        using var budget = new JobBudget(TimeSpan.FromSeconds(30), TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var watching = budget.WatchAsync(stop.Token);

        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(budget.Expired);
    }
}
