using Connector.Kit.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShopConnector.Adapters;

var builder = Host.CreateApplicationBuilder(args);

// The same section the control plane binds. It has to be here as well: an
// agent is where a selector actually gets used, so an unconfirmed value
// corrected on the API alone would be corrected nowhere that matters.
var providers = builder.Configuration.GetSection("ShopAdapters").Get<ShopAdapterOptions>()
                ?? new ShopAdapterOptions();

builder.Services.AddConnectorAgent(builder.Configuration, agent =>
{
    // Every adapter, every tier. Which of them this instance actually serves
    // is decided by the runtimes it advertises (see appsettings.json), so the
    // operator's residential agent and a user's own BYO box run the identical
    // image - which is what made bring-your-own hardware a configuration
    // change rather than a redesign.
    foreach (var adapter in ShopAdapters.All(providers)) agent.AddAdapter(adapter);
});

// The drain window plus the abort grace must fit inside the host's own
// shutdown timeout, which defaults to 30s and would otherwise stop waiting
// first. A dropped login is a browser left open on a provider with a
// half-submitted form and a session nothing will unstick until a TTL notices.
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(45));

await builder.Build().RunAsync();
