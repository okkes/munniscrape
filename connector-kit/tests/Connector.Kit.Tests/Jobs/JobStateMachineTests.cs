using Connector.Kit.Jobs;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// The job machine encodes the requeue rule: a dead agent's job returns to
/// the queue exactly once, and never after a credential has gone upstream.
/// That "exactly once" is enforced by the queue's attempt count, but the
/// legality of the edge at all lives here.
/// </summary>
public sealed class JobStateMachineTests
{
    private static readonly JobState[] All = Enum.GetValues<JobState>();

    public static TheoryData<JobState> EveryState()
    {
        var data = new TheoryData<JobState>();
        foreach (var state in All) data.Add(state);
        return data;
    }

    public static TheoryData<JobState> TerminalStates()
    {
        var data = new TheoryData<JobState>();
        data.Add(JobState.Succeeded);
        data.Add(JobState.Failed);
        data.Add(JobState.Expired);
        return data;
    }

    [Theory]
    // the normal run
    [InlineData(JobState.Queued, JobState.Leased)]
    [InlineData(JobState.Leased, JobState.Running)]
    [InlineData(JobState.Running, JobState.AwaitingInput)]
    [InlineData(JobState.AwaitingInput, JobState.Running)]
    [InlineData(JobState.Running, JobState.Succeeded)]
    // a lost lease returns the job to the queue - from either side of the
    // moment the agent actually started work
    [InlineData(JobState.Leased, JobState.Queued)]
    [InlineData(JobState.Running, JobState.Queued)]
    // failure and expiry from every live state
    [InlineData(JobState.Queued, JobState.Failed)]
    [InlineData(JobState.Leased, JobState.Failed)]
    [InlineData(JobState.Running, JobState.Failed)]
    [InlineData(JobState.AwaitingInput, JobState.Failed)]
    [InlineData(JobState.Queued, JobState.Expired)]
    [InlineData(JobState.Leased, JobState.Expired)]
    [InlineData(JobState.Running, JobState.Expired)]
    [InlineData(JobState.AwaitingInput, JobState.Expired)]
    public void Allows_a_legal_transition(JobState from, JobState to)
    {
        Assert.True(JobStateMachine.CanTransition(from, to));
        JobStateMachine.EnsureTransition(from, to);
    }

    [Theory]
    // work never starts without a lease: an unleased job running is two
    // agents on one login
    [InlineData(JobState.Queued, JobState.Running)]
    [InlineData(JobState.Queued, JobState.Succeeded)]
    [InlineData(JobState.Queued, JobState.AwaitingInput)]
    // a lease is not a result
    [InlineData(JobState.Leased, JobState.Succeeded)]
    [InlineData(JobState.Leased, JobState.AwaitingInput)]
    // an answer resumes the run; it does not finish it
    [InlineData(JobState.AwaitingInput, JobState.Succeeded)]
    [InlineData(JobState.AwaitingInput, JobState.Queued)]
    [InlineData(JobState.AwaitingInput, JobState.Leased)]
    // no going back to a lease
    [InlineData(JobState.Running, JobState.Leased)]
    // terminal is terminal - re-running a succeeded login is the lockout path
    [InlineData(JobState.Succeeded, JobState.Queued)]
    [InlineData(JobState.Succeeded, JobState.Running)]
    [InlineData(JobState.Failed, JobState.Queued)]
    [InlineData(JobState.Failed, JobState.Leased)]
    [InlineData(JobState.Expired, JobState.Queued)]
    public void Refuses_an_illegal_transition(JobState from, JobState to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => JobStateMachine.EnsureTransition(from, to));
    }

    [Theory]
    [MemberData(nameof(TerminalStates))]
    public void A_terminal_state_has_no_exits(JobState terminal)
    {
        Assert.True(JobStateMachine.IsTerminal(terminal));
        Assert.All(All, to => Assert.False(JobStateMachine.CanTransition(terminal, to)));
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void Terminal_means_exactly_no_exits(JobState state)
    {
        var hasExit = All.Any(to => JobStateMachine.CanTransition(state, to));
        Assert.Equal(!JobStateMachine.IsTerminal(state), hasExit);
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void No_state_transitions_to_itself(JobState state)
    {
        Assert.False(JobStateMachine.CanTransition(state, state));
    }

    [Fact]
    public void Every_state_is_reachable_from_queued()
    {
        var seen = new HashSet<JobState> { JobState.Queued };
        var frontier = new Queue<JobState>();
        frontier.Enqueue(JobState.Queued);

        while (frontier.Count > 0)
        {
            var from = frontier.Dequeue();
            foreach (var to in All.Where(t => JobStateMachine.CanTransition(from, t) && seen.Add(t)))
            {
                frontier.Enqueue(to);
            }
        }

        Assert.Equal(All.Order().ToArray(), seen.Order().ToArray());
    }

    [Fact]
    public void The_requeue_walk_is_legal_and_ends_somewhere_terminal()
    {
        // Leased, the agent died, requeued, leased again, ran, failed. The
        // queue is what stops this looping; the machine only has to permit
        // the shape.
        JobState[] walk =
        [
            JobState.Queued, JobState.Leased, JobState.Running, JobState.Queued,
            JobState.Leased, JobState.Running, JobState.Failed,
        ];

        for (var i = 1; i < walk.Length; i++) JobStateMachine.EnsureTransition(walk[i - 1], walk[i]);

        Assert.True(JobStateMachine.IsTerminal(walk[^1]));
    }

    [Fact]
    public void The_challenge_walk_is_legal_end_to_end()
    {
        JobState[] walk =
        [
            JobState.Queued, JobState.Leased, JobState.Running,
            JobState.AwaitingInput, JobState.Running, JobState.Succeeded,
        ];

        for (var i = 1; i < walk.Length; i++) JobStateMachine.EnsureTransition(walk[i - 1], walk[i]);
    }

    [Fact]
    public void The_error_names_the_transition_it_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JobStateMachine.EnsureTransition(JobState.Succeeded, JobState.Queued));

        Assert.Contains("Succeeded", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Queued", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Login_is_the_only_kind_that_carries_a_credential()
    {
        // Not a state rule, but the reason the requeue edge is dangerous:
        // it is only Login that can lock an account by being repeated.
        Assert.Equal(
            new[] { JobKind.Login, JobKind.Fetch, JobKind.Refresh, JobKind.Logout },
            Enum.GetValues<JobKind>());
    }

    [Fact]
    public void The_progress_vocabulary_is_the_documented_closed_set()
    {
        // Legal step values are published in connector-api-spec.md §2.2. A
        // consumer renders and translates these, so adding one is a
        // contract change and removing one breaks a rendered progress bar.
        Assert.Equal(
            new[]
            {
                JobStep.Queued, JobStep.AgentAssigned, JobStep.OpeningProvider, JobStep.Authenticating,
                JobStep.AwaitingHuman, JobStep.SelectingAccounts, JobStep.Downloading, JobStep.Parsing,
                JobStep.Normalizing, JobStep.Finalizing, JobStep.LoggingOut,
            },
            Enum.GetValues<JobStep>());
    }
}
