using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Microsoft.Playwright;

namespace RegistryConnector.Adapters.Tests;

/// <summary>
/// A lease that hands back a cookie jar and nothing else.
///
/// The jar is the point. BKR's session IS its cookie - B2C gives the portal an
/// id_token by form_post and the browser is never handed a token worth
/// storing - so a login that does not carry the jar out looks entirely
/// successful and leaves every later fetch signed out.
/// </summary>
internal sealed class StubBrowserLease : IBrowserLease
{
    public const string Jar = """{"cookies":[{"name":"portal","value":"fixture"}],"origins":[]}""";

    public bool Started => false;

    public Task<IPage> PageAsync(CancellationToken ct) =>
        throw new NotSupportedException("the offline suite drives the page seam, not a browser");

    public Task<string> StorageStateAsync(CancellationToken ct) => Task.FromResult(Jar);

    public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct) =>
        Task.FromResult(Array.Empty<byte>());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
