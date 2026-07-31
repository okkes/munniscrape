using Connector.Kit.Manifests;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// A manifest is the only contract a consumer codes against, so a manifest
/// that lies - claiming unattended operation it cannot deliver, or storing
/// a secret it cannot use without a human - makes the consuming app promise
/// things to users and then fail. Worse than not supporting the provider.
/// </summary>
public sealed class ManifestValidatorTests
{
    private static InvalidOperationException Rejects(ProviderManifest manifest) =>
        Assert.Throws<InvalidOperationException>(() => ManifestValidator.Validate(manifest));

    [Fact]
    public void The_baseline_fixture_is_valid()
    {
        // Every rejection test below mutates exactly one thing off this, so
        // if the baseline were invalid the whole file would prove nothing.
        ManifestValidator.Validate(Make.Manifest());
    }

    // ── custody ──────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_server_custody_without_unattended()
    {
        // Storing a secret that needs a human present to use buys risk and
        // no feature.
        var ex = Rejects(Make.Manifest() with { SecretCustody = SecretCustody.Server, UnattendedFetch = false });

        // Named for the axis it is actually about. The rule is a claim on the
        // FETCH loop - a stored secret nothing can use without a human is pure
        // risk - and the old name let it read as one about the login.
        Assert.Contains("unattended_fetch: true", ex.Message, StringComparison.Ordinal);
    }

    // ── the two axes ─────────────────────────────────────────────────────

    /// <summary>
    /// "A human must be at the browser" is meaningless where there is no
    /// browser, and a manifest that says it is describing a routing constraint
    /// nothing can satisfy.
    /// </summary>
    [Fact]
    public void Rejects_a_headed_login_requirement_on_a_provider_with_no_browser()
    {
        var ex = Rejects(Make.Manifest() with { LoginNeedsHeadedAgent = true });

        Assert.Contains("login_needs_headed_agent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("there is no browser for anyone to sit at", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The combination the split exists for: a provider whose stored token
    /// fetches on its own overnight AND whose login can still meet a wall only
    /// somebody at that machine can pass. One flag could not say both.
    /// </summary>
    [Fact]
    public void Accepts_a_provider_that_fetches_unattended_but_needs_a_human_at_the_browser_to_connect()
    {
        ManifestValidator.Validate(
            (Make.Manifest() with
            {
                Runtime = ProviderRuntime.BrowserOnce,
                Agent = new AgentRequirement { Required = true, Class = AgentClass.Pooled },
                UnattendedFetch = true,
                LoginNeedsHeadedAgent = true,
            })
            .WithSession(new SessionSpec { TtlSeconds = 86_400, Refreshable = true }));
    }

    [Fact]
    public void A_manifest_says_nothing_happens_upstream_on_disconnect_unless_it_says_so()
    {
        // The default is the safe claim: most adapters inherit a logout that
        // does nothing, and a consumer must not tell a user otherwise.
        Assert.Equal(LogoutSupport.None, Make.Manifest().Logout);
    }

    // ── the credential store ─────────────────────────────────────────────

    /// <summary>
    /// Jumbo's shape, and the only one it is for: client custody, a password
    /// form to store, and a session that cannot be refreshed - so without it
    /// the same human types the same password every morning.
    /// </summary>
    [Fact]
    public void Accepts_a_credential_store_on_a_password_login_whose_session_cannot_be_refreshed()
    {
        ManifestValidator.Validate(Make.Manifest() with { OffersCredentialStore = true });
    }

    [Fact]
    public void A_manifest_offers_no_credential_store_unless_it_says_so()
    {
        // A stored password is the heaviest thing this platform holds on
        // somebody's behalf. Opting in has to be written down.
        Assert.False(Make.Manifest().OffersCredentialStore);
    }

    /// <summary>
    /// The refresh is what already stops the human being asked again, so
    /// storing a password on top buys nothing and adds the one credential that
    /// does not rotate and cannot be revoked from here.
    /// </summary>
    [Fact]
    public void Rejects_a_credential_store_where_the_session_refreshes_itself()
    {
        var ex = Rejects(
            (Make.Manifest() with { OffersCredentialStore = true })
            .WithSession(new SessionSpec { TtlSeconds = 86_400, Refreshable = true }));

        Assert.Contains("offers_credential_store is refused on a refreshable session", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A streamed login collects nothing, so there is nothing to seal. A
    /// manifest offering a store here advertises a bundle that always arrives
    /// empty.
    /// </summary>
    [Fact]
    public void Rejects_a_credential_store_on_a_login_that_collects_nothing()
    {
        var manifest = Make.Manifest() with
        {
            OffersCredentialStore = true,
            Runtime = ProviderRuntime.BrowserOnce,
            Agent = new AgentRequirement { Required = true, Class = AgentClass.Pooled },
            Auth = Make.Auth() with { Flow = AuthFlow.RemoteBrowser, Steps = [] },
        };

        var ex = Rejects(manifest);

        Assert.Contains("offers_credential_store needs an auth step with fields", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SecretCustody.Server)]
    [InlineData(SecretCustody.Agent)]
    public void Rejects_a_credential_store_where_the_credential_is_not_the_users_to_hold(SecretCustody custody)
    {
        var ex = Rejects(Make.Manifest() with { OffersCredentialStore = true, SecretCustody = custody });

        Assert.Contains("offers_credential_store needs secret_custody 'client'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_server_custody_when_the_provider_really_is_unattended()
    {
        ManifestValidator.Validate(
            (Make.Manifest() with { SecretCustody = SecretCustody.Server, UnattendedFetch = true })
            .WithSession(new SessionSpec { TtlSeconds = 86_400, Refreshable = true }));
    }

    [Fact]
    public void Rejects_agent_custody_outside_a_persistent_browser_profile()
    {
        // Agent custody means the control plane holds nothing at all, which
        // only makes sense when a specific machine holds the session.
        var ex = Rejects(Make.Manifest() with { SecretCustody = SecretCustody.Agent });

        Assert.Contains("browser_persistent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("byo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_agent_custody_on_a_pooled_agent()
    {
        var ex = Rejects(Make.Manifest() with
        {
            SecretCustody = SecretCustody.Agent,
            Runtime = ProviderRuntime.BrowserPersistent,
            Agent = new AgentRequirement { Required = true, Class = AgentClass.Pooled },
        });

        Assert.Contains("byo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_the_full_byo_persistent_shape()
    {
        // The ASN edge-login case: authenticated once into a profile on the
        // user's own hardware, no login endpoint after, no secret held here.
        ManifestValidator.Validate(Make.Manifest() with
        {
            Runtime = ProviderRuntime.BrowserPersistent,
            Agent = new AgentRequirement { Required = true, Class = AgentClass.Byo },
            SecretCustody = SecretCustody.Agent,
            UnattendedFetch = true,
            Auth = Make.Auth() with
            {
                Flow = AuthFlow.DevicePersistent,
                Steps = [],
                Session = new SessionSpec { TtlSeconds = 31_536_000, Refreshable = false },
            },
        });
    }

    // ── agent routing ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProviderRuntime.BrowserOnce)]
    [InlineData(ProviderRuntime.BrowserInteractive)]
    [InlineData(ProviderRuntime.BrowserPersistent)]
    public void Rejects_a_browser_runtime_without_an_agent(ProviderRuntime runtime)
    {
        // The control plane has no browser binaries, and running one there
        // would put provider traffic on the wrong egress entirely.
        var ex = Rejects(Make.Manifest() with { Runtime = runtime });

        Assert.Contains("needs an agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_non_inline_class_that_claims_it_needs_no_agent()
    {
        var ex = Rejects(Make.Manifest() with
        {
            Agent = new AgentRequirement { Required = false, Class = AgentClass.Pooled },
        });

        Assert.Contains("inline", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_persistent_profile_outside_byo_hardware()
    {
        var ex = Rejects(Make.Manifest() with
        {
            Runtime = ProviderRuntime.BrowserPersistent,
            Agent = new AgentRequirement { Required = true, Class = AgentClass.Pooled },
            Auth = Make.Auth() with { Flow = AuthFlow.DevicePersistent, Steps = [] },
        });

        Assert.Contains("BYO-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_http_running_inline()
    {
        ManifestValidator.Validate(Make.Manifest() with { Runtime = ProviderRuntime.Http });
    }

    // ── auth ─────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_a_password_field_that_is_not_marked_secret()
    {
        // Redaction keys off exactly this flag, so an unmarked password
        // would be logged and screenshotted.
        var ex = Rejects(Make.Manifest().WithFields(
            new FieldSpec { Key = "username", Type = FieldType.Text },
            new FieldSpec { Key = "password", Type = FieldType.Password, Secret = false }));

        Assert.Contains("password", ex.Message, StringComparison.Ordinal);
        Assert.Contains("secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unattended_without_a_refreshable_session()
    {
        // Without a refresh path a human is needed every time, by definition.
        var ex = Rejects((Make.Manifest() with { UnattendedFetch = true })
            .WithSession(new SessionSpec { TtlSeconds = 86_400, Refreshable = false }));

        Assert.Contains("refreshable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_unattended_with_a_refreshable_session()
    {
        ManifestValidator.Validate((Make.Manifest() with { UnattendedFetch = true })
            .WithSession(new SessionSpec { TtlSeconds = 86_400, Refreshable = true }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_session_with_no_lifetime(int ttl)
    {
        var ex = Rejects(Make.Manifest().WithSession(new SessionSpec { TtlSeconds = ttl, Refreshable = false }));

        Assert.Contains("ttl_seconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_credential_flow_with_no_steps()
    {
        var ex = Rejects(Make.Manifest() with { Auth = Make.Auth() with { Steps = [] } });

        Assert.Contains("at least one auth step", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_device_persistent_on_a_runtime_that_has_no_persistent_profile()
    {
        var ex = Rejects(Make.Manifest() with
        {
            Auth = Make.Auth() with { Flow = AuthFlow.DevicePersistent, Steps = [] },
        });

        Assert.Contains("device_persistent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_select_field_with_no_options()
    {
        // A consumer renders this as a picker; no options is an empty
        // dropdown a user cannot get past.
        var ex = Rejects(Make.Manifest().WithFields(
            new FieldSpec { Key = "country", Type = FieldType.Select, Options = null }));

        Assert.Contains("no options", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_duplicate_field_key_across_config_and_steps()
    {
        var manifest = Make.Manifest();
        var ex = Rejects(manifest with
        {
            Auth = manifest.Auth with
            {
                Config = [new FieldSpec { Key = "username", Type = FieldType.Text }],
            },
        });

        // Inputs arrive as one flat dictionary, so a duplicate key means one
        // of the two fields silently never reaches the adapter.
        Assert.Contains("duplicate field key 'username'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_field_pattern_that_is_not_a_regex()
    {
        // The consumer compiles this client-side; an invalid one blocks the
        // whole form rather than one field.
        var ex = Rejects(Make.Manifest().WithFields(
            new FieldSpec { Key = "username", Type = FieldType.Text, Pattern = "^[a-" }));

        Assert.Contains("invalid pattern", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_a_valid_field_pattern()
    {
        ManifestValidator.Validate(Make.Manifest().WithFields(
            new FieldSpec { Key = "username", Type = FieldType.Text, Pattern = "^.{3,64}$" }));
    }

    // ── identity and resources ───────────────────────────────────────────

    [Theory]
    [InlineData("Jumbo")]
    [InlineData("jumbo_plus")]
    [InlineData("-jumbo")]
    [InlineData("jumbo-")]
    [InlineData("jumbo--plus")]
    [InlineData("")]
    public void Rejects_an_id_that_is_not_kebab_case(string id)
    {
        // The id is a route namespace, so anything else changes the URL
        // shape the consumer was told to expect.
        Rejects(Make.Manifest() with { Id = id });
    }

    [Theory]
    [InlineData("jumbo")]
    [InlineData("mock-store-simple")]
    [InlineData("ah")]
    public void Accepts_a_kebab_case_id(string id)
    {
        ManifestValidator.Validate(Make.Manifest() with { Id = id });
    }

    [Theory]
    [InlineData("nl")]
    [InlineData("NLD")]
    [InlineData("N1")]
    [InlineData("")]
    public void Rejects_a_country_that_is_not_iso_3166_alpha_2(string country)
    {
        Rejects(Make.Manifest() with { Country = country });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Rejects_a_manifest_version_below_one(int version)
    {
        // The version is sealed into every bundle as AAD; zero would make
        // "no version" and "version zero" indistinguishable.
        var ex = Rejects(Make.Manifest() with { ManifestVersion = version });

        Assert.Contains("manifest_version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_provider_that_offers_nothing()
    {
        var ex = Rejects(Make.Manifest() with { Resources = [] });

        Assert.Contains("at least one resource", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_duplicate_resource_id()
    {
        var ex = Rejects(Make.Manifest() with { Resources = [Make.Resource(), Make.Resource()] });

        Assert.Contains("duplicate resource id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_resource_id_that_is_not_kebab_case()
    {
        Rejects(Make.Manifest() with
        {
            Resources = [Make.Resource() with { Id = "Receipts" }],
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_resource_that_can_return_nothing(int maxRecords)
    {
        var ex = Rejects(Make.Manifest() with
        {
            Resources = [Make.Resource() with { MaxRecordsPerFetch = maxRecords }],
        });

        Assert.Contains("max records", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_enum_param_that_lists_no_values()
    {
        var ex = Rejects(Make.Manifest() with
        {
            Resources =
            [
                Make.Resource() with
                {
                    Params = [new ParamSpec { Key = "include", Type = ParamType.Enum, Values = null }],
                },
            ],
        });

        Assert.Contains("lists no values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_param_that_is_both_required_and_internal()
    {
        // Internal means a caller may not set it; required means they must.
        var ex = Rejects(Make.Manifest() with
        {
            Resources =
            [
                Make.Resource() with
                {
                    Params = [new ParamSpec { Key = "cursor", Type = ParamType.Text, Required = true, Internal = true }],
                },
            ],
        });

        Assert.Contains("required and internal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_null_manifest()
    {
        Assert.Throws<ArgumentNullException>(() => ManifestValidator.Validate(null!));
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        // A validator that stops at the first error turns fixing a manifest
        // into a guessing game one round trip at a time.
        var ex = Rejects(Make.Manifest() with
        {
            Id = "Jumbo",
            Country = "nl",
            ManifestVersion = 0,
            Resources = [],
        });

        Assert.Contains("Jumbo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nl", ex.Message, StringComparison.Ordinal);
        Assert.Contains("manifest_version", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least one resource", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_the_provider_it_refused()
    {
        var ex = Rejects(Make.Manifest() with { ManifestVersion = 0 });

        Assert.Contains("'mock-provider'", ex.Message, StringComparison.Ordinal);
    }
}
