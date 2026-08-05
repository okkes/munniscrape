using Connector.Kit.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RegistryConnector.Adapters;

var builder = Host.CreateApplicationBuilder(args);

// The same section the control plane binds. It has to be here as well: an
// agent is where a selector actually gets used, so an unconfirmed value
// corrected on the API alone would be corrected nowhere that matters.
var providers = builder.Configuration.GetSection("RegistryAdapters").Get<RegistryAdapterOptions>()
                ?? new RegistryAdapterOptions();

builder.Services.AddConnectorAgent(builder.Configuration, agent =>
{
    foreach (var adapter in RegistryAdapters.All(providers)) agent.AddAdapter(adapter);
});

// The drain window plus the abort grace must fit inside the host's own
// shutdown timeout, which defaults to 30s and would otherwise stop waiting
// first. A dropped login is a browser left open on a provider with a
// half-submitted form and a session nothing will unstick until a TTL notices.
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(45));

await builder.Build().RunAsync();
