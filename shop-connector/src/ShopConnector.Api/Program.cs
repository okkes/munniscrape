using Connector.Kit.Hosting;
using Connector.Kit.Manifests;
using ShopConnector.Adapters;

var builder = WebApplication.CreateBuilder(args);

// Every unconfirmed provider fact - endpoints, client ids, login selectors,
// money units - hangs off this one section, so correcting one after a
// provider moves is a deploy-time edit rather than a release.
var providers = builder.Configuration.GetSection("ShopAdapters").Get<ShopAdapterOptions>()
                ?? new ShopAdapterOptions();

builder.Services.AddConnectorPlatform(builder.Configuration, platform =>
{
    platform.ServiceKind = ProviderKind.Store;

    // The image stamps its tag here, so an operator reading a catalogue
    // response can tell which build answered the call.
    if (builder.Configuration["BUILD_NUMBER"] is { Length: > 0 } build) platform.ServiceVersion = build;

    // The whole fleet, mocks included. The catalogue is the only contract the
    // consumer codes against, and a provider missing from it cannot be
    // connected at all - not even through an agent that could serve it. The
    // inline runner filters itself down to `agent.required: false` providers,
    // so registering a browser tier here never means this process leasing
    // work it has no browser for.
    foreach (var adapter in ShopAdapters.All(providers)) platform.AddAdapter(adapter);
});

var app = builder.Build();

app.UseConnectorPlatform();
app.MapConnectorReference();
app.MapConnectorApi();
app.MapAgentApi();

await app.RunAsync();
