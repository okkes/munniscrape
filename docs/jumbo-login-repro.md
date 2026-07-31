# Reproducing the Jumbo login stall

A real connect ends in `provider_changed` after 180 seconds. The error is a
settle timeout, not a refusal: `JumboReturnWatcher` polls the page URL and never
sees a signed-in one, so `SettleAsync` runs out its budget and gives up. What it
does not tell us is *where* the sign-in stopped — and that needs somebody
watching the browser, because every signal the agent has says only "still on a
login URL".

This is the run to make. It needs a headed browser and a real Jumbo account.

## What "signed in" means to the code

`JumboReturnWatcher.IsSignedIn` (JumboAdapter.cs) returns true only when both
halves hold:

1. the URL contains `jumbo.com` (`JumboOptions.CookieDomainSuffix`), **and**
2. it contains **none** of `JumboOptions.LoginUrlMarkers`:
   `auth.jumbo.com`, `/u/login`, `/authorize`, `/api/auth/login`,
   `/account/inloggen`.

So the failure is: after 180 seconds (`LoginSettleSeconds`) the URL still
matched a marker, or was not on `jumbo.com` at all.

## Run it

The local compose stack already runs the agent headed under Xvfb
(`ConnectorAgent__Headless: "false"` in `deploy/docker-compose.local.yml`) — but
for this you want to *see* the browser, so run the agent on the host with a real
display rather than in the container:

```
ConnectorAgent__Headless=false
ConnectorAgent__Providers__0=jumbo
```

Then connect through the demo client, or POST the login directly:

```http
POST /v1/jumbo/login
{ "subject": "u_repro", "inputs": { "username": "…", "password": "…" } }
```

Chromium opens `https://www.jumbo.com/account/inloggen`
(`JumboOptions.LoginUrl`), which 302s to `/api/auth/login` and on to Auth0.

## Watch for these five, in order

Note the URL at each step — the URL is the only thing the watcher reads.

1. **The consent wall.** Dismissal is best-effort and its selectors are marked
   UNCONFIRMED. If a wall is covering the form, the fills below silently miss.
2. **The username field.** `input#username` was on the page on 2026-07-28.
   Does it still exist, and did the value land in it?
3. **The password screen.** Auth0 can split the two credentials across two
   screens; the adapter probes for the password field for only
   `PasswordProbeMs` = 2000 ms after filling the username. If Auth0 now takes
   longer than two seconds to render screen two, the password is never typed
   and the form sits there — **this is the most likely cause.**
4. **The wall.** hCaptcha, reCAPTCHA and Turnstile assets are all confirmed on
   the page and Auth0 activates one on risk score. An interactive widget cannot
   be relayed, which is why Jumbo now declares `login_needs_headed_agent`.
   Did one appear, and was it answered?
5. **The final URL.** If the sign-in *succeeded* in the browser and the job
   still failed, this is the interesting case: the landing URL still matches a
   marker. Copy it verbatim. Fixing that is a one-line change to
   `LoginUrlMarkers` rather than anything structural.

## What to bring back

The URL at each of the five steps, and which of them the browser actually
reached. That is enough to tell the three real possibilities apart:

- **stopped at 3** → widen `PasswordProbeMs`, or probe rather than time-box.
- **stopped at 4** → the wall is real; the manifest already routes this login to
  a headed agent, so the remaining question is relay versus a streamed login.
- **reached 5 and still failed** → a marker is over-matching a signed-in URL.
  The cheapest fix in the file.

`JumboLoginTests` already drives all of this offline through `ILoginPage` and
`IRedirectWaiter`, so whatever the live run shows can be pinned as a test
without a live account afterwards.
