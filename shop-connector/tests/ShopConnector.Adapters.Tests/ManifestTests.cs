using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.AlbertHeijn;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.LidlPlus;
using ShopConnector.Adapters.Mock;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The manifest is the only contract the consuming app codes against, so
/// each load-bearing fact from docs/shopping-connector-service.md is asserted
/// here by value rather than left to a reader to spot.
///
/// A manifest that lies is worse than an unsupported provider: it makes the
/// consumer promise something to a user and then fail. The three facts that
/// matter most are Jumbo's honest 24-hour non-refreshable session, Lidl's
/// unattended operation with its country/language config, and Albert Heijn's
/// version bump: its login stopped being a paste and became a password, so
/// every bundle minted under version 1 has to be refused.
/// </summary>
public sealed class ManifestTests
{
    private static readonly IProviderRegistry Registry = new ProviderRegistry(ShopAdapters.All());

    public static TheoryData<string> ProviderIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var manifest in Registry.Manifests) data.Add(manifest.Id);
            return data;
        }
    }

    [Fact]
    public void Registry_registers_every_shop_provider()
    {
        // Ordinal-sorted by the registry, so this list is alphabetical rather
        // than grouped by how anyone thinks about the providers.
        string[] expected =
        [
            "ah", "amazon-nl", "bol", "coolblue", "jumbo", "lidl",
            "magento-guest",
            "mock-store-broken", "mock-store-captcha", "mock-store-persistent",
            "mock-store-simple", "mock-store-slow", "mock-store-sms",
            "picnic", "woo-guest",
        ];

        Assert.Equal(expected, Registry.Manifests.Select(m => m.Id));
    }

    [Theory]
    [MemberData(nameof(ProviderIds))]
    public void Every_manifest_validates(string providerId)
    {
        var manifest = Registry.RequireManifest(providerId);

        // Throws with every failing rule listed; a manifest that cannot boot
        // the host must not pass the suite either.
        ManifestValidator.Validate(manifest);
    }

    [Theory]
    [MemberData(nameof(ProviderIds))]
    public void Every_provider_offers_the_receipts_resource_the_spec_documents(string providerId)
    {
        var manifest = Registry.RequireManifest(providerId);

        var receipts = manifest.Resource("receipts");
        Assert.NotNull(receipts);
        Assert.Equal(ResourceShape.Receipt, receipts.Returns);

        var since = receipts.Param("since");
        Assert.NotNull(since);
        Assert.Equal(ParamType.Date, since.Type);
        Assert.True(since.Required, "since is required on every receipts resource");

        var until = receipts.Param("until");
        Assert.NotNull(until);
        Assert.Equal(ParamType.Date, until.Type);

        var include = receipts.Param("include");
        Assert.NotNull(include);
        Assert.Equal(ParamType.Enum, include.Type);
        Assert.True(include.Multi, "include is comma-separated");
        Assert.Equal(new[] { "items" }, include.Values);
    }

    [Theory]
    [MemberData(nameof(ProviderIds))]
    public void Every_manifest_names_copy_keys_and_never_prose(string providerId)
    {
        var manifest = Registry.RequireManifest(providerId);

        // A connector never emits user-facing English. Every string a human
        // reads is a key the consuming app owns and translates.
        Assert.StartsWith("connect.", manifest.NotesKey, StringComparison.Ordinal);

        foreach (var step in manifest.Auth.Steps)
        {
            Assert.StartsWith("connect.", step.LabelKey, StringComparison.Ordinal);
        }

        foreach (var field in manifest.Auth.AllFields())
        {
            Assert.StartsWith("connect.", field.LabelKey, StringComparison.Ordinal);
        }
    }

    // ---- Jumbo: the correction the service plan makes ----------------------

    [Fact]
    public void Jumbo_declares_the_cookie_s_real_life_and_no_refresh_path()
    {
        var session = Registry.RequireManifest(JumboAdapter.ProviderId).Auth.Session;

        // The whole point of §3.3. Jumbo's credential is a browser cookie with
        // a ~24 hour life that this connector cannot renew; a manifest
        // claiming thirty days would make the consumer promise a month of
        // silent syncing and then fail every day after the first.
        //
        // The declaration is unchanged and the reason behind it is corrected.
        // It used to read "Jumbo has no refresh grant of any kind", and that
        // is false: auth.jumbo.com advertises refresh_token and offline_access
        // with PKCE S256, and the web client asks for offline_access by name.
        // What is true is narrower - that client's code is exchanged by
        // Jumbo's own backend and the browser is handed nothing but a cookie -
        // so there is a refresh path and we are not on it.
        Assert.Equal(86_400, session.TtlSeconds);
        Assert.False(session.Refreshable, "the web client never holds a refresh token; the backend keeps it");
        Assert.True(session.RotatesOnUse);
    }

    [Fact]
    public void Jumbo_is_an_interactive_browser_provider_on_a_residential_dutch_line()
    {
        var manifest = Registry.RequireManifest(JumboAdapter.ProviderId);

        Assert.Equal(ProviderRuntime.BrowserInteractive, manifest.Runtime);
        Assert.True(manifest.Agent.Required);
        Assert.Equal(AgentClass.Pooled, manifest.Agent.Class);
        Assert.NotNull(manifest.Agent.Egress);
        Assert.Equal("NL", manifest.Agent.Egress.Country);
        Assert.Equal("residential", manifest.Agent.Egress.Kind);

        // A human signs in roughly daily, so scheduled sync is not offerable
        // and the consumer must not offer it.
        Assert.False(manifest.Unattended);
        Assert.False(manifest.Auth.Reauth.Cheap);
        Assert.Equal(SecretCustody.Client, manifest.SecretCustody);
        Assert.Equal(AuthFlow.Password, manifest.Auth.Flow);

        // Image for a plain captcha, which can be photographed and typed back.
        // AppApproval for the interactive widgets Auth0 Universal Login
        // actually ships - hCaptcha, reCAPTCHA and Turnstile assets are all
        // CONFIRMED-live on jumbo.com's login page and Auth0 activates one on
        // risk score. A widget cannot be relayed, so the human passes it in
        // the window the agent opened and the challenge exists only to ask
        // them and wait. MfaCode because Auth0 supports a one-time code.
        //
        // A challenge the consumer has no UI for is the worst moment to
        // discover one, so all three are declared rather than found out.
        Assert.Equal(
            new[] { ChallengeType.Image, ChallengeType.AppApproval, ChallengeType.MfaCode },
            manifest.Auth.Challenges);
    }

    // ---- Lidl: the T2 flagship --------------------------------------------

    [Fact]
    public void Lidl_is_unattended_and_carries_country_and_language_as_config()
    {
        var manifest = Registry.RequireManifest(LidlPlusAdapter.ProviderId);

        // The refresh token works headlessly forever, which is what makes
        // scheduled sync offerable at all.
        Assert.True(manifest.Unattended);
        Assert.True(manifest.Auth.Session.Refreshable);
        Assert.Equal(7_776_000, manifest.Auth.Session.TtlSeconds);
        Assert.True(manifest.Auth.Reauth.Cheap);

        var config = manifest.Auth.Config;
        Assert.Equal(2, config.Count);

        var country = Assert.Single(config, f => f.Key == "country");
        Assert.Equal(FieldType.Select, country.Type);
        Assert.True(country.Required);
        Assert.False(country.Secret);
        Assert.Equal(new[] { "NL", "DE", "AT", "BE", "FR", "IT", "ES" }, country.Options);

        var language = Assert.Single(config, f => f.Key == "language");
        Assert.Equal(FieldType.Select, language.Type);
        Assert.True(language.Required);
        Assert.Equal(new[] { "nl", "de", "en", "fr", "it", "es" }, language.Options);
    }

    [Fact]
    public void Lidl_hands_the_sign_in_to_the_human_and_never_sees_a_password()
    {
        var manifest = Registry.RequireManifest(LidlPlusAdapter.ProviderId);

        // T1, and no agent. Lidl's reCAPTCHA Enterprise scores the BROWSER,
        // not the account: a live attempt on 2026-07-28 with correct
        // credentials and correct selectors was bounced back to the
        // identifier screen with a generic notice. No adapter change moves a
        // verdict about the browser, so the browser stopped being ours - the
        // human signs in on Lidl's page in their own, and hands back a code.
        Assert.Equal(ProviderRuntime.Http, manifest.Runtime);
        Assert.False(manifest.Agent.Required, "the human's browser does the sign-in");
        Assert.Equal(AuthFlow.OauthRedirect, manifest.Auth.Flow);

        // The headline, and the reason this is better rather than merely
        // different: there is no credential field at all. The password is
        // typed into Lidl's own page and only a single-use code comes back.
        Assert.DoesNotContain(manifest.Auth.AllFields(), f => f.Key == "password");
        Assert.DoesNotContain(manifest.Auth.AllFields(), f => f.Key == "username");
        Assert.DoesNotContain(manifest.Auth.AllFields(), f => f.Key == "phone");
        Assert.DoesNotContain(manifest.Auth.AllFields(), f => f.Type == FieldType.Password);

        // Only the redirect. The one-time code, any captcha and any device
        // check now happen inside the human's own browser, where they are
        // that browser's problem - which is exactly what makes this work.
        Assert.Equal(new[] { ChallengeType.Redirect }, manifest.Auth.Challenges);

        var step = Assert.Single(manifest.Auth.Steps);
        var redirect = Assert.Single(step.Fields);
        Assert.Equal("redirect_url", redirect.Key);

        // Secret because the pasted address carries a live authorization
        // code; optional because the authorize URL does not exist until the
        // challenge is raised, so nobody can supply it up front.
        Assert.True(redirect.Secret, "the pasted address carries a live code");
        Assert.False(redirect.Required);

        // Unchanged: country and language are still needed on every ticket
        // URL and header, and they are neither secret nor a challenge.
        var config = manifest.Auth.Config;
        Assert.Contains(config, f => f.Key == "country");
        Assert.Contains(config, f => f.Key == "language");

        // Still refreshable, and still unattended AFTER the one sign-in -
        // that is the whole point of taking the code rather than a password.
        Assert.True(manifest.Auth.Session.Refreshable);
        Assert.True(manifest.Unattended);

        var receipts = manifest.Resource("receipts");
        Assert.NotNull(receipts);
        Assert.Equal(730, receipts.MaxHistoryDays);
        Assert.Equal(21_600, manifest.Limits.MinIntervalSeconds);
        Assert.Equal(1, manifest.Limits.Concurrency);
    }

    // ---- Albert Heijn: the login that stopped needing a console ------------

    [Fact]
    public void Albert_heijn_drives_its_login_in_a_pooled_browser_on_a_dutch_residential_line()
    {
        var manifest = Registry.RequireManifest(AlbertHeijnAdapter.ProviderId);

        // T2, not T1. The predecessor made the human read a failed appie://
        // navigation out of their browser's console and paste it back, which
        // is impossible on a phone; the agent now types the credentials and
        // catches the redirect itself.
        Assert.Equal(ProviderRuntime.BrowserOnce, manifest.Runtime);
        Assert.True(manifest.Agent.Required);
        Assert.Equal(AgentClass.Pooled, manifest.Agent.Class);
        Assert.NotNull(manifest.Agent.Egress);
        Assert.Equal("NL", manifest.Agent.Egress.Country);
        Assert.Equal("residential", manifest.Agent.Egress.Kind);

        // The login is streamed now, so the flow says so. The agent and the
        // Dutch residential line stay exactly as they were: the browser being
        // driven by a human rather than by us does not change where it has to
        // appear to be.
        Assert.Equal(AuthFlow.RemoteBrowser, manifest.Auth.Flow);

        // Two, and the shortness is the change. The whole page is streamed, so
        // a captcha inside it is not a separate question needing its own
        // relay - the human is already looking at it with a pointer on it, and
        // so is an SMS box, an app-approval prompt, and whatever AH asks for
        // next that nobody here has seen. Redirect stays as the last resort.
        //
        // A consumer with no UI for a challenge its provider raises strands
        // the user, so these are declared rather than discovered.
        Assert.Equal(
            new[] { ChallengeType.LiveView, ChallengeType.Redirect },
            manifest.Auth.Challenges);

        // And the typed login still declares the three it needs, one flag away.
        Assert.Equal(
            new[] { ChallengeType.Image, ChallengeType.AppApproval, ChallengeType.Redirect },
            AlbertHeijnManifest.Build(liveLogin: false).Auth.Challenges);

        // The refresh token works headlessly, which is what makes scheduled
        // sync offerable at all.
        Assert.True(manifest.Unattended);
        Assert.Equal(SecretCustody.Client, manifest.SecretCustody);
        Assert.Equal(WebSupport.Ephemeral, manifest.WebSupport);
        Assert.Equal(7_776_000, manifest.Auth.Session.TtlSeconds);
        Assert.True(manifest.Auth.Session.Refreshable);
        Assert.True(manifest.Auth.Session.RotatesOnUse);
    }

    [Fact]
    public void Albert_heijn_asks_for_nothing_at_all_because_the_human_types_into_ahs_own_page()
    {
        var manifest = Registry.RequireManifest(AlbertHeijnAdapter.ProviderId);

        // The custody claim, as a property of the manifest rather than a
        // sentence in a design document: no field means no form, no form means
        // no credential posted to this platform, written to a job's inputs, or
        // held anywhere by anything here. ManifestValidator refuses a
        // remote_browser flow that declares one, so this cannot quietly regain
        // a password box.
        Assert.Empty(manifest.Auth.Steps);
        Assert.Empty(manifest.Auth.AllFields());

        // Nothing about the SHAPE of a bundle changed - it is still an access
        // token and a refresh token - so the version deliberately stays at 2.
        // Bumping it would log out every connected user to announce that a
        // password is now typed somewhere else.
        Assert.Equal(2, manifest.ManifestVersion);

        // The whole page is streamed, so a captcha inside it needs no separate
        // relay: the human is already looking at it with a pointer on it.
        Assert.Contains(ChallengeType.LiveView, manifest.Auth.Challenges);

        // And the typed login is still there, one flag away, for the day AH
        // changes something that breaks the streamed one.
        var typed = AlbertHeijnManifest.Build(liveLogin: false);
        Assert.Equal(AuthFlow.Password, typed.Auth.Flow);
        Assert.Equal(2, Assert.Single(typed.Auth.Steps).Fields.Count);
    }

    [Fact]
    public void The_typed_albert_heijn_login_still_asks_for_an_email_and_a_password_and_not_for_a_paste()
    {
        var manifest = AlbertHeijnManifest.Build(liveLogin: false);
        var step = Assert.Single(manifest.Auth.Steps);

        Assert.Equal(2, step.Fields.Count);
        Assert.DoesNotContain(manifest.Auth.AllFields(), f => f.Key == "redirect_url");

        // An e-mail address, not a member number or a phone: getting this
        // wrong in the consumer's form costs a failed attempt against a
        // defended page.
        var username = Assert.Single(step.Fields, f => f.Key == "username");
        Assert.Equal(FieldType.Text, username.Type);
        Assert.False(username.Secret);
        Assert.True(username.Required);

        var password = Assert.Single(step.Fields, f => f.Key == "password");
        Assert.Equal(FieldType.Password, password.Type);
        Assert.True(password.Secret, "an unmarked password is logged and screenshotted");
        Assert.True(password.Required);
    }

    // ---- Mocks: the offline backbone --------------------------------------

    [Fact]
    public void Mock_store_persistent_is_the_byo_case_that_holds_no_secret()
    {
        var manifest = Registry.RequireManifest(MockStoreAdapters.Persistent);

        Assert.Equal(ProviderRuntime.BrowserPersistent, manifest.Runtime);
        Assert.Equal(AgentClass.Byo, manifest.Agent.Class);
        Assert.True(manifest.Agent.Required);

        // Agent custody means the control plane holds nothing at all, which
        // is why the flow has no credential step.
        Assert.Equal(SecretCustody.Agent, manifest.SecretCustody);
        Assert.Equal(AuthFlow.DevicePersistent, manifest.Auth.Flow);
        Assert.Empty(manifest.Auth.Steps);
        Assert.True(manifest.Unattended);
    }

    [Theory]
    [InlineData(MockStoreAdapters.Simple)]
    [InlineData(MockStoreAdapters.Sms)]
    [InlineData(MockStoreAdapters.Captcha)]
    [InlineData(MockStoreAdapters.Slow)]
    [InlineData(MockStoreAdapters.Broken)]
    public void Mock_store_providers_other_than_persistent_run_inline_with_no_browser(string providerId)
    {
        var manifest = Registry.RequireManifest(providerId);

        // A mock that needed an agent or a browser would stop being usable as
        // the offline backbone the moment CI ran without one.
        Assert.Equal(ProviderRuntime.Http, manifest.Runtime);
        Assert.False(manifest.Agent.Required);
        Assert.Equal(AgentClass.Inline, manifest.Agent.Class);
        Assert.Equal(60, manifest.Limits.MinIntervalSeconds);
    }

    [Fact]
    public void Catalog_digest_changes_only_when_a_manifest_version_does()
    {
        var again = new ProviderRegistry(ShopAdapters.All());

        // The consumer caches the catalogue against this ETag, so it must be
        // stable across processes rather than merely within one.
        Assert.Equal(Registry.CatalogDigest, again.CatalogDigest);
        Assert.StartsWith("sha256:", Registry.CatalogDigest, StringComparison.Ordinal);
    }
}
