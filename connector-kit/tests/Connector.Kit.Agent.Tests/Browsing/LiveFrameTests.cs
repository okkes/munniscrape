using Connector.Kit.Agent.Browsing;
using Connector.Kit.Challenges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// The live shutter, against a real browser, because every claim it makes is a
/// claim about what Chromium and Playwright actually do and a fake would simply
/// agree with whatever we assumed.
///
/// Three of these are the fork in the redaction rule made visible: the same
/// page, still holding a password, is refused by the relay's capture and
/// photographed by the live one. Delete either half and one of them fails.
/// </summary>
public sealed class LiveFrameTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>
    /// The agent's browser is phone-sized so providers serve their mobile
    /// layout, and at scale factor 1 so one image pixel is one CSS pixel.
    /// </summary>
    private const int Width = 390;

    private const int Height = 844;

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    /// <summary>
    /// The size a frame advertises is the size its pixels are.
    ///
    /// This is the whole coordinate contract in one assertion. The consumer
    /// multiplies its fractions by the size in the header; if the encoder
    /// produced anything else - which is exactly what happens on a retina agent
    /// with the API's default <c>Scale = device</c> - every event the human
    /// makes lands at the difference, and nothing in the system notices.
    /// </summary>
    [Fact]
    public async Task A_live_frame_is_a_jpeg_at_the_size_it_says_it_is()
    {
        var page = await NewPageAsync(LoginForm());
        var capture = await Redactor().CaptureLiveFrameAsync(page, 60, CancellationToken.None);

        Assert.True(capture.Captured);
        Assert.True(Jpeg.Looks(capture.Jpeg), "the live path must produce a JPEG, never a PNG");
        Assert.Equal((Width, Height), (capture.Width, capture.Height));
        Assert.Equal((Width, Height), Jpeg.SizeOf(capture.Jpeg));

        output.WriteLine($"live frame at q60, {Width}x{Height}: {capture.Jpeg.Length} B");
    }

    /// <summary>
    /// A retina agent produces a frame at CSS size, not at device size.
    ///
    /// Playwright's <c>Scale</c> defaults to <c>device</c>, so without the
    /// explicit setting this page comes back 780x1688 while the header still
    /// says 390x844 - and every fraction the human sends is then multiplied by
    /// half the number it should be, putting every event in the top-left
    /// quadrant of the page. Nothing else in the system would notice.
    ///
    /// The agent runs at scale factor 1 today, which is exactly why this test
    /// is here: the bug is invisible until somebody runs a headed agent on a
    /// laptop, and then it is invisible again because it looks like a UI
    /// alignment problem.
    /// </summary>
    [Fact]
    public async Task A_retina_agent_still_produces_a_frame_at_css_size()
    {
        var page = await NewPageAsync(LoginForm(), deviceScaleFactor: 2);

        var capture = await Redactor().CaptureLiveFrameAsync(page, 60, CancellationToken.None);

        Assert.Equal((Width, Height), (capture.Width, capture.Height));
        Assert.Equal((Width, Height), Jpeg.SizeOf(capture.Jpeg));
    }

    /// <summary>
    /// The relay's refusal, unchanged. A page whose password box holds content
    /// produces no still - and every existing caller depends on that.
    /// </summary>
    [Fact]
    public async Task The_relay_still_refuses_a_page_that_holds_a_secret()
    {
        var page = await NewPageAsync(LoginForm(password: "hunter2"));
        var redactor = Redactor();

        Assert.False(await redactor.IsSafeToCaptureAsync(page, CancellationToken.None));
        Assert.Empty(await redactor.CaptureAsync(page, null, CancellationToken.None));
    }

    /// <summary>
    /// And the live path photographs that same page anyway.
    ///
    /// This is the inversion, and it is deliberately asserted right beside the
    /// test above so the pair reads as one decision. Refusing here would stream
    /// a black rectangle at the exact moment the feature exists for - the human
    /// is typing that password, on purpose, into the provider's own page - and
    /// what makes it acceptable is the custody change around it, not a softer
    /// rule: a streamed provider declares no credential fields, so nothing of
    /// ours was typed in, and the frame is never stored anywhere.
    /// </summary>
    [Fact]
    public async Task The_live_path_photographs_a_page_the_human_is_typing_into()
    {
        var page = await NewPageAsync(LoginForm(password: "hunter2"));

        var capture = await Redactor().CaptureLiveFrameAsync(page, 60, CancellationToken.None);

        Assert.True(capture.Captured);
        Assert.Equal((Width, Height), Jpeg.SizeOf(capture.Jpeg));
    }

    /// <summary>
    /// The live path leaves the page exactly as it found it.
    ///
    /// The relay hides secret-bearing elements for the length of a capture and
    /// puts them back; the live path never touches the DOM at all. Worth
    /// pinning, because the human is mid-login on this page and a field left
    /// invisible - or a <c>data-connector-vis</c> attribute left in the DOM of a
    /// page whose whole purpose is detecting automation - would be discovered
    /// by them and not by us.
    /// </summary>
    [Fact]
    public async Task The_live_path_does_not_touch_the_page_it_photographs()
    {
        var page = await NewPageAsync(LoginForm(password: "hunter2"));

        await Redactor().CaptureLiveFrameAsync(page, 60, CancellationToken.None);

        Assert.Equal("visible", await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('#password')).visibility"));
        Assert.False(await page.EvaluateAsync<bool>(
            "() => document.querySelector('#password').hasAttribute('data-connector-vis')"));
        Assert.Equal("hunter2", await page.EvaluateAsync<string>(
            "() => document.querySelector('#password').value"));
    }

    /// <summary>
    /// A page that did not change produces the same bytes, which is what makes
    /// hashing the encoded frame a valid change detector - and change detection
    /// is what the whole bandwidth argument rests on.
    ///
    /// It is also the test that catches somebody turning the caret back on: a
    /// blinking caret changes the pixels about twice a second forever, and this
    /// fails within one blink of that.
    /// </summary>
    [Fact]
    public async Task An_unchanged_page_is_photographed_byte_for_byte_identically()
    {
        var page = await NewPageAsync(LoginForm());
        await page.FocusAsync("#password");

        var redactor = Redactor();
        var first = await redactor.CaptureLiveFrameAsync(page, 60, CancellationToken.None);
        await Task.Delay(900);
        var second = await redactor.CaptureLiveFrameAsync(page, 60, CancellationToken.None);

        Assert.Equal(first.Jpeg, second.Jpeg);
    }

    /// <summary>
    /// A page that DID change produces different bytes, so the suppression
    /// cannot be passing merely because the shutter is stuck.
    /// </summary>
    [Fact]
    public async Task A_page_that_changed_is_photographed_differently()
    {
        var page = await NewPageAsync(LoginForm());
        var redactor = Redactor();

        var before = await redactor.CaptureLiveFrameAsync(page, 60, CancellationToken.None);
        await page.EvaluateAsync("() => document.querySelector('#error').textContent = 'Onjuist wachtwoord'");
        var after = await redactor.CaptureLiveFrameAsync(page, 60, CancellationToken.None);

        Assert.NotEqual(before.Jpeg, after.Jpeg);
    }

    /// <summary>
    /// A frame carries no size when there is no viewport to take one from, so
    /// it is refused. A guessed size is every subsequent event landing
    /// somewhere nobody chose, silently.
    /// </summary>
    [Fact]
    public async Task A_context_with_no_viewport_produces_no_frame()
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = ViewportSize.NoViewport,
        });

        var page = await context.NewPageAsync();
        await page.SetContentAsync(LoginForm());

        Assert.False((await Redactor().CaptureLiveFrameAsync(page, 60, CancellationToken.None)).Captured);
    }

    /// <summary>
    /// What the encoder choice actually costs, measured against PNG on both
    /// kinds of page, because the belief it was chosen on is only half true.
    ///
    /// "PNG of a phone-sized page is a quarter of a megabyte" is a statement
    /// about CONTENT and not about size. A login form is flat white with a few
    /// boxes on it, which is the case deflate was built for - measured here,
    /// PNG comes back SMALLER than JPEG q60. The moment there is a photograph
    /// on the page, which every real provider login has, that reverses by an
    /// order of magnitude, and it reverses harder still on the captcha grid
    /// this pipeline also has to carry.
    ///
    /// So the assertion is on the case the decision rests on, and both numbers
    /// go to the log, because the flat-form number is the one that says JPEG is
    /// not free and the encoder is worth measuring per stream later.
    /// </summary>
    [Fact]
    public async Task The_encoder_is_measured_rather_than_assumed()
    {
        var redactor = Redactor();
        var box = new CropRegion(0, 0, Width, Height);

        var flat = await NewPageAsync(LoginForm());
        var flatPng = await redactor.CaptureAsync(flat, box, CancellationToken.None);
        var flatJpeg = await redactor.CaptureLiveFrameAsync(flat, 60, CancellationToken.None);

        var busy = await NewPageAsync(PhotographicPage());
        var busyPng = await redactor.CaptureAsync(busy, box, CancellationToken.None);
        var busyJpeg = await redactor.CaptureLiveFrameAsync(busy, 60, CancellationToken.None);

        output.WriteLine(
            $"flat login form : PNG {flatPng.Length} B, JPEG q60 {flatJpeg.Jpeg.Length} B " +
            $"({(double)flatPng.Length / flatJpeg.Jpeg.Length:0.00}x)");
        output.WriteLine(
            $"page with a photo: PNG {busyPng.Length} B, JPEG q60 {busyJpeg.Jpeg.Length} B " +
            $"({(double)busyPng.Length / busyJpeg.Jpeg.Length:0.00}x)");

        Assert.NotEmpty(busyPng);
        Assert.True(busyJpeg.Jpeg.Length * 3 < busyPng.Length,
            $"JPEG q60 ({busyJpeg.Jpeg.Length} B) must beat PNG ({busyPng.Length} B) comfortably on real content");
    }

    private ScreenshotRedactor Redactor() => new(TestRig.Manifest, NullLogger.Instance);

    private async Task<IPage> NewPageAsync(string html, float deviceScaleFactor = 1)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = Width, Height = Height },
            DeviceScaleFactor = deviceScaleFactor,
        });

        var page = await context.NewPageAsync();
        await page.SetContentAsync(html);
        return page;
    }

    /// <summary>
    /// The shape of every provider login: a username in plain text, a password
    /// in dots, a place for an error message, and a submit button. Named
    /// <c>password</c> because that is the key the shared manifest declares
    /// secret, so the redactor has something to refuse.
    /// </summary>
    internal static string LoginForm(string password = "") =>
        "<!doctype html><html><body style='margin:0;background:#ffffff;font:16px sans-serif'>"
        + "<h1 style='margin:24px 16px'>Inloggen</h1>"
        + "<div id='error' style='margin:0 16px;color:#c00;min-height:24px'></div>"
        + "<input id='username' name='username' autocomplete='username' placeholder='E-mailadres' "
        + "style='display:block;margin:8px 16px;width:calc(100% - 32px);height:48px;font-size:16px' />"
        + "<input id='password' name='password' type='password' autocomplete='current-password' "
        + "value='" + password + "' placeholder='Wachtwoord' "
        + "style='display:block;margin:8px 16px;width:calc(100% - 32px);height:48px;font-size:16px' />"
        + "<button id='submit' style='margin:16px;width:calc(100% - 32px);height:48px'>Inloggen</button>"
        + "</body></html>";

    /// <summary>
    /// A page with a photograph on it, standing in for the hero image, the
    /// gradients and the antialiased type a real provider login is mostly made
    /// of. A SMOOTH field rather than per-pixel noise, and the difference
    /// matters: white noise is the worst case for both encoders and measures
    /// nothing anybody will ever ship, whereas a smooth field is the case where
    /// deflate finds no runs and the discrete cosine transform finds almost
    /// everything - which is what a photograph is.
    ///
    /// Drawn from a formula so it needs no asset and cannot change under the
    /// test.
    /// </summary>
    private static string PhotographicPage() =>
        "<!doctype html><html><body style='margin:0'><canvas id='c' width='" + Width + "' height='" + Height
        + "'></canvas><script>"
        + "const x=document.getElementById('c').getContext('2d');"
        + "const d=x.createImageData(" + Width + "," + Height + ");"
        + "for(let y=0;y<" + Height + ";y++){for(let w=0;w<" + Width + ";w++){"
        + "const i=(y*" + Width + "+w)*4;"
        + "d.data[i]=128+120*Math.sin(w/37)*Math.cos(y/53);"
        + "d.data[i+1]=128+120*Math.sin((w+y)/61);"
        + "d.data[i+2]=128+120*Math.cos(w/23)*Math.sin(y/91);"
        + "d.data[i+3]=255;}}"
        + "x.putImageData(d,0,0);</script></body></html>";
}
