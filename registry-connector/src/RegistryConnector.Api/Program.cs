using Connector.Kit.Hosting;
using Connector.Kit.Manifests;
using RegistryConnector.Adapters;

var builder = WebApplication.CreateBuilder(args);

// Every unconfirmed provider fact - endpoints, login selectors, the words a
// registry uses for a credit type - hangs off this one section, so correcting
// one after a provider moves is a deploy-time edit rather than a release.
var providers = builder.Configuration.GetSection("RegistryAdapters").Get<RegistryAdapterOptions>()
                ?? new RegistryAdapterOptions();

builder.Services.AddConnectorPlatform(builder.Configuration, platform =>
{
    platform.ServiceKind = ProviderKind.Registry;

    // The image stamps its tag here, so an operator reading a catalogue
    // response can tell which build answered the call.
    if (builder.Configuration["BUILD_NUMBER"] is { Length: > 0 } build) platform.ServiceVersion = build;

    foreach (var adapter in RegistryAdapters.All(providers)) platform.AddAdapter(adapter);
});

var app = builder.Build();

app.UseConnectorPlatform();
app.MapConnectorReference();
app.MapConnectorApi();
app.MapAgentApi();

await app.RunAsync();
