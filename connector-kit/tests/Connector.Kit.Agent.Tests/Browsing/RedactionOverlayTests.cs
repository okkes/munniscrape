using Connector.Kit.Agent.Browsing;
using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// The redaction mask, against a real browser, because the defect it exists to
/// prevent is a fact about how Playwright PAINTS and cannot be reproduced
/// against a fake.
///
/// Observed on a live Albert Heijn login: the relayed hCaptcha arrived with a
/// black bar across "Klik op de kortste lijn", and the account's owner could
/// not tell what they were being asked. The cause is not z-order. Playwright
/// paints each mask into an <c>x-pw-glass</c> element declared
/// <c>popover: manual</c> - the browser's top layer - from the target's
/// <c>getBoundingClientRect()</c>. Nothing on the page can be stacked above it,
/// so the mask for the password field UNDERNEATH a captcha overlay lands
/// squarely on the question.
///
/// These tests pin the way out: conceal the element ourselves so it paints
/// nothing, and drop only the mask for what we concealed. Revert
/// <c>ConcealAsync</c> and the first test fails on a black picture.
/// </summary>
public sealed class RedactionOverlayTests : IAsyncLifetime
{
    /// <summary>Distinctive on purpose: any pixel of it proves the overlay was photographed.</summary>
    private const string OverlayColour = "#008080";

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
    /// A password field under an overlay must not black out the overlay.
    ///
    /// Asserted by comparing against the same page WITHOUT the field: if no
    /// mask was painted, the two pictures are the same pixels. Both elements
    /// are absolutely positioned so the concealed input - which keeps its box,
    /// deliberately, so nothing reflows - cannot move the overlay either way.
    /// </summary>
    [Fact]
    public async Task An_overlay_covering_a_password_field_is_photographed_intact()
    {
        var crop = new CropRegion(40, 40, 260, 120);

        var withField = await CaptureAsync(Markup(withPassword: true), crop);
        var withoutField = await CaptureAsync(Markup(withPassword: false), crop);

        Assert.NotEmpty(withField);
        Assert.NotEmpty(withoutField);

        // Byte-equal, not merely similar: a mask would paint a solid black
        // rectangle across the middle of one of them.
        Assert.Equal(withoutField, withField);
    }

    /// <summary>
    /// The property the mask existed to provide, unchanged: a page still
    /// holding a secret is not photographed at all, concealment or no.
    /// </summary>
    [Fact]
    public async Task A_page_still_holding_a_secret_is_refused()
    {
        var page = await NewPageAsync(Markup(withPassword: true, value: "hunter2"));
        var redactor = new ScreenshotRedactor(ManifestWithSecret(), NullLogger.Instance);

        Assert.False(await redactor.IsSafeToCaptureAsync(page, CancellationToken.None));
        Assert.Empty(await redactor.CaptureAsync(page, null, CancellationToken.None));
    }

    /// <summary>
    /// The page is handed back as it was found. An adapter goes on to type
    /// into this form, so a field left invisible would strand the login in a
    /// way no error message would explain.
    /// </summary>
    [Fact]
    public async Task The_field_is_visible_again_afterwards()
    {
        var page = await NewPageAsync(Markup(withPassword: true));
        var redactor = new ScreenshotRedactor(ManifestWithSecret(), NullLogger.Instance);

        await redactor.CaptureAsync(page, new CropRegion(40, 40, 260, 120), CancellationToken.None);

        var visibility = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('#password')).visibility");
        var marker = await page.EvaluateAsync<bool>(
            "() => document.querySelector('#password').hasAttribute('data-connector-vis')");

        Assert.Equal("visible", visibility);
        Assert.False(marker);
    }

    private async Task<byte[]> CaptureAsync(string html, CropRegion crop)
    {
        var page = await NewPageAsync(html);
        var redactor = new ScreenshotRedactor(ManifestWithSecret(), NullLogger.Instance);
        return await redactor.CaptureAsync(page, crop, CancellationToken.None);
    }

    private async Task<IPage> NewPageAsync(string html)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 400, Height = 300 },
            DeviceScaleFactor = 1,
        });

        var page = await context.NewPageAsync();
        await page.SetContentAsync(html);
        return page;
    }

    /// <summary>
    /// A password box at a fixed spot, and an opaque panel over it - the shape
    /// of every login a provider fronts with a captcha.
    /// </summary>
    private static string Markup(bool withPassword, string value = "")
    {
        var field = withPassword
            ? "<input id='password' type='password' autocomplete='current-password' value='" + value + "' "
              + "style='position:absolute;left:60px;top:70px;width:200px;height:40px' />"
            : string.Empty;

        return "<!doctype html><html><body style='margin:0;background:#ffffff'>"
            + field
            + "<div style='position:absolute;left:40px;top:40px;width:260px;height:120px;background:"
            + OverlayColour + ";color:#ffffff;font:16px sans-serif'>Klik op de kortste lijn</div>"
            + "</body></html>";
    }

    /// <summary>
    /// The shared rig's manifest already declares a secret field keyed
    /// <c>password</c>, which is what the fixture's input is named. Reused
    /// rather than rebuilt so this test cannot quietly drift from the shape
    /// every other agent test redacts against.
    /// </summary>
    private static ProviderManifest ManifestWithSecret() => TestRig.Manifest;
}
