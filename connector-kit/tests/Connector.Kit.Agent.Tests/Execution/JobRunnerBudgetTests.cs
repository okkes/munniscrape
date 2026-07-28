using Connector.Kit.Errors;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// What the whole runner does about a human who is not at the desk yet.
///
/// The incident these exist for: an interactive widget went up, the adapter
/// parked on it exactly as designed, and the job's own 300s budget killed the
/// run and called it <c>internal</c> - a run that was behaving perfectly,
/// reported as our bug, while somebody was still walking over to answer it.
/// </summary>
public class JobRunnerBudgetTests
{
    [Fact]
    public async Task A_job_parked_on_a_challenge_outlives_its_own_budget()
    {
        // One second of budget, three seconds of human. The old wall-clock
        // deadline lost this race every time.
        using var rig = new TestRig(ScriptedAdapter.AsksAHuman(TimeSpan.FromMinutes(5)));
        var job = TestRig.Login(budgetSeconds: 1);

        var running = rig.RunAsync(job);
        await Task.Delay(3_000);

        Assert.Null(rig.Control.Failure);
        rig.Control.Answer();

        await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, rig.Control.ChallengesRaised);
        Assert.Null(rig.Control.Failure);
        Assert.NotNull(rig.Control.Result);
    }

    [Fact]
    public async Task An_adapter_that_raises_no_challenge_still_dies_on_its_budget()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges());
        var job = TestRig.Login(budgetSeconds: 1);

        await rig.RunAsync(job).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, rig.Control.ChallengesRaised);
        Assert.NotNull(rig.Control.Failure);
        Assert.Null(rig.Control.Result);
    }

    [Fact]
    public async Task A_run_that_uses_up_its_budget_is_not_reported_as_internal()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges());

        await rig.RunAsync(TestRig.Login(budgetSeconds: 1)).WaitAsync(TimeSpan.FromSeconds(30));

        // `internal` is retriable AND says "ours, not theirs": it pages an
        // operator over a run nobody can fix and renders as nothing a user can
        // act on. A budget that only counts work can only run out on work.
        Assert.True(rig.Control.FailedWith(ErrorCode.ProviderUnavailable),
            $"expected provider_unavailable, got {rig.Control.FailureCode}");
        Assert.NotEqual(ErrorCatalog.Wire(ErrorCode.Internal), rig.Control.FailureCode);

        // And the detail says which budget and how much of the wait was a
        // human's, so a real timeout stays diagnosable.
        Assert.Contains("working budget", rig.Control.Failure!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parked_job_keeps_its_lease_alive()
    {
        // The renewal loop fires at half the remaining lease with a five-second
        // floor, so a short lease and a twelve-second wait is the cheapest way
        // to see it go round more than once.
        using var rig = new TestRig(ScriptedAdapter.AsksAHuman(TimeSpan.FromMinutes(5)));
        var job = TestRig.Login(budgetSeconds: 1, leaseSeconds: 10);

        var running = rig.RunAsync(job, leaseTtlSeconds: 10);
        await Task.Delay(12_000);

        var renewalsWhileParked = rig.Control.Renewals;
        rig.Control.Answer();
        await running.WaitAsync(TimeSpan.FromSeconds(30));

        // Without these the control plane's expiry sweep takes the job back
        // mid-wait and hands it to a second agent, which would undo everything
        // the suspended budget bought.
        Assert.True(renewalsWhileParked >= 2,
            $"a job parked for 12s renewed its lease only {renewalsWhileParked} time(s)");
        Assert.Null(rig.Control.Failure);
    }

    [Fact]
    public async Task A_challenge_nobody_answers_still_ends_the_run_on_its_own_expiry()
    {
        // The other half of the bargain: suspending the budget is only safe
        // because the challenge carries its own mandatory deadline.
        using var rig = new TestRig(ScriptedAdapter.AsksAHuman(TimeSpan.FromSeconds(2)));

        await rig.RunAsync(TestRig.Login(budgetSeconds: 30)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, rig.Control.ChallengesRaised);
        Assert.True(rig.Control.FailedWith(ErrorCode.MfaTimeout),
            $"expected mfa_timeout, got {rig.Control.FailureCode}");
    }
}
