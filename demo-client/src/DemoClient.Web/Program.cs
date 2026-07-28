using DemoClient.Web.Relay;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration, and the refusals that go with it ─────────────────────────

var relay = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
var demo = builder.Configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new DemoOptions();

if (relay.Problems() is { Count: > 0 } problems)
{
    // Failing here is cheap. A relay that boots with two identical subject
    // salts looks perfectly healthy while handing both connectors the same
    // pseudonym for the same person, which is the one privacy property this
    // whole topology exists to provide.
    throw new InvalidOperationException(
        "the relay refuses to start:" + Environment.NewLine + "- " +
        string.Join(Environment.NewLine + "- ", problems));
}

builder.Services.AddSingleton(relay);
builder.Services.AddSingleton(demo);
builder.Services.AddSingleton<SubjectMinter>();
builder.Services.AddSingleton<ConnectorClients>();
builder.Services.AddSingleton<PrefillStore>();
builder.Services.AddSingleton<RelayExceptionFilter>();

// One HTTP client per connector. Named rather than typed because there are two
// instances of one type; see ConnectorClients.
foreach (var (service, endpoint) in relay.Connectors)
{
    builder.Services
        .AddHttpClient(ConnectorClients.HttpClientName(service), http =>
        {
            http.BaseAddress = new Uri(endpoint.BaseUrl, UriKind.Absolute);

            // Infinite here, bounded per request in ConnectorClient. The SSE
            // proxy shares this client, and HttpClient.Timeout would abort a
            // live event stream partway through a login.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

    // Production: .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    //     { SslOptions = { ClientCertificates = [cert from ClientCertificatePath] } })
    // pairs the client certificate with the M2M bearer token ConnectorClient
    // would attach. Both layers, because either one alone is a weak answer.
}

// Snake case on the way in and out, so the relay's surface and the connectors'
// read as one API rather than two conventions meeting in the middle.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = RelayJson.Options.PropertyNamingPolicy;
    json.SerializerOptions.DictionaryKeyPolicy = RelayJson.Options.DictionaryKeyPolicy;
    json.SerializerOptions.DefaultIgnoreCondition = RelayJson.Options.DefaultIgnoreCondition;

    foreach (var converter in RelayJson.Options.Converters)
    {
        json.SerializerOptions.Converters.Add(converter);
    }
});

var app = builder.Build();

// ── The SPA ─────────────────────────────────────────────────────────────────

// index.html at /, then the rest of wwwroot. Plain HTML, CSS and ES modules -
// no bundler, no npm, no build step. `dotnet run` is the whole toolchain.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapRelayApi();

// ── Observable once, at startup ─────────────────────────────────────────────

// Two salts, two subjects, one human. Printing them is the cheapest possible
// demonstration that the connectors cannot correlate the user between them.
var startup = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Relay");
var minter = app.Services.GetRequiredService<SubjectMinter>();

foreach (var client in app.Services.GetRequiredService<ConnectorClients>().All)
{
    startup.LogInformation("{Service} -> {BaseUrl} as subject {Subject}",
        client.Service, client.Options.BaseUrl, minter.For(client.Service));
}

await app.RunAsync();
