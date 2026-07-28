# Connecting a store — the plan for what a person actually does

Written 2026-07-28, after the first live captcha relay.

## The decision

**Stop driving a browser for any provider whose login is a real
authorization-code redirect. Keep the tap relay, unchanged and unextended,
for the providers that have no redirect to hand over. Build a streamed
browser view only if the relay is still failing those providers after the
redirect work has landed — and only as a clipped rectangle, never a
viewport.**

The relay is not the problem. A human's normalised taps were replayed by
`page.Mouse` into a cross-origin hCaptcha frame and hCaptcha accepted them
and advanced. That is the hard part and it works. The problem is one step
upstream: **we chose to type a password into an automated browser, on a page
whose entire purpose is to refuse automated browsers.** hCaptcha fired
because the browser was fresh, headless and on our egress — not because of
anything about the account. Everything downstream of that choice is
consequence: the screenshot, the redaction, the crop, the round budget, the
600-second clock, and four password submissions against a defended page under
a platform rule that says a submitted credential is never retried.

Albert Heijn's login is an OAuth authorization-code flow to a custom scheme.
Lidl's is too, and Lidl already made this move — `LidlPlusOptions.cs:90`,
`ClientSideAuthorization` defaults to **true**, with the reason written into
the manifest: on 2026-07-28 a live attempt with correct credentials and
correct selectors was bounced to the identifier screen by reCAPTCHA
Enterprise, which scores the browser and never draws a widget at all. AH's
half of the same mechanism is already written — `AskTheHumanAsync`
(`AlbertHeijnAdapter.cs:318-337`) builds the authorize URL and raises
`ChallengeType.Redirect`, and `ObtainCodeAsync:189-194` returns a handed-over
code without starting a browser, covered offline by
`A_redirect_handed_over_up_front_needs_no_browser_and_no_human`. It is
demoted to a last resort. **The change is when it runs, not what it does.**

This is not a new idea in this repository. It was the original AH design and
it was withdrawn — `shopping-connector-service.md:246-286`, "The decision, and
what overrode it". Read the reason it was withdrawn, because it decides the
shape of this plan: *"On a phone, `appie://login-exit?code=…` either opens the
real Appie app — which consumes the code — or fails with nothing to select,
so there is frequently nothing to paste."* That was a **client capability**
failure, not a mechanism failure. The mechanism was never tried and found
wanting; there was nowhere to put the callback. A native shell with a
navigation-cancel hook is exactly the thing that closes that gap, and it is
the one piece of this plan that does not exist in any repo yet.

So the plan is staged around that fact, not around the connector work. The
connector work is roughly two days. The gate is a native shell and one
experiment nobody has run.

---

## Which providers this rule moves, and which it does not

Read from each provider's own options and manifest.

| Provider | Login | Route |
| --- | --- | --- |
| **Lidl Plus** | authorization code → `com.lidlplus.app://callback`, PKCE S256 | **Already moved.** `Runtime.Http`, `Agent.Inline`, `Flow.OauthRedirect` |
| **Albert Heijn** | authorization code → `appie://login-exit`, **no PKCE, no state, no scope** | **Moves.** This plan |
| **Picnic** | `POST /user/login` JSON + SMS | N/A — already `Runtime.Http`, no browser |
| **Coolblue** | real OIDC, but the callback is `https://www.coolblue.nl/inloggen/oidc` | **Does not move.** An https callback on a domain we do not control cannot be claimed without Coolblue serving `assetlinks.json` / an associated domain. Browser stays |
| **Jumbo** | Auth0 web session at `auth.jumbo.com`, credential is a cookie | **Does not move.** No custom scheme, nothing to intercept. `JumboOptions.cs:44-59` records native client values as research for a future day; that day is not this plan |
| **bol.com** | Spring Security form post, `j_username`/`j_password`, outcome is a cookie jar | **Does not move.** No authorize endpoint exists |
| **Amazon** | form sign-in, storage-state session | **Does not move.** Login with Amazon yields a profile token, not order history |
| **Magento / Woo guest** | order number + e-mail, `Challenges = []` | N/A — no login at all |

Two of eight move. Anyone reading this as "groceries solved" has not read
`JumboOptions.cs`. What it does mean is that the two providers whose login was
never suited to a browser stop using one, and the four that genuinely need a
browser keep the relay that was built for them.

---

## Stage 1 — Guard a redirect answer like the password it replaces

**This stage is independently useful and it is a live bug today, for Lidl,
in production shape.** It has nothing to do with Albert Heijn and should not
wait for it.

The argument for the whole plan is custody: an AH password today is written
to `Job.InputsJson` in the connector's own database (`EfLeasedJobQueue.cs:63`,
cleared at `:391`/`:434`), handed to an agent over the long-poll (`:456`),
typed into Chromium, and is the entire reason `ScreenshotRedactor` exists.
Under a redirect flow all of that disappears and what flows instead is a
single-use authorization code. **That trade is only a win if the code is
treated as least as carefully as the password was, and today it is not.**

Two verified gaps:

1. `ChallengeService.AnswerAsync` writes `row.AnswerValue = value` and nulls
   `row.ImageBytes` on the next line — the picture is purged, the answer is
   not. `SweepAsync` purges `ImageBytes` only, and deletes rows only once
   `ExpiresAt < now - 1 day`. So a callback URL carrying a live authorization
   code **rests in the connector database for the challenge's lifetime plus a
   day.** The password it replaces is cleared at terminal state.
2. `AgentJobContext.SecretValues` is built once in the constructor, from
   manifest secret fields found in `Job.Inputs`. A value that arrives as a
   *challenge answer* is never in it, so `SecretScrubber` cannot mask a
   pasted authorization code out of a failure detail or an exception dump —
   whereas today's AH password is scrubbed.

### What changes

- `connector-kit/src/Connector.Kit.Hosting/Challenges/ChallengeService.cs` —
  add `AnswerValue` to the sweep's purge pass beside `ImageBytes`, and null it
  when the owning job reaches a terminal state, mirroring what
  `EfLeasedJobQueue` already does for `InputsJson`.
- `connector-kit/src/Connector.Kit.Agent/Execution/AgentJobContext.cs` —
  `SecretValues` becomes a set that `AskAsync` adds to when the answer it
  returns belongs to a `ChallengeType.Redirect`. One line at the return, one
  type change on the property.
- `connector-kit/src/Connector.Kit.Hosting/Data/Entities.cs:149-155` — the
  comment claiming `Job.MaterialJson` is "the only place the control plane
  ever holds credential material at rest" is false while the challenge table
  holds SMS codes and callback URLs. Correct it rather than leave a comment
  that teaches the wrong thing.

### The design question inside this stage

`AwaitAnswerAsync` re-reads the row on every pass, so purging on *delivery*
would break redelivery after an agent restart. Purging on terminal state is
strictly safe and is what mirrors `InputsJson`; purging on delivery is
stronger and needs the answer to "does an agent ever need this twice?" to be
no. Take terminal-state now; revisit if the window matters.

### How we know it worked

A test that answers a redirect challenge, drives the job to a terminal state,
and asserts `AnswerValue` is null. A test that fails a job after a redirect
answer and asserts the code does not appear in the failure detail. Both run
offline.

---

## Stage 2 — Open AH's redirect path, behind one switch, default off

**Independently useful:** it turns AH's current worst ending into a working
one, and it makes the experiment in Stage 3 runnable through the real API
instead of a test harness. It changes nothing for anyone connected today.

The live run's last line was `type=AppApproval` — the gate's "pass the widget
in the window in front of you", raised because the relay's escalation budget
ran out. On a headed dev agent that is merely useless; on the pooled agent AH
actually declares, `CaptchaGate` throws `Unrelayable` instead, which tells the
user to connect from a machine they are sitting at. Both are dead ends.
`ChallengeType.Redirect` sits after both in the same code path and is not
reachable through the API at all, because `redirect_url` is undeclared and
`AuthInputValidator` refuses it as `unknown input 'redirect_url'` — which the
adapter's own comment at `:183-188` says out loud.

### What changes

`shop-connector/src/ShopConnector.Adapters/AlbertHeijn/AlbertHeijnOptions.cs`

- Add `ClientSideAuthorization`, defaulting to **false**, mirroring
  `LidlPlusOptions.cs:90`.

`shop-connector/src/ShopConnector.Adapters/AlbertHeijn/AlbertHeijnAdapter.cs`

- `LoginAsync` branches on the switch before anything is leased, into a
  near-copy of `LidlPlusAdapter.ClientAuthorizeAsync:171-225` minus PKCE
  (there is no verifier to keep alive — see the open questions).
- `ObtainCodeAsync`, `SettleAsync`, `DismissConsentAsync`, the `CaptchaGate`
  construction and every selector list stay exactly as they are. Nothing is
  deleted in this stage.

`shop-connector/src/ShopConnector.Adapters/AlbertHeijn/AlbertHeijnManifest.cs`
— **and this is the part no proposal got right.**

Build the manifest **from the options**: `AlbertHeijnManifest.Build(options)`,
held as an instance field rather than the `static readonly` it is today. The
switch then decides both halves at once:

| `ClientSideAuthorization` | `Auth.Flow` | `Auth.Steps` | `Auth.Challenges` |
| --- | --- | --- | --- |
| `false` (today) | `Password` | `credentials`: username + password | `[Image, AppApproval, Redirect]` |
| `true` | `OauthRedirect` | `authorize`: one optional secret `redirect_url` | `[Redirect]` |

**The failure this prevents is already live for Lidl.** `LidlPlusManifest`
statically declares `Flow = OauthRedirect` and a single `redirect_url` field,
while `LidlPlusAdapter` still carries a full browser login behind
`ClientSideAuthorization = false`. Flip that switch today and the browser path
demands `username`, which the manifest does not declare, so
`AuthInputValidator` rejects it before the adapter is ever called. **Lidl's
fallback is unreachable through the control plane — it is dead code wearing a
switch.** If AH copies that shape, AH's "recovery is a config flip" is a
fiction too. Deriving the manifest from the options is what makes the claim
true, and it costs about ten lines.

`ManifestVersion` **stays at 2.** It is sealed into every bundle as AAD, and
what a bundle means is `SessionMaterial` — an access token and a refresh
token — which is identical either way. The file already reasons exactly this
way at `:91-94` for a smaller change. Bumping it would silently log out every
connected AH user in order to announce a change to a form.

`Runtime` **stays `BrowserOnce` and `Agent` stays `{ Required = true, Class =
Pooled, Egress = NL/residential }`.** See the open questions — this is the
one place where getting it wrong fails *after* the user thinks they have
succeeded. `BrowserLease.Started` is `Volatile.Read(ref _page) is not null`
and `PageAsync` launches on first call, so a redirect login leases an agent
and starts no Chromium. The pooled agent costs a lease and nothing else.

### What the human experiences

Nothing, unless an operator flips the switch. If one does, on desktop web:
one button, "Sign in at Albert Heijn", AH's own page in a new tab, and the
existing `redirectChallenge` renderer's paste box
(`demo-client/.../challenges.js:534-553`). Ugly, and on desktop it works —
which is more than the `AppApproval` ending it replaces.

### How we know it worked

`A_redirect_handed_over_up_front_needs_no_browser_and_no_human` already
asserts the behaviour; it becomes reachable rather than theoretical. Add:
the manifest under each switch position validates and matches the code path's
required inputs, and a login posted with `inputs: {}` under
`ClientSideAuthorization = true` raises a `redirect` challenge and starts no
browser. All offline.

---

## Stage 3 — The native shell, and the flip

This is the stage that pays the user, and it is the only one whose work is
mostly outside this repository.

### The gate, first: one experiment, costing one login

Nothing here has ever been driven. **Before the switch is flipped anywhere,
drive `login.ah.nl` once inside a WKWebView / WebView2 and confirm two
things:** that `client_id=appie-ios` is still accepted from a webview user
agent, and that the sign-in still ends in a navigation to
`appie://login-exit?code=…`. Under the never-retry rule this costs a real
attempt against a real account, so it is run once, deliberately, and the
result is written down here.

If it fails, this stage stops and Stage 2's switch stays off. That is the
whole point of Stage 2 shipping first: the experiment is cheap and the
rollback is free.

### The mechanism: a navigation hook, not an OS auth session

Use an in-app WebView and cancel the navigation:
`decidePolicyFor navigationAction` (iOS), `NavigationStarting` (WebView2),
`shouldOverrideUrlLoading` (Android). This is the same event `RedirectWatcher`
already listens for in Playwright, on the same navigation.

Do **not** reach for `ASWebAuthenticationSession(callbackURLScheme: "appie")`
as the primary route. `appie://` is Albert Heijn's own app's scheme, not ours.
Whether an OS auth session intercepts a foreign scheme ahead of an installed
app that claims it is a platform question nobody here has answered, and the
repo has already recorded the failure it produces: the Appie app consumes the
code, the code is single-use, and the user is left with a login that stopped
with no explanation. A navigation hook fires before any OS hand-off and does
not care who owns the scheme.

The cost of that choice, stated because it is real: the user loses the system
sheet that names `login.ah.nl` with a padlock, and has to take munni's word
that the box it drew is not reading what they type. Put that in the UI copy
rather than letting the copy claim the stronger version.

### What changes

- **munni's native shell** (different repo): one branch on
  `auth.flow === "oauth_redirect"` to draw no form, one WebView screen, one
  navigation-cancel hook deriving the pattern from `challenge.return_pattern`,
  and posting the whole callback URL as the challenge answer.
  `RedirectCode.Extract` already accepts a full URL, a URL with stray
  whitespace, or a bare code.
- **This repo:** flip `ClientSideAuthorization` to `true` for AH in the
  deployed configuration. That is the entire connector-side change, because
  Stage 2 built the manifest from the option.

### What the human experiences

Tap Albert Heijn. Tap Connect. No form — there are no fields. AH's own login
page appears, their password manager offers the password, their passkey
works, and if a captcha draws they solve it with a finger, in a real browser,
with hover and animation and instant feedback, in two seconds. The sheet
closes by itself. Receipts sync.

Reconnecting after the 90-day session dies is one tap, not a retyped
password.

And the property that matters most under this platform's own rules: **account
lockout becomes structurally impossible for AH.** We never submit a
credential, so we cannot submit a wrong one. Today's live run spent four
password submissions against a defended page; under this design those would
have been four sign-ins that AH's own page rate-limited and explained, in
Dutch, on the user's own screen.

### How we know it worked

The metric to watch is **not** the login success rate. Under this design a
provider-side change — a rotated client id, a moved authorize path — produces
a user who signs in successfully on AH's own page and is then told
`session_expired`. That exact failure has already happened once in this
project (`shopping-connector-service.md:203-213`: *"The obvious reading is
'my password is wrong', which sends a user to reset a password that was
fine"*). Deleting nine selector lists removes the loud failures and leaves
the quiet one.

So: **alert on the token-exchange failure rate**, separately from login
failures, from day one. It will not be caught by a stack trace.

---

## Stage 4 — The web PWA

Say this plainly rather than in a footnote: **a browser cannot claim
`appie://`, and no amount of work changes that.** There is no popup bridge,
because no page of ours ever loads on a custom scheme, so there is nothing to
`postMessage` from.

What web users get, in order of preference:

1. **Desktop: the paste box stays.** `redirectChallenge` already renders it
   and it already works when there is an address bar to copy from. It is not
   good and it is better than the `AppApproval` dead end it replaces.
2. **Mobile web: hand off to the phone's own app.** The PWA shows a deep link
   into the native shell, the shell runs the WebView sign-in, and the PWA
   polls `GET /v1/{provider}/login/{session_id}` on the same server-side
   session. Nothing new on the wire; the session already exists and is
   already pollable. This is the only design that rescues mobile web, and it
   only exists once Stage 3 does.
3. **If (2) is not built: mark AH `WebSupport.None` for mobile web
   specifically, not for web as a whole.** `SessionService.CreateAsync`
   refuses a `DeviceClass.Web` session cleanly on `WebSupport.None`, but the
   vocabulary has no per-form-factor value, so this is a consumer-side rule
   in munni rather than a manifest change. Offering mobile web a flow that
   dead-ends at the last step is worse than saying "connect this one from the
   app".

Note what the PWA gives up, and what it gains. It gives up the ability to
connect AH from mobile web without the app. It gains: today the PWA takes a
real AH password into a tab-lifetime bundle and the user retypes it every
visit (`WebSupport.Ephemeral`). Under a redirect flow there is no password to
retype and none to take. Even the degraded paste route is a custody
improvement.

---

## Stage 5 — The streamed view, and it is conditional

**Do not build this yet, and do not build it for Albert Heijn at all.**

Build it only if, after Stage 3 has landed, the tap relay is still the front
door for bol, Amazon, Jumbo or Coolblue *and* is still failing them. Those
four have no redirect to hand over and will keep meeting widgets in our
Chromium.

Two things are settled in advance so the spike does not have to rediscover
them:

**It is a clipped shutter, not a screencast.** I checked the pinned
Playwright 1.61.0 XML: `ScreencastStartOptions` carries only `OnFrame`,
`Path`, `Quality` and `Size` — and `Size` is *max* width/height with the
page's aspect ratio preserved. **There is no clip anywhere in the screencast
API.** `PageScreenshotOptions.Clip` does exist. A screencast therefore emits
the whole viewport and deletes the third layer of `ScreenshotRedactor` — *"when
the adapter declares a crop region, only that region is rendered at all;
everything outside it is absent rather than merely obscured"* — and turns the
feature into a live remote view of an authenticated grocery session: the
user's address, their order history, their account. A repeated clipped
`ScreenshotAsync` at JPEG quality with `Animations = Allow` keeps the crop
structural. Cropping frames back down on the agent means decoding JPEG in
.NET, which means a new image dependency to undo a capability we did not
want.

**Pointer only. No keyboard, ever, in a first version.** Hover, drag, scroll
and typing are all reachable through `IMouse` / `IKeyboard` today with no CDP
at all — the same pipeline `MouseTapSurface` already uses — so the restriction
costs nothing mechanically. It costs the authorization model to lift it.
`TapAnswer.TryParse` is the entire authorization model of the current relay:
the only sentence a client can utter is `tap.v1:` followed by fractions in
`[0,1]` and an optional terminal `submit`, mapped into a rectangle
re-measured from the provider's own selectors immediately before dispatch. A
compromised munni, a compromised connector, or XSS in a client cannot say
"navigate", "type" or "read cookies" because **the grammar has no words for
them.** Pointer events can be bounded by the rect the way `TapReplay`'s
`Fits`/`Inside` already bound taps. Keys cannot, and a key channel into a
browser that is at that moment authenticated as the user is a different
system.

Budget honestly if it is ever funded: a 3-5 day spike that streams a clipped
hCaptcha widget and clicks it back against the mock provider, and 3-6 weeks
to production — per-frame redaction latch, viewer liveness, adaptive
bandwidth, teardown on every path, and a sticky-routing-by-`jobId` deployment
constraint the platform does not have today, because a frame slot is
instance-local memory with no database to re-read.

And the caveat that decides whether it is worth anything: **a live view
cannot beat a score.** Lidl was refused before a widget was ever drawn. A
stream of a page that bounces to the identifier screen is a stream of a page
that bounces to the identifier screen. It is worth building only where a
widget actually appears.

---

## What happens to the tap relay

**Keep it. Do not extend it. Stop making it Albert Heijn's front door.**

Keeping it needs an argument and here it is: bol, Amazon, Jumbo and Coolblue
have no redirect to intercept, they will keep meeting widgets in our browser,
and the relay is the only thing that answers one. It is also the fallback for
AH if Stage 3's experiment fails or AH rotates its client. It is already paid
for and it is proven live.

What changes about it is its status, not its code:

- **No more tuning.** The remaining defect — a widget photographed mid-draw,
  the 12,618-byte spinner — is best-effort by construction: `CaptchaGate`
  waits up to three seconds for the frame to settle *"and when it runs out the
  picture is taken anyway"*. It cannot be eliminated while the mechanism is
  "photograph a thing that is animating". Do not spend another day on it.
- **Nothing is deleted.** `CaptchaGate`, `PageOps`, `RedirectWatcher`,
  `ScreenshotRedactor`, `MouseTapSurface` and today's four fixes all stay.
  Four providers still drive browsers.
- **AH's browser half stays too**, behind the switch, with its selectors —
  and, because of Stage 2's options-built manifest, actually reachable. The
  moment it is deleted, recovery from an AH-side change becomes a release
  against a provider that has already broken.

For the record, on the four fixes committed today: three land cleanly, and
`5da4888` was not a relay defect at all — it was a disconnected SSE reader
taking the request down with it. It presented as the whole app hanging
mid-captcha, which is a fair part of why the relay felt unstable. That is
worth knowing before anyone concludes the relay's mechanism is shaky. It is
not; it is pointed at the wrong wall.

---

## What the owner has to decide

These are not engineering calls and they should not be made by default.

**1. Do we spend one live AH login on the experiment?**
Everything in Stage 3 turns on whether `login.ah.nl` completes a sign-in
inside a webview and still redirects to `appie://login-exit?code=`. Under the
never-retry rule this costs a real attempt on a real account. If the answer
is no, Stage 2 ships anyway and the switch stays off.

**2. Are we comfortable using `appie-ios` in front of the user?**
The adapter already sends AH's own app identity as `x-client-name` and its
user agent, and defends that as honest client identification. A native flow
makes it visible: the user is signing into a page issued to Albert Heijn's
own app, and any consent copy will say "Appie". Also note the grant is
**unscoped** — the authorize URL carries no `scope`, so we receive whatever
the Appie app receives. That is already true today; a native flow puts it in
front of a person. Product and legal question, not a technical one.

**3. App store review.** An app that opens another company's login page and
catches its callback is a plausible flag under Apple 5.2.2 and Google's
impersonation policy. Unknowable from here. It is not a reason not to build
it; it is a reason to have the answer to (2) written down before submission.

**4. Do we accept losing our diagnostics?**
Today a failed AH login leaves a screenshot, a last URL and a settle trace.
Tomorrow it leaves "the user closed the sheet." `ChallengeType.Redirect` is
opaque by construction — one string comes back. That is the flip side of the
custody argument and it is not free. The mitigation is the exchange-failure
alert in Stage 3, and it is a mitigation, not a replacement.

**5. Whether to probe AH for PKCE support.**
`AuthorizeUrlTemplate` sends no `code_challenge` and `ExchangeAsync` posts
`{clientId, code}` with no verifier, so an AH authorization code is
**bearer-grade**: whoever catches it can spend it until it is burned. Lidl
does not have this problem. We cannot add PKCE unilaterally — AH's server has
to accept it. Probing whether `login.ah.nl` accepts `code_challenge` and
whether `/mobile-auth/v1/auth/token` accepts `code_verifier` costs another
live attempt. Worth noting the risk direction: if AH *adds* PKCE later, the
flow fails loudly at the exchange and the fix is additive —
`PkceChallenge.Create()` already exists in the tree.

**6. `Runtime.Http` + `AgentClass.Inline` for AH — later, and on evidence.**
Lidl has already taken this bet and it is unproven there too. AH demands NL
residential egress today because `login.ah.nl` is defended; moving login to
the device solves that for *login*, but `api.ah.nl/graphql` would then be
called from the control plane on a datacenter IP, and nothing establishes
that it tolerates one. Getting this wrong is the worst-shaped failure in the
plan: the user connects, sees green, and then every sync fails for a reason
nothing tells them. Keep the pooled NL agent until a control-plane fetch is
observed to work. It costs a lease.

---

## What nobody verified

Marked, because a plan built on these is a plan built on wishes.

**Verified in this repository or the pinned assembly** — the mechanism half of
this plan rests on these and they are solid: `ClientAuthorizeAsync` is a
complete shipped client-side authorization login and is Lidl's default; AH's
`AskTheHumanAsync` already raises `Redirect` with `Url` and `ReturnPattern`;
`ObtainCodeAsync:189-194` already returns a handed-over code with no browser
and latches `CredentialSubmitted()`, covered by an existing offline test;
`AuthInputValidator` throws `unknown input '<key>'`; `ManifestValidator`
permits `Http` + `Required=true` + `Pooled`; `BrowserLease.Started` is lazy;
AH's authorize template carries no state, scope or PKCE and `ExchangeAsync`
posts only `{clientId, code}`; `ChallengeRow.AnswerValue` is written and never
purged while `ImageBytes` is; `SecretValues` is built only from
`Job.Inputs`; `ScreencastStartOptions` has no clip and
`PageScreenshotOptions.Clip` exists; `Challenge.Url` / `ReturnPattern` and
`AuthFlow.OauthRedirect` are already in the frozen kit, so **nothing in
stages 1-4 touches `Connector.Kit`'s contract.**

**Not verified, and load-bearing:**

- **That `login.ah.nl` completes a sign-in in a mobile webview and redirects
  to `appie://login-exit?code=`.** This is the whole plan's foundation and it
  has never been driven. Stage 3's gate exists for it.
- **That AH's OAuth client is what we think it is.** The repo carries two
  disagreeing readings: `docs/shopping-connector-service.md:185-189` has
  `client_id=appie-ios` at `login.ah.nl/login?...`, CONFIRMED 2026-07-27 from
  `gwillem/appie-go`; `docs/research/retailers-groceries.md:102` has
  `client_id=appie` at `login.ah.nl/secure/oauth/authorize?...` from
  `salujayatharth/ah-api`. The first supersedes the second — `appie` is
  precisely what broke the first live login — but note what that means: **the
  foundation of this plan is a third-party reading of somebody else's mobile
  app, and it has already rotated once inside this project's own history.**
- **That the Albert Heijn app registers `appie://`.** Near-certain, since it
  is that app's own callback, and unconfirmable offline. The entire
  scheme-collision risk rests on it — and the repo has already recorded the
  consequence at `shopping-connector-service.md:259`.
- **Platform behaviour.** Whether `ASWebAuthenticationSession` intercepts a
  foreign `callbackURLScheme` ahead of an installed app that claims it;
  whether Android Custom Tabs reach the OS intent resolver. Both are why this
  plan recommends a navigation hook instead of betting on either.
- **That `api.ah.nl/graphql` tolerates datacenter egress.** Unestablished for
  AH, equally unestablished for Lidl, which already made the move. Open
  question 6.
- **That hCaptcha stands down for a real device on a Dutch residential line.**
  Nobody has run it. Note the plan does not depend on it: if a grid draws
  inside the user's own browser, they tap it with a finger in two seconds. The
  defensible claim is the weaker one — the captcha stops being *our* problem,
  not that it stops existing.
- **Every number in Stage 5.** Frame sizes, latency, per-shutter cost, the
  cost of a per-frame concealment probe. All arithmetic over assumptions. The
  3-5 day spike figure is credible; the 3-6 week figure is unknowable until
  the spike runs.

**Common to every option, and differentiating none of them:** we keep
`appie-ios`, `/mobile-auth/v1/auth/token` and the two GraphQL operations
whatever we do. GraphQL introspection on `api.ah.nl` was disabled in March
2026, so a schema change cannot be detected in advance by anyone. Nothing
here protects against AH renaming `posReceiptsPage`.
