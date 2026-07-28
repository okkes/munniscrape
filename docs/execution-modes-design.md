# Execution modes — where a browser runs, and whose session it uses

Status: **PROPOSAL 2026-07-28.** Nothing built yet beyond what §7 marks as
already shipped.

Companion to [connector-platform-design.md](connector-platform-design.md).
Every technical claim here was verified against the shipped
`Microsoft.Playwright` 1.61.0 assembly and driver bundle, Chromium source, and
the vendors' own image documentation. Anything that could not be verified is
marked **UNVERIFIED** rather than smoothed over.

---

## 1 · The ask, and why it needs three axes

The requirement, in the operator's words: prefer plain HTTP; otherwise
containerise the browser; and offer bring-your-own agents, split into "use my
existing Chrome" and "use an isolated one" — the hope being that a real browser
with real cookies avoids captcha triggers and can reuse an integrated login
like ASN's.

Today all of that is squeezed into one manifest field, `runtime`. It cannot
express it, because there are really **three independent questions**:

| Axis | Values | Decided by |
| --- | --- | --- |
| **Where** does the browser run? | nowhere (HTTP) · agent-local · shared browser service · user's machine | the agent |
| **Whose session** does it use? | ephemeral · dedicated profile · the human's live browser | the agent |
| **Can a human reach it?** | no · headed locally · headed behind a viewer | the agent |

`runtime` stays what it is — the provider's *minimum demand*. The three axes
above are what an **agent offers**, and the control plane already matches
offers against demands (`AgentCapabilities.CanServe`). This design adds the
missing dimensions to that match rather than inventing a second mechanism.

---

## 2 · Two findings that change the design

Both were assumptions I would otherwise have built on, and both are false.

### 2.1 "Attach to my existing Chrome" — dead the obvious way, alive a better way

The classic recipe — start Chrome with `--remote-debugging-port=9222` and
attach — **no longer works on the profile anyone cares about.** Chrome 136+
(branded builds) refuses remote debugging whenever the user data directory is
the default one:

```cpp
if (default_user_data_dir_check_enabled && is_default_user_data_dir.value_or(true))
  return base::unexpected(NotStartedReason::kDisabledByDefaultUserDataDir);
```

It fails closed, applies to `--remote-debugging-pipe` too, and
`--profile-directory` does **not** dodge it (the check is on the *user data
dir*, not the profile subfolder).

The workaround everyone reaches for — `--user-data-dir=C:\tmp\prof` — does
start, and is worthless for our purpose: it is a **brand-new empty profile**.
Chrome's own rationale is App-Bound Encryption, so copying the real `User Data`
elsewhere does not carry decryptable cookies either. **There is no way to get
the user's real sessions through a command-line flag.**

What does work is newer than this project: **Chrome 144** (stable 2026-01-13)
added an approval mode. The user opens `chrome://inspect/#remote-debugging` and
ticks *"Allow remote debugging for this browser instance"*; the server starts
**immediately, no restart**, and — critically — that path never calls
`IsRemoteDebuggingAllowed`, so **it works on their real profile**. Every
incoming connection then raises a permission dialog and an automation banner
for the session.

Playwright 1.61 already speaks it. `ConnectOverCDPAsync("chrome")` resolves the
channel's default user data dir, reads `DevToolsActivePort`, and connects — and
its failure hint is literally that chrome://inspect instruction.

So the feature is real, it is three months old, and it is opt-in per connection
by the human. That is a much better consent story than a debug port left open.

### 2.2 Containerising the browser does not help with captchas

Worth stating plainly because it is the intuitive reason to want it. What
triggers hCaptcha and Akamai is overwhelmingly **egress reputation and a blank
profile** — and a container on a NAS is the worst of both: datacenter-ish IP,
zero history, fresh every run. Containerising buys reproducibility, isolation
and easy deployment. It does not buy trust.

The captcha argument is an argument for **BYO**, not for containers. Both are
worth building; they solve different problems.

---

## 3 · The modes

| Mode | Where | Session | Human | Good for |
| --- | --- | --- | --- | --- |
| **M0 `http`** | no browser | n/a | n/a | Anything with a real API. **Always prefer this.** |
| **M1 `agent-local`** | agent's own process | ephemeral or dedicated profile | headed on a desktop, else headless | development; the operator's residential box |
| **M2 `browser-service`** | shared Playwright server container | ephemeral only (see §4.3) | via noVNC | containerised operator fleet |
| **M3 `byo-profile`** | user's machine | dedicated profile that persists | headed if they want | privacy-maximal; profile earns trust over months |
| **M4 `byo-attach`** | user's machine | **their live browser** | inherently | integrated logins (ASN), captcha avoidance |

M3 and M4 are the two halves the operator described as "isolated" versus
"existing Chrome". M1 and M2 are the operator-hosted halves.

**M0 is not a fallback, it is the goal.** Albert Heijn spends one browser on
login and then never needs one again; Lidl the same. A provider that can be
demoted to M0 for its steady state should be, and the manifest's `runtime` tier
is exactly where that gets recorded.

---

## 4 · Each mode, concretely

### 4.1 M0 — HTTP only

Already shipped: `AgentClass.Inline`, run in-process by `InlineJobRunner`.
Nothing to add. The discipline is in the adapters — every provider whose
steady state is HTTP must not hold a browser open for it.

### 4.2 M1 — the agent launches its own browser

Already shipped: `LaunchAsync` / `LaunchPersistentContextAsync` inside
`BrowserLease`, with `Headless` and `ProfileRootDirectory` from config. This is
what runs today, headed, on the developer's machine.

### 4.3 M2 — a shared browser service

Playwright's own server, not Selenium. Both work from .NET, but for a
Playwright codebase the native server is the closer fit; Grid's Playwright
interop is officially *experimental* and adds a WebDriver hop for nothing.

Two server flavours, and the difference matters:

| | `launch-server` | `run-server` |
| --- | --- | --- |
| Browser processes | **one, shared** | one **per client** |
| Context isolation | real (`isolateContexts`) | real |
| Failure isolation | **none** — one crash kills every job | per job |
| Launch latency | none | a browser start per connect |
| Concurrency cap | unbounded | `--max-clients` |

**Recommendation: `run-server`.** A shared Chromium process that OOMs takes
every concurrent job with it, and Chromium OOMs readily on Docker's default
64 MB `/dev/shm`. Paying a browser launch per job is the cheaper mistake.

Sharp edges, all verified:

- **No `LaunchServerAsync` exists in the .NET binding.** The server is started
  by a **hidden CLI command** (`playwright.ps1 run-server`) with no stability
  contract. The NuGet package ships that script, so no Node install is needed.
- **No authentication and no TLS.** The Host/Origin check is disabled whenever
  the server binds a non-loopback address — which it must, to be reachable
  from another container. Anyone who can route to the port and knows the path
  gets full browser control: cookies, `file://`, arbitrary JS. **Network
  isolation is the entire security model**, so it binds to the compose network
  only and is never published.
- **Version lockstep is enforced.** Client and server must match on
  **major.minor**; a mismatch is rejected with `HTTP 428`. The image tag and
  the `PackageVersion` in `Directory.Packages.props` must move as one commit.
- **`ExposeNetwork` silently does nothing** with `launch-server`
  (upstream microsoft/playwright#31718).
- **Video needs `SaveAsAsync`** — `IVideo.PathAsync()` throws when remote.

And the constraint that decides the architecture:

> **A persistent profile cannot cross a remote connection.**
> `LaunchPersistentContextAsync` is a `BrowserType` method; `ConnectAsync`
> returns an `IBrowser`, which has no equivalent, and every `run-server`
> connection mode sets `denyLaunch: true`.

So **M2 can never serve a `browser_persistent` (T4) provider.** T4 requires the
agent to launch the browser itself — M1 or M3/M4. That is a hard boundary, not
a preference, and the capability matcher must enforce it rather than letting a
job route somewhere it will fail.

### 4.4 M3 — BYO with a dedicated profile

`LaunchPersistentContextAsync` against a directory the agent owns on the user's
hardware. Survives restarts, accumulates cookies and history, and over months
starts looking like exactly what it is: one person's browser. Never touches
their daily browser, so no consent problem beyond running the agent at all.

This is the **default recommendation for BYO**, and the honest answer to "avoid
captcha triggers" for most providers: residential egress plus an aged profile
is most of the benefit of M4 with almost none of the risk.

### 4.5 M4 — BYO attached to the user's live Chrome

The powerful one, and the dangerous one.

```csharp
var browser = await pw.Chromium.ConnectOverCDPAsync("chrome",
    new BrowserTypeConnectOverCDPOptions { NoDefaults = true });

var context = browser.Contexts[0];   // the REAL profile - real cookies, real tabs
```

Two details that are the whole feature:

- **`Contexts[0]` or nothing.** `NewContextAsync()` creates a fresh
  incognito-like context with **zero cookies** and silently defeats the entire
  purpose. This deserves a comment in the code and a test.
- **`NoDefaults = true`** exists precisely for this case: it suppresses
  Playwright's default overrides (download behaviour, focus and media
  emulation) so attaching to someone's daily driver does not disturb it.

`CloseAsync()` on a CDP-attached browser disconnects; it does **not** quit
their Chrome.

**What it costs, stated plainly.** A browser-level CDP socket is not scoped to
a provider. It grants `Target.getTargets`, `Page.navigate` to any origin,
`Runtime.evaluate` in any page, and browser-wide `Storage.getCookies` — every
site that human is signed into: bank, email, cloud console, password vault.
**CDP has no authentication, no authorisation, and no per-origin scoping, so
per-site restriction is not implementable.** The only real gates are Chrome
144's per-connection approval dialog, the loopback bind, and enterprise policy.

Therefore M4 ships with:

- an explicit, unmissable consent step naming what it can reach — not a
  checkbox reading "use my browser";
- **opt-in per provider**, never a global default;
- no artifact capture at all while attached — no screenshots, no DOM dumps,
  no traces — because they would capture other tabs;
- a hard preference for M3 in the UI, with M4 offered only where a provider
  genuinely needs it (ASN's integrated login is the real case).

Also plan for it to be unavailable: enterprise policy `RemoteDebuggingAllowed`
disables the whole path, and it does not exist on Android.

---

## 5 · How it threads through the platform

The pieces already exist; they need one more dimension.

```jsonc
// AgentCapabilities - what an agent OFFERS
{
  "providers": ["asn"],
  "runtimes": ["browser_persistent"],
  "egress": { "country": "NL", "kind": "residential" },
  "execution": {
    "mode": "byo_attach",          // http | agent_local | browser_service | byo_profile | byo_attach
    "attended": true,              // already implemented, from Headless
    "viewer": "novnc",             // none | novnc  - can a human be sent somewhere to look
    "persistent_profiles": true    // false for browser_service: the wire forbids it
  },
  "max_concurrency": 1
}
```

```jsonc
// ProviderManifest.agent - what a provider DEMANDS
{
  "required": true,
  "class": "byo",
  "egress": { "country": "NL", "kind": "residential" },
  "execution": {
    "min_session": "profile",      // ephemeral | profile | attached
    "requires_attended": false
  }
}
```

`CanServe` grows to check the execution dimensions, and the leased-job query
filters on them. The rule that must be encoded rather than documented:

- `browser_persistent` ⇒ `persistent_profiles: true` ⇒ **never**
  `browser_service` (§4.3);
- `min_session: attached` ⇒ `byo_attach` only;
- a provider whose login raises an interactive captcha and finds
  `attended: false` fails fast — already shipped as of today's captcha work.

Files this touches, end to end: `AgentContracts.cs` (capabilities),
`ProviderManifest.cs` (demand), `ManifestValidator.cs` (the impossible
combinations), `EfLeasedJobQueue.TryLeaseAsync` (the match),
`BrowserLease.cs` (the four launch paths), `ConnectorAgentOptions.cs`
(configuration), plus the agent Dockerfiles and compose.

---

## 6 · Running it two ways

The operator wants one system that runs headed on a desktop today and fully
containerised tomorrow, still watchable.

### 6.1 Three ways to watch, and only two let you type

| | Interact? | Notes |
| --- | --- | --- |
| Headed on the host (`dotnet run`, `Headless=false`) | **yes** | what runs today; no Docker involved |
| Container + Xvfb + x11vnc + **noVNC** | **yes** | `selenium/standalone-chrome` ships this: noVNC on **7900**, raw VNC on 5900, default password `secret`, `--shm-size=2g` recommended. Interactive, not view-only. **amd64 only.** For a Playwright image the stack must be assembled by hand. |
| Trace viewer / video / `PWDEBUG` | **no** | after the fact; excellent for diagnosis, useless for solving a captcha |

Since solving a captcha is the point, only the first two qualify. Viewer ports
sit in the 84xx block — **8440** bank, **8441** shop — and raw VNC (5900) is
never published.

One trap worth encoding: do **not** use `restart: always` on a headed viewer
container. A crash loop will keep re-opening a window a human is typing into.

### 6.2 One config, two ways

Compose **profiles** for which services exist, plus an env switch for headed
versus headless. Two verified traps:

- `compose.override.yml` is auto-merged **only** when Compose picks the default
  file itself. The moment the README says `-f deploy/docker-compose.local.yml`
  — which it does today — the override is silently not loaded. Either stop
  naming files explicitly or always spell out both `-f` flags.
- Never put `${VAR:?}` in a profiled service; the interpolation still fires
  when the profile is disabled.

### 6.3 The BYO packaging question

For M3/M4 on someone else's machine, Docker is probably the wrong shape:

- Docker Desktop on Windows/macOS carries a **paid licence** for organisations
  ≥250 employees or ≥$10M revenue, plus a ~4.9 GB pull.
- **A container cannot reach a Chrome on the Windows host.** The CDP port binds
  `127.0.0.1`; `host.docker.internal` resolves to a *non-loopback* host address,
  and Docker Desktop's host networking does not close that gap. **M4 therefore
  cannot run from a container on Windows at all** — the agent must run natively.

Since the agent already publishes `--self-contained`, a signed native installer
(agent plus `playwright install chromium`) is smaller, has no licence question,
and is the only thing that can offer M4. Compose stays the operator-fleet story
(M1/M2); BYO gets an installer.

If the agent *is* containerised for M3, note that today's compose mounts only
`/profiles` — `agent-state.json` needs a volume too, or the agent re-enrols on
every restart.

---

## 7 · What exists today

Shipped and working: **M0** (inline HTTP), **M1** (agent-local, headed or
headless, ephemeral or persistent profile), the `Attended` flag and the
interactive-captcha handling built on it, outbound-only agent enrollment, and
capability matching on providers, runtimes and job kinds.

Not built: **M2**, **M3** as a packaged product, **M4** entirely, the execution
dimensions in the capability match, the viewer containers, and the compose
profile split.

---

## 8 · Slices

| | Slice | Delivers |
| --- | --- | --- |
| **X1** | Execution dimensions in `AgentCapabilities` + manifest demand + validator + lease matching. No new modes yet. | the vocabulary, and the T4-cannot-use-browser-service rule enforced in code |
| **X2** | Compose profile split; headed-local vs headless-container from one config; the `-f` override trap fixed in the docs | "runs two ways" for what already exists |
| **X3** | **M2** browser service: `run-server` container, version-locked to the NuGet pin, network-isolated, plus a noVNC viewer container | the containerised fleet |
| **X4** | **M3** BYO installer: native, self-contained, persistent profile, state volume, enrollment flow a non-expert can complete | the privacy-maximal option, and most of the captcha benefit |
| **X5** | **M4** BYO attach: `ConnectOverCDPAsync("chrome")`, `Contexts[0]`, `NoDefaults`, consent UX, artifact capture disabled, per-provider opt-in | integrated logins; ASN |
| **X6** | Demote providers to M0 wherever their steady state is HTTP | fewer browsers, less exposure |

X1 and X2 are worth doing regardless — they cost little and stop the current
single `runtime` field from accumulating more meaning it cannot carry.

---

## 9 · Open decisions

1. **Is M4 offered to end users at all**, or kept as an operator/advanced
   feature? It hands a connector agent every logged-in session in the browser,
   and no per-site restriction is possible. My recommendation: build it, gate
   it per provider, default it off, and let ASN be the case that justifies it.
2. **Chrome 144 is a hard floor for M4.** Are users expected to be on it? On
   anything older there is no path to a real profile at all.
3. **arm64.** `selenium/standalone-chrome` is amd64-only, and Playwright's
   arm64 story is still the open question from the platform design. If the
   fleet is meant to run on a Pi, M2 needs verifying before X3 is scheduled.
4. **`run-server` is an undocumented CLI surface.** Acceptable for an
   operator-run fleet, but it should be pinned and smoke-tested in CI so an
   upgrade cannot break it silently.
5. **Does the operator's residential box stay M1**, or become an M2 client?
   M1 keeps persistent profiles available; M2 does not.
