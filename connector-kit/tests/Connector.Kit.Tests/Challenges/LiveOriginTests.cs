using Connector.Kit.Challenges;
using Xunit;

namespace Connector.Kit.Tests.Challenges;

/// <summary>
/// The one anti-phishing affordance the streamed login has.
///
/// The human is looking at a photograph of a page on a machine they cannot see,
/// so there is no address bar and no padlock. This string is the only evidence
/// they get about whose password box they are filling in, which makes every one
/// of these cases a security case rather than a formatting one.
/// </summary>
public sealed class LiveOriginTests
{
    [Theory]
    [InlineData("https://login.ah.nl/login?client_id=x&code=secret", "https://login.ah.nl")]
    [InlineData("https://login.ah.nl:8443/login", "https://login.ah.nl:8443")]
    [InlineData("http://localhost:8420/", "http://localhost:8420")]
    [InlineData("https://LOGIN.AH.NL/Login", "https://login.ah.nl")]
    public void An_origin_is_the_origin_and_never_the_rest_of_the_url(string url, string expected) =>
        Assert.Equal(expected, LiveOrigin.Normalize(url));

    /// <summary>
    /// The query string is where a login puts its authorization code, and this
    /// value is rendered on screen and travels through a relay. Origin only is
    /// not tidiness.
    /// </summary>
    [Fact]
    public void A_secret_in_the_query_string_never_reaches_the_screen()
    {
        var origin = LiveOrigin.Normalize("https://login.ah.nl/cb?code=abc123&state=xyz");

        Assert.Equal("https://login.ah.nl", origin);
        Assert.DoesNotContain("abc123", origin, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case this label exists for.
    ///
    /// "аh.nl" here begins with a Cyrillic а (U+0430). Rendered as unicode it is
    /// indistinguishable from "ah.nl" at any font size, on any screen, to
    /// anybody - which would turn the one thing a person checks before typing a
    /// password into an active lie. Browsers show the punycode form for exactly
    /// this reason.
    /// </summary>
    [Fact]
    public void A_lookalike_domain_is_shown_as_punycode_rather_than_as_the_real_thing()
    {
        var origin = LiveOrigin.Normalize("https://аh.nl/login");

        Assert.Equal("https://xn--h-7sb.nl", origin);

        // The assertion that carries the security property, stated separately
        // so it survives anyone correcting the exact encoding above.
        Assert.NotEqual("https://ah.nl", origin);
        Assert.StartsWith("https://xn--", origin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not somebody's sign-in page. Showing one of these where a domain belongs
    /// would dress up "we have no idea what this is" as an answer.
    /// </summary>
    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<h1>Sign in</h1>")]
    [InlineData("file:///tmp/login.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("chrome://newtab")]
    public void A_scheme_that_is_not_a_website_has_no_origin_to_show(string url) =>
        Assert.Null(LiveOrigin.Normalize(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/login")]
    public void Anything_unparseable_reads_as_unknown_rather_than_as_itself(string? url) =>
        Assert.Null(LiveOrigin.Normalize(url));

    /// <summary>
    /// Bounded before parsing. A host is 253 characters at most by DNS, so this
    /// is not a hostname and is not worth handing to a URL parser.
    /// </summary>
    [Fact]
    public void An_absurdly_long_url_is_refused_rather_than_truncated()
    {
        var url = "https://" + new string('a', LiveOrigin.MaxLength) + ".nl";

        Assert.Null(LiveOrigin.Normalize(url));
    }
}
