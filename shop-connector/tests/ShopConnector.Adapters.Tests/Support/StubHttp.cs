using System.Net;
using System.Text;

namespace ShopConnector.Adapters.Tests.Support;

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public string Path => Uri.AbsolutePath;

    public string Query => Uri.Query;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A provider, recorded.
///
/// The responder sees the request and its zero-based ordinal, so a test can
/// say "the first list call 401s and the second succeeds" without a mutable
/// counter of its own - which is exactly the shape the refresh-and-retry
/// path has to be driven through.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, int, HttpResponseMessage> _respond;
    private readonly List<RecordedRequest> _requests = [];

    public StubHttpHandler(Func<RecordedRequest, int, HttpResponseMessage> respond) => _respond = respond;

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        // NonValidated, not the typed collections. Enumerating request.Headers
        // re-renders structured headers from their PARSED form, which splits a
        // User-Agent into its product tokens and rejoins them with ", " - so a
        // test reading it that way sees "Appie/9.28, (iPhone17,3; …)" and fails
        // against a header that goes onto the wire perfectly intact (verified
        // against a raw socket). NonValidated reports what was actually set,
        // which is the only thing worth asserting on.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers.NonValidated)
        {
            headers[name] = values.ToString();
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers.NonValidated)
            {
                headers[name] = values.ToString();
            }
        }

        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, headers);
        _requests.Add(recorded);

        return _respond(recorded, _requests.Count - 1);
    }
}

/// <summary>
/// The handler a provider that must never be contacted gets.
///
/// It counts as well as throws: an adapter that swallowed the exception
/// would still be caught by the count, and "made zero network calls" is the
/// claim, not "did not crash".
/// </summary>
internal sealed class ThrowingHttpHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Calls++;

        throw new InvalidOperationException(
            $"this adapter must not make network calls, but it requested {request.Method} {request.RequestUri}");
    }
}

internal static class Stub
{
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Fixture(string name, HttpStatusCode status = HttpStatusCode.OK) =>
        Json(Adapters.Fixtures.FixtureCatalog.Read(name), status);

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") };

    /// <summary>An interstitial: a block page or a login redirect where JSON was promised.</summary>
    public static HttpResponseMessage Html(string body = "<html><body>blocked</body></html>") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
}
