using Connector.Kit.Hosting;
using Connector.Kit.Hosting.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// <c>Connector:DevEnrollmentCode</c> is a fixed, reusable, ten-year enrollment
/// code that exists so an agent in a compose file can enroll with nobody
/// present. That is a password by any other name, and it is written in a file
/// in this repository.
///
/// So the two properties worth testing are not the happy path. They are: it
/// keeps working across restarts and wiped volumes (otherwise the stack it
/// exists for breaks the second time anyone runs it), and it cannot be present
/// in production at all.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class DevEnrollmentTests(ShopApiFactory factory)
{
    [Fact]
    public async Task The_development_code_survives_being_redeemed()
    {
        var code = $"AGNT-DEV0-{Guid.NewGuid():N}"[..24];

        await SeedAsync(code);
        var first = await RedeemAsync(code);

        // The agent's state file and the control plane's database are two
        // separate volumes. Clearing either one - a stuck agent state deleted
        // by hand, a rebuilt agent image, a compose file brought down while its
        // database volume survives - sends the agent back to enrollment with a
        // code that a one-time rule would already have spent forever.
        await SeedAsync(code);
        var second = await RedeemAsync(code);

        Assert.Equal(ConnectorOptions.DevFleetSubject, first.Subject);
        Assert.Equal(ConnectorOptions.DevFleetSubject, second.Subject);
    }

    /// <summary>
    /// Re-seeding is what makes it reusable; a single seed must still behave
    /// like every other enrollment code, or the dev path and the real path
    /// would be different code with different rules.
    /// </summary>
    [Fact]
    public async Task One_seeding_is_still_only_one_redemption()
    {
        var code = $"AGNT-ONCE-{Guid.NewGuid():N}"[..24];

        await SeedAsync(code);
        await RedeemAsync(code);

        await Assert.ThrowsAnyAsync<Exception>(() => RedeemAsync(code));
    }

    [Fact]
    public void A_production_connector_refuses_to_start_carrying_a_development_enrollment_code()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddConnectorPlatform(Production(devCode: "AGNT-DEV0-LOCAL")));

        Assert.Contains("Connector:DevEnrollmentCode", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same configuration without the code must fail for its OTHER reasons
    /// and not this one - otherwise the test above would pass against a
    /// production check that refuses everything.
    /// </summary>
    [Fact]
    public void A_production_connector_without_one_is_not_refused_for_this_reason()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddConnectorPlatform(Production(devCode: null)));

        Assert.DoesNotContain("Connector:DevEnrollmentCode", failure.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Production(string? devCode)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Connector:Mode"] = "Production",
        };

        if (devCode is not null) settings["Connector:DevEnrollmentCode"] = devCode;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private async Task SeedAsync(string code)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AgentAuth>()
            .SeedDevEnrollmentAsync(code, CancellationToken.None);
    }

    private async Task<Connector.Kit.Hosting.Data.EnrollmentRow> RedeemAsync(string code)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AgentAuth>()
            .RedeemEnrollmentAsync(code, CancellationToken.None);
    }
}
