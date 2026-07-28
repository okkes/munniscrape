namespace Connector.Kit.Agent.Tests;

/// <summary>
/// Whether anyone is actually there.
///
/// An hCaptcha is an interactive widget, not a picture: it wants drags and
/// tile clicks and mints its own token, so relaying a screenshot cannot answer
/// it however faithfully we photograph it. The only honest paths are "the
/// owner solves it in the window in front of them" and "say we are blocked",
/// and this flag is the entire basis on which an adapter picks one.
/// </summary>
public class AttendedTests
{
    [Fact]
    public async Task A_headed_agent_is_attended()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges(), headless: false);
        await using var context = rig.Context(TestRig.Login(budgetSeconds: 30));

        Assert.True(context.Attended);
    }

    [Fact]
    public async Task A_headless_agent_is_not_attended()
    {
        using var rig = new TestRig(ScriptedAdapter.Wedges(), headless: true);
        await using var context = rig.Context(TestRig.Login(budgetSeconds: 30));

        Assert.False(context.Attended);
    }

    [Fact]
    public async Task Headless_is_the_default_so_nobody_is_assumed_to_be_there()
    {
        // The dangerous direction is claiming a human when there is none: the
        // job hangs on a widget nobody can reach until its budget runs out,
        // which is the failure the flag was added to fix.
        Assert.True(new ConnectorAgentOptions().Headless);

        using var rig = new TestRig(ScriptedAdapter.Wedges());
        await using var context = rig.Context(TestRig.Login(budgetSeconds: 30));

        Assert.False(context.Attended);
    }
}
