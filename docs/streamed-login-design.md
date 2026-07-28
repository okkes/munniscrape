# Streaming the provider's own login — the design

Written 2026-07-28, after the first live captcha relay and the owner's
decision to build a live view.

Supersedes Stage 5 of `docs/connect-ux-plan.md`, which said "do not build this
yet". It does not supersede Stages 1-4: the redirect work is still the better
answer wherever a provider has a redirect to hand over, and the custody
argument below is the same argument that plan makes, arrived at from the other
side.

---

## What this is

A clipped rectangle of the provider's own login page, rendered in our
Chromium on the agent, photographed 5-12 times a second, streamed to the
account owner's device, with their pointer and their keystrokes replayed back
into it. They type their password into Albert Heijn's page. The page is on our
machine; the fingers are theirs.

It replaces two things:

- **For a captcha:** the still-picture tap relay's round trip. Today a widget
  is photographed, raised as a challenge, polled for by the client at 1200 ms
  (`demo-client/src/DemoClient.Web/wwwroot/app.js:967`), answered, and settled.
  A stream makes that a continuous surface instead of a slideshow.
- **For a login:** the form munni draws from `Auth.Steps`. That form is what
  puts a password into `LoginRequest.Inputs`. A streamed login has no form.

It does **not** replace the relay itself. See "What happens to the tap relay".

---

## The custody change, first, because it is the argument

Today an Albert Heijn password travels this route:

1. The client draws a form from the manifest and collects it.
2. It crosses munni as `LoginRequest.Inputs`.
3. `EfLeasedJobQueue.cs:63` serialises it into `JobRow.InputsJson` — **at rest
   in the connector's own Postgres**, cleared only at terminal state
   (`:391`, `:434`).
4. It is handed to whichever agent leases the job (`:456`).
5. It is deserialised into `LeasedJob.Inputs`, added to
   `AgentJobContext.SecretValues` (`AgentJobContext.cs:81-88`), typed into
   Chromium, and cleared from the DOM by `LoginPage.ClearSecretsAsync`.
6. `ScreenshotRedactor` exists because of step 5.

Under a streamed login, steps 1 through 5 do not happen. The provider declares
no auth fields, so `Auth.AllFields()` is empty, so no field is `Secret`, so
`LeasedJob.Inputs` is empty, so `JobRow.InputsJson` holds nothing. **The
credential stops entering the platform.** It is not encrypted better; it is
not there.

State the comparison honestly in all four directions, because three of them
are wins and one is not:

| | Today | Streamed |
| --- | --- | --- |
| **At rest** | Plaintext in `JobRow.InputsJson` for the job's life | Nothing, anywhere |
| **In transit** | Same three hops, same TLS | Same three hops, same TLS |
| **Against a compromised munni** | A reusable credential, good tomorrow, from anywhere, and good wherever else it is reused | The characters as they pass, plus a session window |
| **In logs** | A string `SecretScrubber` can find and mask | A picture. **No scrubber can find a password in a JPEG.** |

The third row is the biggest single win and it is under-stated by "better at
rest": munni stops holding a durable reusable secret and starts holding a
transient session. The fourth row is a real regression and it has to be closed
structurally rather than by discipline — see N11.

Two things that do **not** change and should not be claimed:

- **The agent is unaffected.** It types the password into Chromium either way.
  A malicious BYO agent owner gains nothing on the credential.
- **Detection is unaffected.** Streaming moves *who types*. It does not move
  our egress, our fingerprint or our automation signal. Lidl was refused by
  reCAPTCHA Enterprise before a widget was ever drawn
  (`connect-ux-plan.md:26-30`). A live view cannot beat a score.

---

## The two inversions, faced

### Inversion 1 — pointer-only is abandoned, and the argument it rested on was never what we said it was

The old argument: `TapAnswer.TryParse` is a closed grammar, so a compromised
munni, a compromised connector or XSS in a client cannot say "navigate",
"type" or "read cookies", **because the grammar has no words for them**
(`connect-ux-plan.md:376-386`).

That argument was half wrong before a single key was added, and the half that
was wrong is the half that mattered. **The safety was the rectangle, not the
vocabulary.** A pointer confined to a 400x600 hCaptcha crop cannot navigate
because there is nothing inside that crop to click. A pointer over a login
form can click "forgot password" and navigate, with the existing grammar,
today. Widening the surface from a captcha tile grid to a login form already
spent the guarantee. The keyboard is not what broke it.

So the honest replacement, and it is weaker:

> A compromised client cannot express navigation, script execution, file
> upload, cookie access or any read, because no event in the grammar carries a
> URL, a selector, a script or a path — the dispatcher is a switch over a
> closed union whose arms call only `IMouse` and `IKeyboard`. What it *can*
> express is bounded by a rectangle the adapter re-measured from the
> provider's own selectors immediately before dispatch, by where the human put
> focus, and by how long the channel lives.

What is still closed by construction, and is worth more than it looks:

- **Key identity is a token from a frozen ~12-member table**, mapped on the
  agent through a `FrozenDictionary` — never a Playwright key string.
  `IKeyboard.PressAsync`'s own documentation in the pinned 1.61.0 XML says
  *"Shortcuts such as key: `Control+o` … are supported as well"*, and
  `IKeyboard.DownAsync` latches a modifier for subsequent presses. So the
  guarantee is **not** "the wire has no word for Control" — that is necessary
  and not sufficient. It is "no relayed string ever reaches `PressAsync`, and
  `down`/`up` are not exposed for keys at all".
- **No modifiers on pointer events either.** `MouseClickOptions.Modifiers`
  exists; `Ctrl+click` opens a link in a new tab. And no secondary button: a
  context menu renders in browser chrome, invisible in a clipped frame, and is
  then navigable with the arrow keys and Enter this design also allows. Both
  are crop escapes that no geometric bound catches.
- **No response channel carrying anything but redacted pixels**, so "read
  `document.cookie`" has nothing to travel back on.

The cost of refusing modifiers is close to zero and worth writing down because
it looks larger than it is: `Ctrl+V` in the streamed page would paste the
*agent's* clipboard, which is empty and shared across a pooled agent. The
human's password manager lives on their device; their paste lands in the
client's own composer and rides as one `text` event.

### Inversion 2 — "never photograph a page holding a secret" is not weakened, it is forked by destination

`ScreenshotRedactor`'s first layer refuses to produce any image while a
manifest-declared secret field or any `input[type=password]` holds content
(`ScreenshotRedactor.cs:249-287`). A streamed login is a page the human is
deliberately filling in. The rule cannot survive unchanged.

It is **not** relaxed. It is scoped, and the scope is a property of the
*capture*, not of the page:

```
enum CaptureIntent { Artifact, Relay, LiveView }
```

- **`Artifact`** — a failure screenshot an operator reads, stored, durable.
  `IsSafeToCaptureAsync`, `CaptureAsync`, `CaptureArtifactsAsync`,
  `DomDigestAsync`: **not one line changes.**
- **`Relay`** — a still the human answers, written to `ChallengeRow.ImageBytes`.
  `ConcealAsync` / `StillConcealedAsync` / `RevealAsync`, the `MaskLocators`
  fallback, `Animations = Disabled`: **not one line changes.**
- **`LiveView`** — a new method beside them, never a parameter on them, with
  its own rules (R1-R7 below).

The intent is chosen **by the platform, from the challenge type, in
`AgentJobContext`** — never by an adapter and never by anything arriving from
a client. This is the same doctrine `ImageForAsync` already applies at
`AgentJobContext.cs:402-413`, where adapter-supplied bytes are re-verified
through `IsSafeToCaptureAsync` *because the adapter holds a real `IPage` and
could have produced them by any route*. An adapter cannot get LiveView rules
because it does not pick.

One machine, three intents, rather than a second class — because all three
share the shadow-root walk, and a second copy is the one that drifts.
`PlaywrightLoginPage.SecretSelectors` (`Support/LoginPage.cs:249`) is already
a documented second copy of this rule; a third would be worse.

#### Is "a password renders as dots" load-bearing, or a comforting story?

**Both, and the halves must be separated.** For `<input type=password>`
Chromium substitutes the replacement glyph in the compositor, so the plaintext
is not in the raster at any point. That is a real property and it is the same
one every screen-share relies on. But it is the page's mask, the page can drop
it, and three things measured or read this week say it is not the guarantee:

1. **The encoded frame size carries the password's length.** Measured, JPEG
   q60, 400x600 login form: 0 chars → 15,935 B; 8 → 16,242; 20 → 16,681;
   32 → 17,086. Monotonic, ~+36 B per dot. Anyone who can count bytes on the
   wire reads the length without decoding a pixel. **PNG over the same range is
   flat and non-monotonic** — 15,964 / 16,068 at 8 chars / 16,069 at 12 /
   16,083 at 32 — because deflate treats a row of identical dots as a run.
2. **The frame timing carries the keystroke rhythm.** With change-suppression
   on, a frame is emitted roughly per keystroke (measured 43-47% of shutters
   at 12 fps). Inter-keystroke timing is a published attack on password
   entropy, and it is worth far more against a six-digit OTP than against a
   password.
3. **The pixels genuinely carry things that are not the password.** A username
   or e-mail renders in plain text in every frame — there is no dots story
   there at all. An SMS/OTP box is `type=text`. And note what a streamed
   provider does to the redactor: `_probes` is built from
   `manifest.Auth.AllFields().Where(f => f.Secret)`
   (`ScreenshotRedactor.cs:230-236`), and a streamed provider declares no
   fields, so **`_probes` is empty and the manifest-driven half of the
   redactor is inert.** The only redaction left is the two hardcoded rules.

The dots argument may be used to say *"we are not routing the password as a
value"*. It may never be used to say *"the frames are safe"*.

#### The seven rules that replace the refusal

**R1 — a LiveView capture is legal only while a viewer is attached.** The
frame POST's response carries `{ viewers, interval_ms, closed }`; zero viewers
for 10 s stops the shutter. Without it a closed tab leaves Chromium
photographing an authenticated browser at 12 fps for the full expiry with
nobody watching — which is also the moment R6 is the only thing left standing.
Every other rule leans on "the human is looking at it right now" being a fact
the code checks.

**R2 — a LiveView frame is never stored.** Not `ChallengeRow.ImageBytes`, not
disk, not a log, not a webhook. One in-memory slot per job, overwritten, dropped
at terminal state. This is a strictly stronger position than the still relay
holds today, and it is what makes the custody claim true. It needs a test, not
a habit.

**R3 — the crop is non-nullable on the live path, and a measurement miss is
`RegionCapture.Refused`.** Never widened to the viewport. This is layer 3 made
structural, and it is the only thing standing between a login relay and a live
remote view of an authenticated grocery account. Verified against the pinned
assembly: `PageScreenshotOptions.Clip` exists; `ScreencastStartOptions` carries
only `OnFrame`, `Path`, `Quality`, `Size`, and `Size` is a max bound preserving
aspect ratio. **There is no clip in the screencast API, and there is no CDP
escape hatch either** — "shrink the viewport until it equals the crop" turns
the guarantee from structural into coincidental and is struck, not filed.

**R4 — the secret set is latched at first sight, never re-derived per frame.**
A "show password" toggle flips `type` from `password` to `text`, at which
point neither `SecretProbeScript`'s `input[type=password]` scan nor
`ConcealScript` matches the element at all — it *silently leaves the secret
set*. So the set is computed once when the stream opens, from element
identity, and membership is permanent for the stream's life.

**R5 — concealment inverts, and only for the fields the human is filling.**
`ConcealAsync` still runs and still hides every secret-bearing element the
human has **not** focused — a provider-prefilled hidden value has no business
in the picture. `StillConcealedAsync` becomes `StillAsExpectedAsync`: the
post-shutter re-check asserts the visible secret set is *exactly* the
authorised set, so a page that revealed a different field mid-capture still
has its bytes thrown away. The predicate changes from "none" to "exactly
these"; the teeth do not change. Failure drops **that one frame**, not the
stream — the "cheaper to throw the bytes away than to be wrong" rule
(`ScreenshotRedactor.cs:367-371`) applied per frame instead of per capture.

But R5 cannot run per frame at the redactor's current cost. See "The shutter
budget" below: it is established once and re-verified on a ~500 ms cadence and
on every navigation, not four DOM walks per frame. Two consequences that must
be written down rather than discovered: `data-connector-vis` then sits in the
DOM of a page whose entire purpose is detecting automation for the whole
login instead of for ~30 ms, and the human can Tab into a concealed field and
type blind.

**R6 — the stream latches shut, and stopping is terminal.** Four independent
stops, any sufficient, none reversible, an `Interlocked.Exchange` checked
after every await:
1. **Navigation off the declared login origin** — `IPage.FrameNavigated`,
   against an exact-host allowlist the adapter declares in its manifest.
   `ManifestValidator` refuses a wildcard, because an adapter that declared
   `*.ah.nl` would keep streaming after a successful login. This must be
   enforced where the manifest is validated, not trusted to adapter
   discipline, for the same reason the validator already refuses an unmarked
   password field (`ManifestValidator.cs:130-135`).
2. **The adapter's own success signal** — the `RedirectWatcher` capture that
   already exists. This must fire *before the job ends*, because the job ends
   after the fetch, and between authentication and job end the browser holds
   the user's address and order history.
3. **`Challenge.ExpiresAt`** — already mandatory, already bounds the wait
   (`AgentJobContext.cs:211-212`).
4. **R1's viewer liveness.**

**R7 — bucketed size, and a fixed clock while a secret field has focus.**
Frames pad to 4 KB buckets, so the observable length stops moving. And while a
latched-secret element has focus, the stream switches from change-driven to a
fixed clock — identical frames emitted anyway, same bucket, indistinguishable
on the wire. Cost is roughly 200 KB/s for the five seconds someone spends
typing a password. No amount of TLS removes a timing channel, and this is the
rule most likely to be reverted by whoever reads the first bandwidth graph, so
it needs an offline test asserting constant inter-arrival times and constant
padded sizes while a secret field is focused.

**And the one combination that gets nothing and risks everything: a stream may
never open on a page the platform typed into.** If any manifest field is
`Secret` and holds content, the *old* refusal stands, unchanged. Enforce the
converse in `ManifestValidator` too — a streaming flow declares no secret
fields, so `AuthInputValidator` (`:53`) refuses a password on arrival with the
`unknown input '<key>'` it already throws. Both paths coexisting for one
provider means munni keeps the strictly worse capability *and* gains the new
one.

---

## Authorization: the live channel gets its own capability

This is the change that does not appear in any of the transport sketches and
it is not optional.

Every consumer-facing challenge route today passes `subject: null`
(`LoginEndpoints.cs:141, 157, 191, 213, 232, 245, 301`), and
`SessionService.RequireAsync:82` then skips its only ownership check. The
job-scoped answer route is weaker still — `JobEndpoints.cs:89-91` looks a job
up by id and provider and takes no session at all. So **at the connector, a
`ses_…` or a `job_…` is the entire capability**, and `Ids.cs` says this is
deliberate. munni adds nothing on the challenge path either
(`RelayEndpoints.cs:225-263`).

That is a defensible trade when the capability buys one captcha PNG and one
one-shot string. It is not a defensible trade for live pixels of a login page
plus a keyboard into an authenticated browser. Deriving `/frame` and `/input`
from ids the client already holds — which is what every transport sketch
proposes, and which is right about *URLs* — silently inherits that decision.

So: **the connector mints a capability at raise time, bound to the session's
subject, single-driver, carried only inside the challenge payload, revoked by
the R6 latch. The frame and input routes authorize on it alone.**

A token is not a hostname, so the no-URLs-in-the-payload rule survives intact:
the client still derives paths from `{service, provider, sessionId,
challengeId}` exactly as `api.js:112-113` already does for the image, and
still never learns a connector hostname.

Five things this closes with one change:

1. The live channel stops being bearer-by-id. The subject is checked once, at
   mint, where `SessionService.RequireAsync` already has the code to do it.
2. `POST /{provider}/jobs/{jobId}/answer` cannot reach a live channel at all,
   so N4's refusal stops being the only thing between a leaked job id and a
   driven browser.
3. **R6's latch gains something to revoke.** Without it the latch is a
   correctness property of one `while` loop on the agent. With it, revocation
   is a control-plane fact: after the latch, every queued and future input
   event is refused at the connector, before it reaches the agent, whatever
   the frame loop is doing.
4. Single-driver, the per-challenge event budget and the refusal counter have
   somewhere to live. There is **no rate limiter anywhere in this codebase** —
   `AddRateLimiter` returns nothing — so a per-capability budget is the
   cheapest place to introduce the first one.
5. A munni bug that hands user A user B's ids yields a refusal rather than a
   screen-share of B's password entry.

What it does not buy, so nobody over-reads it: nothing against a compromised
munni, which sees the token in transit; nothing against XSS in the client,
which holds it legitimately; nothing against a malicious agent, which is
upstream of it.

---

## The blocker that must be fixed before anything long-lived is raised

`ChallengeService.AwaitAnswerAsync` reads the **latest** challenge for the
job:

```csharp
.Where(c => c.JobId == jobId).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct)
```

and `RaiseAsync` does not retire prior challenges. `AgentJobContext.AskAsync`
then returns whatever comes back **without comparing `answer.ChallengeId` to
`raised.ChallengeId`** (`:226-231`).

Today this is invisible, because the still relay raises one challenge at a
time. This design raises **one long-lived `LiveView` challenge and keeps it
open**, and `CaptchaGate` raises further challenges on the same job while it
is open. The moment a second challenge exists, the LiveView's `AskAsync` is
handed the other challenge's answer, and `LogAnswer` applies the wrong
challenge's logging rule to it — which for `AnswerKind = Taps` logs every
coordinate in full (`:389-395`).

**Fix this first, in `AskAsync`, by ignoring an answer whose `ChallengeId` is
not the one raised.** It is a few lines, it is a latent bug today, and every
stage below sits on top of it.

---

## The shutter budget, and why 12 fps is not free

Two independent readings agree and they must be reconciled with a
measurement, not with arithmetic.

Playwright's driver runs `safeNonStallingEvaluateInAllFrames` plus a
`document.fonts.ready` await in `_preparePageForScreenshot`, and another
evaluate-in-all-frames in `_restorePageAfterScreenshot`, on **every**
`ScreenshotAsync`. On top of that `ScreenshotRedactor.CaptureAsync` runs four
more per-frame passes: `IsSafeToCaptureAsync`, `ConcealAsync`,
`StillConcealedAsync`, `RevealAsync`. That is six evaluate-per-frame passes
before a pixel is encoded.

Against a synthetic single-frame local page the shutter measured a **~33 ms
floor, encoding-independent**, and the redactor's probe measured 0.82 ms — from
which "2.5% overhead" follows. But `AlbertHeijnOptions.cs:205-222` confirms
hCaptcha draws **two** iframes on top of `login.ah.nl`'s own document, so N≥3
and realistically 5. At N=5 and six passes that is ~25 ms of probes against a
33 ms shutter — **75%, not 2.5%** — and PNG's own encoder becomes the cost on
high-entropy content (measured 84 ms for a captcha grid at dsf=1, 188 ms at
dsf=2).

Three consequences that are settled regardless of what the measurement says:

- **`Scale = ScreenshotScale.Css`, always, on the live path.** At dsf=2 it
  collapses 50,442 B to 14,407 B at equal or better latency, and it makes one
  image pixel one CSS pixel, which deletes the scale term from the coordinate
  mapping entirely. (Note for the existing code: `CaptureAsync:331-338` sets no
  `Scale`, and the API default is `device` — so on any retina or headed agent
  the *current still relay* is shipping ~3.5x the bytes it needs. That is a
  real finding about shipped code, and for the relay it is a genuine trade — a
  lower-resolution captcha is harder for a human to read — so it is flagged,
  not prescribed, there.)
- **`Animations = Allow` and `Caret` handling are per-intent.** `Disabled`
  fights a page continuously at 12 fps; it must stay for `Relay`, because a
  still of a mid-animation widget is exactly the 12,618-byte spinner defect.
  Getting this backwards regresses a bug that has been paid for once.
- **The token bucket skips rather than queues.** A slow shutter degrades the
  burst tier to ~7 fps instead of building a backlog that arrives late and
  maps into a box that has moved.

And one thing streaming gets for free that the relay cannot have: the
mid-draw spinner defect disappears. `connect-ux-plan.md:415-419` says it
"cannot be eliminated while the mechanism is photograph a thing that is
animating". A half-drawn frame self-heals 80 ms later. It is the only
structural win in the whole design and it should be said out loud.

---

## Stages

Each is independently shippable. The first is small enough to finish and it
proves or kills the approach without a live login, without munni, and
**without touching either of the two inversions.**

### Stage 0 — Measure the shutter, and fix the challenge-id collision (1-2 days)

Not shippable; a gate. Loop a clipped `ScreenshotAsync` against the mock
provider on a page carrying two nested iframes, with the redactor's four
passes in place and then removed, and write the numbers down here. One number
— milliseconds per frame at N=5 — decides whether this is a live view or a
slideshow, and it is the number every cadence and bandwidth figure below
scales with.

In the same pass, fix `AskAsync` to ignore an answer whose challenge id is not
the one it raised.

**Kill criterion:** if a frame with the redactor's per-frame passes costs more
than ~150 ms at N=5 and cannot be brought under ~80 ms by moving R5 to a slow
cadence, this is a remote desktop over a modem and the still relay's 1.5-second
round trip is the better product.

### Stage 1 — The live view for a captcha. Pointer only. No inversions. (1-2 weeks)

**This is the stage that proves or kills, and the reason it is first is that
it needs neither inversion.** A captcha page holds no filled secret, so
`IsSafeToCaptureAsync` passes unchanged. A captcha needs no keyboard, so the
closed grammar stands unchanged. Everything else — the clipped shutter, the
crop discipline, the capability token, the frame slot, the transport, the R6
latch, the client renderer, viewer liveness — is exercised end to end.

Scope:

- `ChallengeType.LiveView` and `ChallengeAnswerKind.Live`, appended.
- `LiveSpec { Width, Height, Input, MaxFps, Origin }` on `Challenge`, nullable.
- `CaptureIntent` on `ScreenshotRedactor` plus `CaptureLiveFrameAsync`.
  `Artifact` and `Relay` behaviour byte-for-byte identical.
- The frame slot: one singleton, one latest frame per job, no `DbSet`, no new
  table, no new column.
- Four endpoints: agent frame POST and input long-poll inside the existing
  `agents` group; consumer frame stream and input POST behind the capability.
- The demo client renders it against the mock provider.

**Transport for this stage: SSE plus POSTs, and that is good enough to start.**
The consumer frame leg is `text/event-stream` proxied by
`RelayEndpoints.ProxyEventsAsync` (`:531`) **verbatim** — it already sets
`X-Accel-Buffering: no`, calls `DisableBuffering()` and flushes per 4 KB
chunk, and `5da4888` fixed the disconnected-reader bug one day ago, which is
why SSE is safe to build on now and would not have been last week. Base64
costs 33%; that is worth paying to avoid making munni build a second proxy
mode before anyone knows whether the feature works. Binary long-poll is a
Stage 3 optimisation and a WebSocket is probably never — it buys 0.7% of bytes
and one RTT and costs a framing protocol, a reconnect state machine, and a
connector that is stateful in a way these endpoints are not. The client never
sees the agent leg, so it can become either later without touching the
consumer contract.

Add `FrameStream.WriteAsync` **beside** `EventStream.WriteAsync` in
`EndpointSupport.cs` rather than reusing it. `EventStream` is a state-differ
that string-compares against the last render and keep-alives at 5 s;
comparing 40 KB blobs is silly, and a 5-second freeze mid-password is not a
keep-alive interval. Reuse its `OperationCanceledException` / `IOException`
discipline, which is the part that matters.

**One `ConnectorSignals` key per direction, not one per job.** The class holds
one `TaskCompletionSource` per key (`:30`, `:40`), so every signal wakes every
waiter on that key — and every wake costs a DB re-read
(`AwaitAnswerAsync:184`). Keying frames and input on the same `job:{id}` means
12 spurious challenge queries a second. And the frame reader must poll its
in-memory slot at ~30-50 ms rather than inheriting `EventStream`'s 5-second
fallback, which is a real design change to a class whose comment says "a
missed signal costs latency and nothing else" — true at one event per human
minute, false at 12 per second.

**How we know it worked:** a human taps a mock captcha tile in the demo client
and sees the result in under 400 ms; a test navigates the page to a foreign
origin and asserts zero further frames; a test asserts no row in the database
contains frame bytes.

### Stage 2 — The keyboard, and the streamed login (2-4 weeks, gated on Stage 1 and on munni)

This is where both inversions land and where the custody payoff arrives.

- The input grammar gains `text { s }` (≤256 chars, dispatched via
  `IKeyboard.TypeAsync`) and `key { token }` over the frozen table. No
  modifiers, no F-keys, no raw key strings, no `down`/`up` for keys.
- `select.set { value }`, answering options the agent reported. A native
  `<select>` popup renders in browser chrome, **outside the page's rendering
  surface**, so it appears in no clipped screenshot — without this the human
  taps a dropdown, watches nothing happen, and concludes the view is broken.
  They would be right.
- The focus fence: keys are refused, never redirected, when the focused
  element's box is not inside the crop. Re-probed after every focus-moving
  event, never latched — a page moves focus constantly (autofocus on load, an
  OTP field auto-advancing, a modal focus-trap), and a cached "focus was fine
  when we started" flag sends the rest of a batch somewhere nobody chose,
  silently, with a 204 on every request. Silently focusing something on the
  human's behalf is us choosing where their password goes.
- `AuthFlow.RemoteBrowser` on the manifest, plus the origin allowlist, plus
  the validator rules.
- `CredentialSubmitted()` latches **conservatively** — on the first `Enter`,
  or the first pointer-down while any latched-secret field holds content. The
  adapter cannot see the keystroke, the provider still counts the attempt, and
  `ProgressReport.CredentialSubmitted`'s own doc (`AgentContracts.cs:153-159`)
  already picks the direction. Over-latching costs a refused safe retry;
  under-latching costs a locked account.
- **At-most-once delivery, by construction, not by flag.** Client-supplied
  monotonic `seq`; a gap is a 409 refusal, never a re-request; the connector
  hands each batch out exactly once and cannot hand it out again. Every other
  channel here retries — `PostProgressAsync` three times — so a retry helper
  is the house style and someone will apply it. A re-delivered `Enter` is a
  second credential submission under a platform rule that says a submitted
  credential is never retried.
- Client-side: local echo (dots from a **counter**, never an accumulated
  string), an offscreen shadow field cleared after every event, and `seq` on
  every gesture so a stale one is dropped with a log line rather than replayed
  at the difference.

### Stage 3 — Production hardening

Viewer liveness wired end to end; adaptive cadence measured by the agent from
its own POST latency (it is the only party with that information); R7's
buckets and fixed clock with their offline test; binary transport if the
bandwidth graph demands it; the `/view/` route prefix excluded from body
logging by construction.

### Stage 4 — Consumers that cannot stream

See below. Do not let this slip behind Stage 3.

---

## What the human sees, and the three places it is currently wrong

**1. The first screen lies today, and it is the most damaging kind of wrong.**
`manifest-form.js:197-199` renders *"This provider asks for nothing at connect
time."* followed by a Connect button, for any manifest with no config and no
steps — and `manifest-form.js` **never reads `auth.flow`**. Under streaming
that sentence precedes a live login form. The user is told they will be asked
nothing and is then handed a password box.

This is why the design adds `AuthFlow.RemoteBrowser` rather than letting
`Auth.Steps == []` carry it. Empty steps *already* means "you are already
signed in, nothing to do" for `DevicePersistent`. The two most different
screens in the product would be indistinguishable in the contract, and the
client would render them identically. One appended enum value fixes it.

**2. There is no copy, and an unwritten copy key is a visible defect.**
`copy.js:231-235` renders an unmapped key verbatim, in monospace, marked as a
bug — deliberately. Ship without writing `connect.challenge.live_login` and
the user sees the literal token above a video of a password box. Today's
`imageChallenge` copy (`challenges.js:183`) says the picture was *"fetched from
the relay, one shot, and redacted before it ever left the agent"*, which under
a stream is false on both counts.

**3. `awaiting_human` renders as "Waiting for you" (`copy.js:93`) — while the
user waits for us.** Connect to first frame is: enqueue → agent long-poll →
*lazy* Chromium launch (`BrowserLease.Started` is
`Volatile.Read(ref _page) is not null`) → navigate a defended page → measure
the crop → raise. Nobody has measured it. `STEP_ORDER` (`app.js:28`) has no
word for "your browser is starting", and that is the first thing the user
reads in front of an empty box.

### The two things that decide whether this reads as a bank app or a broken screen-share

**Show the origin.** A rectangle showing a login form with no domain beside it
is, structurally, what a phishing page is. The domain is the single thing a
person uses to decide whether to type a password. `ChallengeView.Url` is
**already** a provider URL on the wire (`Wire.cs:115`) and `redirectChallenge`
already renders it into an `<a href>` (`challenges.js:536`), so the precedent
exists and the "no URLs in the payload" rule was over-broad — it is about
*connector* paths, not provider identity.

Carry the origin **per frame, reported by the agent**, not from the manifest.
A manifest-declared origin is a claim; an agent-reported one is a check. R6's
latch already computes the current origin to decide whether to keep streaming
and then throws it away. Wiring it to the label makes the trust signal and the
safety latch the same mechanism instead of two, and gives the rule its
strongest form: **no origin, no frame.**

**Ship the focused element's rect, and use it for keyboard avoidance.** In a
normal form the browser scrolls the focused input into view. In a streamed
view it *cannot* — focus is on the client's offscreen shadow field and the
real field is a region of a bitmap. The human taps the password box, the soft
keyboard rises, the box is behind it, and they type blind. That is the worst
moment in the flow and nothing in the transport design owns it.

The data is already proposed for a different purpose: the agent measures
`document.activeElement`'s caret rect via `getClientRects()`, normalises it
against the crop the way a `Tap` is normalised, and ships four numbers beside
the frame so the client can draw its own blinking caret (necessary because
`Caret = Hide` is load-bearing for change detection — measured, `Caret =
Initial` with a focused input gave 2 distinct hashes over 1.8 s where `Hide`
gave 1, so the caret blink alone would fire the change detector at ~2 Hz
forever). The same four numbers pan the picture above the keyboard. Same wire
cost.

### Two resolved disagreements, and why

**Forced masking: no. `PageDefault`, plus PNG, plus R7's buckets.** Forcing
`type=password` back on every frame makes the reveal-password eye a no-op *in
the frames*, and the copy that explains why must say "this is a photograph and
we edit it before you see it" — which destroys the trust model the previous
section is trying to build. And it buys a leak the measurements largely close
by other means: PNG is flat and non-monotonic over 0-32 characters, and PNG
already wins on a login form on both bytes (14,407 vs JPEG q70's 15,777) and
fidelity (Dutch diacritics and 11px small print ring under JPEG below ~q80).
A reveal is then what it actually is — a deliberate act by the account's owner
on their own screen, which the code can log as an event and never as a value.

**Encoder: measured per stream, not fixed.** PNG wins on a login form and
loses to JPEG q60 by 9.5x on a captcha grid (160,164 vs 16,796 B), where it
also fails on *cadence* before it fails on bytes. Frame 0 is taken twice, once
each, 66 ms; the smaller wins; re-measured when the main frame's URL changes
and when the latched encoder's frame exceeds 3x its running median. The bytes
tell you which page you are on; no provider-specific flag is needed.

---

## What happens to the tap relay, and to the adapters

**Keep it. Do not extend it. Do not delete it.**

Nothing in `CaptchaGate`, `PageOps`, `RedirectWatcher`, `MouseTapSurface`,
`TapReplay`, `LoginPage.ClearSecretsAsync`, `TapAnswer` or `Tap` changes. All
four of today's committed fixes stay. Four providers still drive browsers and
the relay is the only proven thing in this entire area — a human's normalised
taps were replayed by `page.Mouse` into a cross-origin hCaptcha frame and
hCaptcha accepted them.

It is also the **degradation path**, and that path already exists and needs no
new code: `challenges.js:126-142` checks `answer_kind` before the type switch,
and an unknown kind falls through to `unknownChallenge`, which explains
itself, dumps the JSON and offers a text box. `TapAnswer.TryParse` refuses
whatever is typed, and the adapter reports a challenge nobody answered. That is
exactly the degradation `Challenge.AnswerKind`'s own doc comment (`:81-88`)
already sanctions.

And it is the fallback the moment a stream cannot open: no viewer, no
capability, a client that did not declare `live_view`, a crop that will not
measure. **`ChallengeType.LiveView` must never be the only way a provider can
be connected**, or a frame-pipeline defect becomes a total provider outage.

`Tap`, `TapAnswer`, `CropRegion`, `RegionCapture` and `TapReplay`'s
`Fits`/`Inside` are **reused verbatim** by the stream. That is the single most
valuable decision in the wire design: `Tap` already clamps to [0,1] as a range
test that doubles as the NaN check, rounds to four digits, and owns
`ToPagePixels(CropRegion)` *"so the mapping cannot disagree with the
encoding"*. The stream and the relay cannot drift apart about what a
coordinate means, and `Scale = Css` deletes the last scale term.

**Adapters:** existing login code is untouched. `IJobContext` is not given a
`HostLiveAsync`, because the platform — not the adapter — opens and closes the
stream in `AgentJobContext.AskAsync` when `Type == LiveView`. That is not
adapter convenience; it is the `ImageForAsync` provenance rule applied to a
much larger capability, and it means an adapter author cannot forget the R6
latch because they never touch it. What an adapter *does* own is the
rectangle and the origin allowlist, because only the adapter knows which box
is the login form — and that is precisely what keeps connection logic out of
munni and out of the client.

Albert Heijn's browser login stays behind `ClientSideAuthorization`, exactly as
`connect-ux-plan.md` Stage 2 describes. Nothing here deletes a recovery path.

---

## The PWA, and consumers that cannot stream

A live surface is refused **up front**, before a job is enqueued and before a
browser is leased — never at the last step.

`LoginRequest` gains `Capabilities: IReadOnlyList<string> = []`.
`SessionService.CreateAsync` refuses when `Auth.Flow == RemoteBrowser` and
`"live_view"` is absent, with the same shape as the existing `WebSupport.None`
refusal at `SessionService.cs:39-43`. Default-deny is right and it is narrow:
the check fires only for `RemoteBrowser`, so no existing consumer against any
existing provider is affected. Assuming "yes" on silence strands a user at the
last step, which is worse than refusing at the first.

For the web PWA specifically:

1. **A streamed login in a desktop tab is fine** and is the easiest place to
   ship Stage 1. Pointer, keyboard, real screen, no soft keyboard problem.
2. **Mobile web is where it gets thin.** The soft-keyboard-covers-the-field
   problem is at its worst, and the keyboard-avoidance rect is a mitigation,
   not a fix.
3. **A consumer that declares no capability gets the existing path** — the
   form for a `Password` flow, the relay for a captcha, the paste box for a
   redirect. Nothing regresses.

And the honest loss, which is absent from every transport sketch and is worse
than the loss versus the redirect flow: **streaming is worse than today's form
for password managers.** Today's form is manager-friendly *by manifest
declaration* — `AlbertHeijnManifest.cs:69,78` set `Autofill = "username"` and
`"current-password"`, and `manifest-form.js:59` renders them as `autocomplete`
attributes, so the manual picker fills the AH entry in one tap. Under
streaming that is gone: the shadow field must be `autocomplete=off` (it exists
to be cleared after every keystroke, not to hold a value), and associating it
with the provider's domain would require the client to know the domain, which
is exactly the provider knowledge the architecture forbids. A user whose
20-character generated password lives in a manager and who has never typed it
must now type it — on a phone keyboard, into a picture, under a countdown.
That is not degraded; for some users it is impossible. The mitigation is a
`manifest.Id`-keyed store in munni, and that is a munni product decision.

Related, and cheap: **the countdown is hostile.** `renderChallenge`
(`challenges.js:35-41`) puts a live clock in the header and disables every
control on expiry. `CaptchaSpec.InteractiveSeconds` is 600; a login stream
wants ≤300. No bank runs a visible timer while you type a password. Suppress
the clock for a live surface and warn once near the end instead.

---

## What nobody verified

Marked, because a design built on these is a design built on wishes.

**Verified against the pinned Microsoft.Playwright 1.61.0 assembly:**
`PageScreenshotOptions.Clip` exists; `ScreencastStartOptions` has
`OnFrame`/`Path`/`Quality`/`Size` and no clip, and `Size` is a max bound;
`ScreenshotType` is `{ Png, Jpeg }` only — **no WebP**, so producing one means
a new .NET image dependency; `Scale` defaults to `device`; `Animations`
defaults to `allow`; `IKeyboard.PressAsync` accepts `Control+o`-style
shortcuts as a plain string and `DownAsync` latches a modifier;
`MouseClickOptions` carries `Modifiers`; `IMouse.WheelAsync(float,float)` takes
**no position** and dispatches at the current mouse location, so a `wheel`
event without a preceding `MoveAsync` scrolls whatever the last click was
over; `IBrowserContext.RouteAsync` + `IRoute.AbortAsync` exist;
`IPage.SetViewportSizeAsync` exists; `IFrame.FrameElementAsync` exists — which
means cross-origin focus containment can compose the inner rect with the frame
element's rect and does **not** have to degrade to frame-level containment.

**Measured this session** (headless Chromium, 1280x720 viewport, 400x600 clip,
medians of 7, two synthetic local pages, no provider contacted): the byte and
latency tables quoted above; byte-identical output and identical SHA-256 for
an unchanged page across a 400 ms gap, so hashing encoded bytes is a valid
change detector; the caret-blink result; the password-length series; the
43-47% typing duty cycle; the redactor probe at 0.82-1.06 ms on one frame.

**Not verified, and load-bearing:**

- **The shutter cost on a real login page with hCaptcha's two real iframes.**
  Everything above was measured on a local `file://` page with one frame, no
  third-party script and no compositor pressure. **This is Stage 0 and it is
  the single number the whole design scales with.**
- **The 43-47% change rate on a real page.** Mine has no animation. A page
  with a spinner, a carousel or an animated error banner changes *every*
  frame, which destroys the idle tier, makes the whole design cost burst
  bandwidth continuously, *and* makes R7's fixed clock run permanently. This is
  the measurement most likely to be wrong in the direction that hurts.
- **Cold start to first frame.** Nobody has measured it, and it decides
  whether the first screen reads as alive.
- **That `document.activeElement` is readable in the frame that matters.** The
  machinery exists (the redactor already walks shadow roots and iterates
  `page.Frames`) and `FrameElementAsync` makes the composition possible — but
  it is untested, and if it fails there is no caret *and* no keyboard
  avoidance, at the worst moment in the flow.
- **That `IKeyboard.TypeAsync` handles a character with no key event** — `ü`,
  an emoji, an IME commit — and whether it falls back to `insertText`
  internally. It matters: a provider whose submit button enables on `keydown`
  will not enable under `insertText`, and the failure looks like a wrong
  password.
- **That a provider's login page tolerates being driven this way at all.**
  Typing at human speed from a datacenter or residential agent is the same
  posture that produced four password submissions against a defended page.
  Some providers reject paste into password fields.
- **That a phone's soft keyboard reliably produces the `beforeinput`/`input`
  deltas the client needs**, under iOS autocorrect and Android gesture typing,
  for a field typed `password`. "Clear after every event, never read `.value`"
  is the entire defence against client-side accumulation and it is the part
  most likely to need revision after contact with a real phone.
- **That Chromium's autofill popup is absent from `ScreenshotAsync` output.**
  Asserted, untested. The mitigation does not depend on it — a fresh Playwright
  context has no saved credentials to offer — but the claim must not be
  repeated as fact.
- **Every bandwidth figure.** The only measured image in this repository is
  the 12,618-byte mid-draw hCaptcha PNG at `LoginPage.cs:56`. Arithmetic from
  the design's own choices gives ~200 KB/s at 5 fps with SSE's 33% inflation,
  ~18 MB for a 90-second login, plus ~1 MB of R7 padding *on purpose*.
- **That munni's client API can proxy a binary or high-rate stream without
  buffering.** `ProxyEventsAsync` proves the SSE case in the *demo client*.
  munni's own code is outside this repository.
- **How munni logs request bodies.** The "`/view/` must be excluded from body
  logging" rule is a structural argument, not an observation about munni's
  code. Someone on that side has to confirm it.

**Known and unfixable, so it must be disclosed rather than mitigated:** the
connector cannot attest what the frame shows, because the agent supplies both
the frame and the URL. A malicious agent renders an arbitrary page that
munni's own copy is telling the human to type their password into, with no
address bar and no padlock, and the platform has no vocabulary to notice — a
`LiveSpec` carries width, height and verbs, not an attestation. Today an agent
that faked a captcha image gained nothing; under streaming it gains a
credential-harvesting surface inside the trusted app. This is the structural
reason a native WebView (where the OS draws the chrome) is a different
security class, and it is the strongest argument in `connect-ux-plan.md` that
this decision overrules. `connect-ux-plan.md:262` already requires the weaker
version of this disclosure for the WebView route; the streamed version is
worse and the copy must say so.

---

## Non-negotiables

These survive whatever else changes. Each is enforceable, not aspirational.

- **N1.** Frame and input routes authorize on a minted, subject-bound
  capability — never on `sessionId` or `jobId`. `POST /{provider}/jobs/{jobId}/answer`
  never grows a live-input sibling.
- **N2.** R6's latch is terminal, fires on the adapter's success signal
  *before* the job ends, and revokes the capability rather than only stopping
  the shutter. Needs a test that navigates to a foreign origin and asserts
  zero further frames.
- **N3.** `CropRegion` non-nullable on the live path; a miss is
  `RegionCapture.Refused`; no viewport fallback; no CDP screencast.
- **N4.** `ChallengeService.AnswerAsync` refuses a live-grammar value that is
  anything but the terminal marker — in `AnswerAsync`, not in the endpoint,
  because `JobEndpoints` bypasses the endpoint. No input event and no frame
  reaches any `DbSet`. Needs a test that drives a streamed login and asserts
  no row contains the typed characters.
- **N5.** A stream never opens on a page the platform typed into, and a
  streaming flow declares no secret fields.
- **N6.** Key identity is a token through a frozen table; no relayed string
  reaches `PressAsync`; no `down`/`up` for keys; no modifiers on pointer
  events; no secondary button.
- **N7.** `Artifact` and `Relay` paths are byte-for-byte unchanged.
- **N8.** Every gesture is refused whole or dispatched whole
  (`TapReplay.cs:80-84` — *"never half-clicking a live page"*), and delivery is
  at-most-once by construction.
- **N9.** `CredentialSubmitted()` over-latches.
- **N10.** Viewer liveness. It is a security rule, not a cost rule.
- **N11.** The agent logs counts, kinds and lengths — never text.
  `AgentJobContext.LogAnswer` (`:354-396`) already draws exactly this line.
  And note that `SecretValues` is built once in the constructor from
  `Job.Inputs` (`:81-88`), so under streaming it is **empty forever and
  `SecretScrubber` protects nothing**. Relayed text must therefore never enter
  any string that can become a failure `detail`: dispatch it inside a wrapper
  that constructs its own message and drops the reference. Adding it to a
  scrub set instead keeps the value alive in agent memory for minutes, which
  is the opposite of the goal.
- **N12.** Every agent leg stays outbound. Nothing dials in to an agent.
- **N13.** `ManifestVersion` does not bump. It is sealed into every bundle as
  AAD and what a bundle means is `SessionMaterial`, which is identical either
  way — `AlbertHeijnManifest.cs:91-94` already reasons this way. Bumping logs
  out every connected user to announce a change to a form.
- **N14.** The client derives every URL from ids it already holds. No
  connector hostname or path in any new payload field. (`ChallengeView.ImageUrl`
  at `Wire.cs:130-132` *is* such a path today and nothing leaks only because
  the tab ignores it — `grep -rn "image_url" wwwroot/` returns nothing. Do not
  add a second one; demote that field to explicitly advisory.)

---

## What the owner has to decide

**1. Split the two decisions, or fund them together?**
They have completely different justifications. Streaming the **login** is
unanswerable: a 1200 ms client poll cannot carry a password and no tuning of
`RelayGridAsync` fixes it. The live view for a **captcha** is weak: `48fb628`
already made the relay multi-round with escalation-only budgeting, so the delta
is ~1.5 s per tap (after a one-line client change to refetch on the SSE
transition that already exists) versus ~250 ms for a new transport in three
repositories. Stage 1 is scoped as the captcha view because it proves the
pipeline without touching either inversion — but if it lands and Stage 2 does
not, we have built the weak half.

**2. Does munni commit before the connector half is built?**
`temp/munni (Copy)/server/src/Munni.Api/` has folders for Accounts, Banking,
Shopping, Sync — and **no `Connectors` folder**. `docs/munni-integration-plan.md`
is a specification handed to a separately managed project, and its §6 already
spent the budget the other way: *"the relay bridges the connector's SSE stream
to the client over munni's existing `/sync/events` channel, as a new event
kind. No new transport, no second EventSource, no CORS."* A frame stream cannot
ride an app-wide sync channel. Streaming forces munni to build the dedicated
second transport its own integration plan bought its way out of. The most
likely failure of this whole project is a finished, correct, unreachable
connector feature waiting on a repo that has agreed to none of it — and it
will look fine, because `unknownChallenge` degrades a strange challenge to a
text box.

**3. Is `MaxConcurrency: 1` acceptable while a human types?**
`deploy/docker-compose.yml:66` sets it, `AgentHost.cs:77` enforces it, and
`infra/stacks/shop-prod.jsonc:65` runs one replica of the `home-browser` pool.
**The shop connector has exactly one browser slot in the world.** A streamed
login parks it for up to `InteractiveSeconds` *and* screenshots on it 5-12
times a second, while every other user's bol / Amazon / Jumbo / Coolblue sync
queues behind it. Two concurrent AH connects means the second user's browser
has not launched yet. Raising it costs memory on the NAS; not raising it caps
the feature at one user at a time.

**4. Do we accept the first session-scoped instance affinity in the platform?**
The frame slot and the input queue are process memory with no database to
re-read, so a `jobId` must route to one connector instance for the life of the
stream. Today that costs nothing — one `shop-api`, no `replicas:` key — and
`InMemoryTicketStore`/`InMemoryIdempotencyStore` are already instance-local
(`docs/README.md:34` names the Valkey seam). But those are affine to *one
request*; this is affine to hundreds of requests from two different clients
over minutes, and the failure is a permanently blank rectangle on a job that
reports healthy. Options: declare and check a single-instance constraint at
startup; add a shared cache; or pin the owning instance on the challenge row
and 307 a non-owner (~50 lines, no new dependency). Frames in Postgres is not
an option — it puts the provider's authenticated pixels at rest in the control
plane, which is the exact custody problem streaming exists to remove.

**5. Are we willing to show the user a login box with no browser chrome?**
`connect-ux-plan.md:262-263` already names this cost for the WebView route:
the user *"has to take munni's word that the box it drew is not reading what
they type."* Streaming is worse — the page is not rendering on their device at
all, it is a picture of a page rendering on a machine they have never seen,
and no attestation is possible from this side of the wire. The origin label is
the mitigation and it is a mitigation, not a fix. If the answer is no, the
answer is Stage 3 of the connect-ux plan and this document stops at Stage 1.

**6. Do we accept losing failure diagnostics entirely?**
`CaptureArtifactsAsync` keeps the refusal, so a failure on a page the human
just typed into produces **no screenshot at all**. That is correct, and it
means the one case you most want a picture of is the one case you cannot have
one. The DOM digest still works. Alert on outcome — token exchange, post-login
selector — not on stack traces, exactly as `connect-ux-plan.md:296-308` argues
for the redirect flow.

**7. Which providers is this actually for?**
Nothing here establishes that bol, Amazon, Jumbo or Coolblue draw a widget in
our Chromium rather than scoring it away, and a live view cannot beat a score.
Streaming is worth building where a human's own *typing* was the missing
thing. It is worth nothing where the browser was refused before anyone could
type.
