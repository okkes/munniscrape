using BankConnector.Adapters;
using Connector.Kit.Hosting;
using Connector.Kit.Manifests;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConnectorPlatform(builder.Configuration, platform =>
{
    platform.ServiceKind = ProviderKind.Bank;

    // The image stamps its tag here, so an operator reading a catalogue
    // response can tell which build answered the call.
    if (builder.Configuration["BUILD_NUMBER"] is { Length: > 0 } build) platform.ServiceVersion = build;

    // The whole fleet, not the inline-capable subset. The catalogue is the
    // only contract the consumer codes against, and a provider missing from
    // it cannot be connected at all - not even through an agent that could
    // serve it. The inline runner filters itself down to `agent.required:
    // false` providers, so registering a browser tier here never means this
    // process leasing work it has no browser for.
    foreach (var adapter in BankAdapters.MockFleet()) platform.AddAdapter(adapter);
});

var app = builder.Build();

app.UseConnectorPlatform();
app.MapConnectorApi();
app.MapAgentApi();

await app.RunAsync();
