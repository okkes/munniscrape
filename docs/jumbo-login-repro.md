# The Jumbo login stall — what it was

**Resolved 2026-07-31.** Kept because the diagnosis is the useful part, and
because the same wall will appear on another provider eventually.

## The symptom

A real connect failed `provider_changed` after 180 seconds. The recorded detail
carried the one thing that mattered:

```
jumbo: the login neither produced a session nor stated an error within 180s;
last url was 'https://auth.jumbo.com/u/login?state=…'
```

Still on Auth0's login screen after three minutes, and the `challenges` table
held **zero rows** for that job — so the adapter had not seen a wall at all.

## What it actually was

Opening the real page headed and dumping the DOM ruled out every cheap
explanation at once:

- `input#username`, `input#password` and `button[type='submit']` each matched
  exactly one visible element. Selectors were fine.
- One submit button on the page (`name=action value=default`, "Inloggen"), so
  nothing was clicking the wrong control.
- Both credentials on one screen, so the two-second password probe was not the
  problem either.

The answer was a hidden field and a container:

```html
<input name="captcha" type="hidden">
<div data-captcha-provider="auth0_v2" data-captcha-sitekey="0x4AAAAAA…"
     class="ulp-captcha-container">
```

`auth0_v2` is Auth0's name for **Cloudflare Turnstile** — the `0x4AAAAAA…`
sitekey prefix is the giveaway. It renders with `render=explicit`, so the widget
appears only when Auth0's risk score asks for it, which is why a probe from a
residential line often sees nothing and a pooled agent sees it every time.

The adapter's `InteractiveCaptchaSelectors` did list
`iframe[src*='challenges.cloudflare.com']`, but nothing matched in practice and
the settle loop timed out instead.

## Why detection was not the fix

Fixing the selector would only have changed the error from `provider_changed`
to `blocked_by_provider`. A Turnstile token is minted by the widget's own
JavaScript, in the browser that rendered it, bound to that browser. There is no
picture to photograph out and no tap to replay back — the relay had exactly one
honest answer to an interactive widget, and that answer is "no".

And solving it is not on the table. This platform's line is written into its
design: challenges are **relayed, never solved**. Nobody here reads a grid,
scores one, or asks a service to.

## The fix

Stream the browser instead of relaying the wall, exactly as Albert Heijn does.
`JumboOptions.LiveLogin` ships `true`: the page goes to the account's owner,
who passes Turnstile in the browser that raised it and types their own password.

What it changed:

| | before | after |
| --- | --- | --- |
| `auth.flow` | `password` | `remote_browser` |
| declared fields | username, password | none |
| `auth.challenges` | image, app_approval, mfa_code | live_view |
| `login_needs_headed_agent` | true | false |
| `offers_credential_store` | true | false — nothing is typed, so nothing to store |

`JumboReturnWatcher` needed no change: "back on `jumbo.com` and off every login
marker" was already the terminal signal, which is why streaming slotted in
without inventing one.

## If it regresses

The failure detail still names the last URL, and `challenges` still records
what was raised. Those two together told the whole story without a live watch,
and would again.
