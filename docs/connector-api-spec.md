# Connector API — wire contract

Companion to [connector-platform-design.md](connector-platform-design.md).
This document is the thing an implementer works from: the provider
manifest schema, the public HTTP surface, the session and challenge
protocols, the agent protocol, and the error taxonomy. It applies
**identically** to the bank connector and the shopping connector — only
the provider set differs.

Conventions used everywhere:

- Base path `https://<host>/v1`. Agent path `https://<host>/agent/v1`.
- All money is **minor units** (integer cents) plus an ISO-4217 currency.
- All ids are opaque and prefixed: `ses_`, `job_`, `chl_`, `agt_`, `prf_`,
  `acc_`, `txn_`, `rcp_`, `tkt_`.
- All dates are ISO-8601. `since`/`until` are inclusive dates (`YYYY-MM-DD`);
  timestamps are RFC-3339 with an offset.
- Every mutating call accepts `Idempotency-Key`.
- Every response carries `X-Manifest-Version` for the provider it touched.

---

## 1 · Provider manifest — "what do you need from me?"

This is the user's requirement that *"the connector's api tells what the
interface is"*. The manifest is the **only** contract munni codes
against: munni renders login forms from it, decides which resources to
offer, and knows before asking whether a provider can run unattended.

### 1.1 Schema

```jsonc
{
  "id": "jumbo",                       // route namespace: /v1/jumbo/*
  "name": "Jumbo",
  "kind": "store",                     // store | bank
  "country": "NL",
  "manifest_version": 3,               // bumped on any breaking change; sealed into bundles
  "logo_ref": "jumbo",                 // munni resolves the asset itself

  // ── how the data is actually obtained (informational for munni,
  //    load-bearing for scheduling and agent routing) ──────────────
  "runtime": "browser_once",           // http | browser_once | browser_interactive | browser_persistent
  "agent": {
    "required": true,
    "class": "pooled",                 // inline | pooled | byo
    "egress": { "country": "NL", "kind": "residential" }
  },
  "unattended_fetch": false,           // can a FETCH run with no human present?
  "login_needs_headed_agent": true,    // can the LOGIN hit a wall only somebody
                                       // at the agent's own browser can pass?
  "logout": "none",                    // none | session | account - what DELETE
                                       // /sessions/{id} does upstream, if anything
  "offers_credential_store": false,    // does a login hand back a sealed copy
                                       // of what was typed? see §2.2

  // ── custody: the user's explicit per-service requirement ─────────
  "secret_custody": "client",          // client | server | agent
  "web_support": "ephemeral",          // ephemeral | none — see §1.5

  // ── the login interface ──────────────────────────────────────────
  "auth": {
    "flow": "password",                // see §1.2
    "config": [],                      // non-secret required settings — see §1.4
    "steps": [
      {
        "id": "credentials",
        "label_key": "connect.jumbo.step.credentials",
        "fields": [
          { "key": "username", "type": "text",     "secret": false,
            "label_key": "connect.field.username", "pattern": "^.{3,64}$", "autofill": "username" },
          { "key": "password", "type": "password", "secret": true,
            "label_key": "connect.field.password", "pattern": "^.{6,128}$", "autofill": "current-password" }
        ]
      }
    ],
    "challenges": ["image", "mfa_code"],   // what this provider MAY raise
    "session": {
      "artifact": "sealed_bundle",
      "ttl_seconds": 2592000,              // 30d — after this, a full re-login
      "refreshable": true,
      "rotates_on_use": true               // persist the bundle from every response
    },
    "reauth": {
      "cheap": false,                      // true = silent refresh possible, no human
      "trigger_codes": ["session_expired", "mfa_required"]
    }
  },

  // ── what you can ask for ─────────────────────────────────────────
  "resources": [
    {
      "id": "receipts",
      "endpoint": "GET /v1/jumbo/receipts",
      "returns": "receipt",
      "params": [
        { "key": "since",   "type": "date", "required": true },
        { "key": "until",   "type": "date" },
        { "key": "include", "type": "enum", "values": ["items", "raw"], "multi": true }
      ],
      "max_history_days": 365,
      "typical_duration_seconds": 25
    }
  ],

  "limits": {
    "min_interval_seconds": 21600,      // 6h — enforced server-side, not advisory
    "concurrency": 1,                   // per session, always 1
    "max_history_days": 365
  },

  "status": { "state": "healthy", "since": "2026-07-20T09:00:00Z", "reason_key": null },

  "notes_key": "connect.jumbo.notes"    // any provider-specific caveat, munni owns the copy
}
```

### 1.2 `auth.flow` — the vocabulary

The user asked for the auth flow kind to be stated per service. These are
the legal values; each implies a different UI in munni and nothing else.

| `flow` | Shape | Example |
| --- | --- | --- |
| `password` | one step, username + password | Jumbo |
| `password_sms` | credentials, then a code delivered out of band to the user's phone | Lidl Plus |
| `password_totp` | password step, then a TOTP code step | some retailers |
| `two_step` | username step → server responds → password/secondary step | ING |
| `mobile_approval` | credentials, then wait for an approval in the provider's app | ING app approval |
| `challenge_response` | provider shows a number, human enters it into a device/app, device returns a code | ASN digipass |
| `qr_scan` | provider shows a QR, human scans with the provider app | ASN app login |
| `oauth_redirect` | human logs in on the provider's own site; the redirect their browser cannot open is handed back | Albert Heijn |
| `device_persistent` | authenticated once on a BYO agent's profile; no login endpoint after that | ASN edge login, Jumbo always-on |

`flow` describes the *happy path*. `auth.challenges` lists what may
interrupt it at any point — a `password` flow can still raise an `image`
challenge if the provider decides to show a CAPTCHA.

### 1.3 Field types

`text` · `password` · `number` · `date` · `select` (with `options[]`) ·
`iban` · `phone`. Every field carries `label_key` (munni owns the nl/en/tr
copy — the connector never emits user-facing English), an optional
`pattern` for client-side validation, and `secret: true` when the value
must never appear in a log, a screenshot, or an artifact.

### 1.4 `auth.config` — required settings that are not secrets

Some providers need non-secret values on every call, known before login
and unchanging afterwards. Lidl Plus is the concrete case: `country` and
`language` appear in the ticket URLs and in the `Accept-Language` and
`Country` headers, and nothing works without them.

These are neither credentials (they are not secret) nor challenges (they
are not raised mid-flow), so they get their own section:

```jsonc
"auth": {
  "config": [
    { "key": "country",  "type": "select", "options": ["NL","DE","AT","BE"],
      "required": true, "label_key": "connect.lidl.country" },
    { "key": "language", "type": "select", "options": ["nl","de","en"],
      "required": true, "label_key": "connect.lidl.language" }
  ],
  …
}
```

Config values are supplied alongside `inputs` on `POST /login`, are
**sealed into the bundle** (so a later fetch does not need them resent),
and are echoed in `GET /v1/{provider}/sessions/{id}` so a UI can display
them. They are not secret, so they may be logged.

Without this section the alternative is a provider-specific special case
in the consuming app — precisely what the manifest exists to prevent.

### 1.5 `web_support` — device classes

The platform supports web/PWA clients with **ephemeral custody**: the
bundle lives in the browser tab's session and is gone when the tab
closes, so a web user re-authenticates on each visit. See platform design
§4.1.1.

| value | meaning |
| --- | --- |
| `ephemeral` *(default)* | web clients may connect; the bundle is not persisted across browser restarts |
| `none` | web clients cannot connect — reserved for a login so heavy that repeating it per visit is unreasonable |

A connector issuing a bundle to a web client uses the manifest's
`ttl_seconds` unless the deployment configures
`Connector:Timeouts:WebBundleMaxTtlSeconds`, in which case it is
`min(provider ttl, cap)`. The caller declares the device class with
`X-Device-Class: native | web` on `POST /login`.

The bound that holds for a web client is `ephemeral` custody itself: the
bundle lives in `sessionStorage` and dies with the tab, so a new tab signs
in again whatever the TTL says. A second deadline inside that one fires
only while somebody still has the tab open, and it cannot be renewed —
the control plane holds credential material for the length of a job and
nowhere else, so it has nothing to refresh with. This said
`min(provider ttl, 3600)` while the shipped default was twelve hours and
nothing connected the two.

**Note the interaction with BYO agents:** for a `secret_custody: agent`
provider the bundle contains no secret at all, only `{agent_id,
profile_id}`. A web client with a BYO agent therefore gets a *fully
persistent* connection — better than a native client without one.

### 1.6 Catalogue endpoints

```http
GET /v1/providers
GET /v1/providers/{id}
```

```json
{
  "providers": [ /* manifests as above */ ],
  "service": { "kind": "store", "version": "1.4.2", "manifest_digest": "sha256:…" }
}
```

`manifest_digest` lets munni cache the catalogue and revalidate cheaply
(`If-None-Match`). `status.state` is what lets munni degrade honestly —
*"Jumbo connections are paused, we're fixing it"* instead of a mystery
spinner — and `unattended_fetch` tells munni whether scheduled sync is even
offerable. It is deliberately about the FETCH alone: `login_needs_headed_agent`
answers the separate question of whether connecting needs somebody standing at
the agent, and the two disagree in both directions. Albert Heijn fetches on its
own overnight and still hands its sign-in to a human; Coolblue does both.

---

## 2 · Public surface — provider-namespaced

The routes are exactly the shape the user asked for. They are **not**
hand-written per provider: one generic handler resolves
`/v1/{provider}/{resource}` against the manifest, so adding a provider is
a manifest plus an adapter, never a controller.

### 2.1 Login

```http
POST /v1/{provider}/login
Authorization: Bearer <m2m>            (+ mTLS client cert)
Idempotency-Key: <uuid>
Content-Type: application/json

{
  "subject": "u_7Kf3…",                // pseudonymous, munni-minted
  "label": "Boodschappen",             // optional, echoed back, never sent upstream
  "inputs": { "username": "…", "password": "…" },
  "consent": { "accepted_at": "2026-07-27T10:00:00Z", "terms_version": "2026-07" },
  "prefer_agent": "agt_…"              // optional; required for device_persistent
}
```

Two possible outcomes:

```jsonc
// completed without a human in the loop
200 {
  "session_id": "ses_…",
  "state": "active",
  "bundle": "sb_v1.k3.9f…",            // persist this on the device
  "credential_bundle": "cb_v1.k3.2a…",  // only where offers_credential_store
  "expires_at": "2026-08-26T10:00:12Z",
  "provider_account": { "display_name": "Jumbo — o.doker@…", "external_id": "…" }
}

// the run stopped to ask something
202 {
  "session_id": "ses_…",
  "state": "awaiting_input",
  "challenge": {
    "id": "chl_…",
    "type": "image",
    "prompt_key": "connect.challenge.captcha",
    "image_url": "/v1/jumbo/login/ses_…/challenges/chl_…/image",   // PNG, auth'd, one-shot
    "expires_at": "2026-07-27T10:03:00Z"
  },
  "progress": { "step": "authenticating", "steps_done": ["queued", "agent_assigned", "opening_provider"] }
}
```

### 2.2 Driving an interactive login

```http
GET    /v1/{provider}/login/{session_id}                       → current state + challenge
GET    /v1/{provider}/login/{session_id}/events                → SSE, live progress
GET    /v1/{provider}/login/{session_id}/challenges/{cid}/image → image/png
POST   /v1/{provider}/login/{session_id}/answer                { "challenge_id": "chl_…", "value": "492013" }
POST   /v1/{provider}/login/{session_id}/cancel
DELETE /v1/{provider}/sessions/{session_id}   { "bundle": "…" }  → upstream logout where possible, then purge
```

The body is optional and the bundle inside it is the only way an upstream
logout can happen at all. Custody is the user's device, so the control plane
holds no credential to log out *with* — a caller that wants the provider told
hands its bundle back, and one that has lost it, or never had it, still gets
its connection removed. A provider whose manifest says `logout: none` ignores
the bundle entirely; nothing upstream is contacted and the consumer must not
claim otherwise.

Disconnect always succeeds locally. A bundle that no longer opens, names
another session, or is simply absent means a logout that cannot be performed —
never a disconnect that may be refused.

Session states — deliberately small, every terminal state has a
user-facing meaning:

```
queued ─► running ─┬─► awaiting_input ─► running ─► active
                   ├─► failed
                   └─► expired
active ─► needs_reauth ─► (new login run) ─► active
active ─► disabled          (user or operator)
active ─► blocked           (provider refuses us)
```

`POST /answer` is idempotent per `challenge_id`; a second answer for an
already-answered challenge returns `409 challenge_already_answered`
rather than confusing the agent.

Progress is **typed, not prose** — munni renders and translates it. The
tempting alternative, free-text status strings like `"Logging in to
ING"`, cannot be translated, cannot be rendered as a progress bar, and
becomes a de-facto API the moment a consumer string-matches it:

```json
{ "state": "running", "step": "downloading_transactions",
  "steps_done": ["queued","agent_assigned","opening_provider","authenticating","selecting_accounts"],
  "started_at": "…", "expires_at": "…" }
```

Legal `step` values are a closed enum in the kit: `queued`,
`agent_assigned`, `opening_provider`, `authenticating`, `awaiting_human`,
`selecting_accounts`, `downloading`, `parsing`, `normalizing`,
`finalizing`, `logging_out`.

#### The credential bundle

Some providers cannot refresh a session at all. Jumbo's Auth0 cookie is one, so
a real sign-in is wanted again within a day — and the alternative is asking the
same person for the same password every morning.

Where the manifest says `offers_credential_store`, a successful login also
returns `credential_bundle`: what the human typed, sealed by the same codec, the
same key and the same associated data as the session bundle. Offer it back on
the next login and nobody is asked again:

```http
POST /v1/jumbo/login
{ "subject": "u_7Kf3…", "credential_bundle": "cb_v1.k3.2a…" }
```

The connector keeps no copy — it is handed over exactly once, like the session
bundle. It is read only when `inputs` is empty, so a caller that sends both is
taken at the word of what the human just typed rather than what the device
remembered, and what comes out is fed through the manifest's own validator, so a
bundle cannot carry a field the provider never declared.

**The consumer's obligation.** Where you keep it is the consumer's decision and
the connector cannot enforce it. On web, `sessionStorage` and nothing longer:
a sealed password surviving a browser restart is what this custody model exists
to avoid. On native, the platform's encrypted store — Keychain, Keystore,
SQLCipher. The demo client does not implement this flow.

**Not offered in production.** The catalogue withholds the value there, so
`include` simply does not list `raw` and a request asking for it is refused as
an unaccepted enum value. Diagnosing a shape change is what a development
deployment is for.

**What it costs, said plainly.** A password re-submitted by machine on a
schedule is one that can be wrong with nobody watching, and this platform never
retries a submitted credential precisely because that is how accounts get
locked. It is refused at boot on a refreshable session (the refresh already
removed the reason), on a login that collects no fields, and on anything but
client custody.

### 2.3 Resuming a stored session

```http
POST /v1/{provider}/sessions/resume
{ "subject": "u_7Kf3…", "bundle": "sb_v1.k3.9f…" }

200 { "ticket": "tkt_…", "session_id": "ses_…", "expires_in": 900, "state": "active" }
401 { "error": { "code": "session_expired", "user_action": "reauth", … } }
```

The ticket is a Valkey entry bound to `(subject, session_id)` with a hard
TTL. It never leaves munni's server.

### 2.4 Fetching data

The user's requested shape, verbatim:

```http
GET /v1/jumbo/receipts?since=2026-06-01&include=items
X-Connector-Ticket: tkt_…

GET /v1/ing/transactions?accounts=savings,credit_card&since=2026-01-01
X-Connector-Ticket: tkt_…

GET /v1/ing/accounts
X-Connector-Ticket: tkt_…
```

#### `include=raw`

Where a resource declares it, `raw` returns the provider's own document beside
each normalised record, as a `raw` field on the record itself.

It exists so a shape change can be diagnosed from real traffic. When a provider
renames a field the normalised record simply loses a value and says nothing
about why; the document that produced it is the thing to read.

Three properties, and all three are the point. It is **opt-in and never a
default** — raw is strictly the more sensitive of the two, because
normalisation drops the fields nobody asked for and this puts them back. It is
stored **on the record's own row**, so the ack that purges the record purges
the document with it and raw never acquires a lifetime of its own; a later pass
that does not ask for it clears what an earlier one left. And it is **declared
per resource**, because an adapter that scraped a page or built a record from
three calls has no single document to hand back, and a manifest offering one
would be promising a field that always arrives empty.

A fetch is a job, so it can take a minute and it can *ask a question*
mid-flight (T3 providers re-authenticate on every run). The response is
therefore one of three:

```jsonc
// finished within the request window (T1/T2 — the common case)
200 {
  "resource": "receipts",
  "data": [ /* normalized records, §4 */ ],
  "cursor": "cur_…",
  "complete": true,
  "session": { "bundle": "sb_v1.k3.a1…", "rotated": true }   // persist the new bundle
}

// still running — poll or subscribe
202 {
  "job_id": "job_…",
  "state": "running",
  "poll": "/v1/jumbo/jobs/job_…",
  "events": "/v1/jumbo/jobs/job_…/events"
}

// stopped to ask the human (same challenge object as login)
202 { "job_id": "job_…", "state": "awaiting_input", "challenge": { … } }
```

```http
GET  /v1/{provider}/jobs/{job_id}                → state, and data when complete
GET  /v1/{provider}/jobs/{job_id}/events         → SSE
POST /v1/{provider}/jobs/{job_id}/answer         → same contract as login
POST /v1/{provider}/{resource}/ack               { "cursor": "cur_…" }   ← "delivered, purge it"
```

`ack` is what keeps the connector a pipe: acknowledged rows are deleted
immediately; unacknowledged rows die after 7 days regardless.

**One-shot convenience form** for callers that want a single round trip
and do not need the ticket:

```http
POST /v1/{provider}/{resource}:fetch
{ "subject": "…", "bundle": "sb_v1…", "params": { "since": "2026-06-01", "include": ["items"] } }
```

Same response shapes. Use `resume` + ticket whenever more than one call
follows, whenever SSE is involved, and whenever the bundle would
otherwise be re-sent repeatedly.

### 2.5 Agents (BYO)

```http
GET    /v1/agents?subject=u_7Kf3…            → this user's agents + health
POST   /v1/agents/enrollment { "subject": "…", "name": "Home VM" }
       → { "code": "AGNT-4F2K-8XQ1", "expires_at": "…", "endpoint": "https://…" }
DELETE /v1/agents/{agent_id}                 → revoke token, orphan its profiles
GET    /v1/agents/{agent_id}/profiles        → per-connection persistent-profile health
```

The enrollment code is short-lived, single-use, HMAC-signed, and carries
the subject — so a BYO agent can only ever serve the user who enrolled it.

### 2.6 Health and operations

```http
GET /v1/health                    → liveness (no auth)
GET /v1/status                    → per-provider state, agent pool, queue depth
POST /v1/admin/providers/{id}/status  { "state": "paused", "reason_key": "…" }   ← the kill switch
```

---

## 3 · Webhooks — payload-free, always

```http
POST https://munni-api…/connectors/{service}/events
X-Signature: t=1785312000,v1=<hmac-sha256 of "t.body">
X-Event-Id: evt_…

{ "type": "session.input_required", "session_id": "ses_…", "subject": "u_7Kf3…", "at": "…" }
```

Event types: `session.state_changed` · `session.input_required` ·
`job.state_changed` · `data.available` · `provider.status_changed` ·
`agent.state_changed`.

The event says *something happened*. munni then pulls over its own
authenticated channel. **No financial or receipt data ever rides a
webhook body.** Signatures are replay-windowed (±5 min) and `X-Event-Id`
is deduplicated by munni.

---

## 4 · Normalised output

One schema for every provider. Provider-shaped output is not an option —
the whole value of the connector is that a caller never learns which
provider a record came from.

```jsonc
// account
{ "id": "acc_…", "external_id": "NL91INGB0417164300",
  "type": "current" | "savings" | "credit_card" | "loan",
  "display_name": "Oranje Spaarrekening",
  "iban": "NL91…", "masked_number": "•••• 1234",
  "currency": "EUR",
  "balance": { "value": 1250045, "as_of": "2026-07-22T04:10:00Z" } }

// transaction
{ "id": "txn_…", "external_id": "…", "account_id": "acc_…",
  "booked_at": "2026-07-19", "value_at": "2026-07-19",
  "amount": { "value": -4231, "currency": "EUR" },
  "counterparty": { "name": "JUMBO 1234 UTRECHT", "iban": null },
  "description": "Betaalautomaat 19-07-2026 17:42",
  "kind": "card_payment" | "transfer" | "direct_debit" | "interest" | "fee" | "other",
  "content_hash": "sha256:…" }

// receipt
{ "id": "rcp_…", "external_id": "…",
  "merchant": { "id": "jumbo", "name": "Jumbo", "store_name": "Jumbo Utrecht CS" },
  "purchased_at": "2026-07-19T17:42:00+02:00",
  "total": { "value": 4231, "currency": "EUR" },
  "payment": { "method": "card", "card_last4": "1234", "iban_tail": "4300" },
  "items": [ { "name": "Melk halfvol 1L", "quantity": 2,
               "unit_price": { "value": 119 }, "total": { "value": 238 },
               "discount": { "value": -30, "label": "2e halve prijs" } } ],
  "content_hash": "sha256:…" }
```

`content_hash` plus `(session_id, external_id)` uniqueness gives
idempotency for free: a re-run never duplicates and munni may safely
retry any pull.

`payment.iban_tail` and `payment.card_last4` exist because matching a
receipt to a bank transaction on amount and date alone is ambiguous —
two €12.40 purchases on the same day are common. The payment tail is what
disambiguates them, so it must be populated wherever a provider exposes
it, and its absence must be explicit (`null`, not omitted) so the
consumer knows matching will be weaker rather than silently guessing.

---

## 5 · Error taxonomy

Never a bare 500. Every error is:

```json
{ "error": {
    "code": "invalid_credentials",
    "retriable": false,
    "user_action": "reauth",
    "message_key": "connect.error.invalid_credentials",
    "detail_id": "err_…",
    "retry_after_seconds": null
} }
```

| `code` | HTTP | `retriable` | `user_action` |
| --- | --- | --- | --- |
| `invalid_credentials` | 401 | **never** | `reauth` |
| `session_expired` | 401 | no | `reauth` |
| `mfa_failed` | 401 | no | `reauth` |
| `mfa_timeout` | 408 | yes | `retry` |
| `challenge_expired` | 410 | yes | `retry` |
| `blocked_by_provider` | 403 | no | `wait` |
| `provider_changed` | 502 | no | `wait` — pages the operator, flips status to `degraded` |
| `provider_unavailable` | 503 | yes | `retry` |
| `rate_limited` | 429 | yes | `wait` (+ `retry_after_seconds`) |
| `agent_unavailable` | 503 | yes | `start_your_agent` |
| `unsupported_resource` | 400 | no | `none` |
| `consent_expired` | 403 | no | `reconnect` |
| `internal` | 500 | yes | `retry` |

Two rules matter more than the list:

- **`message_key`, never English prose.** munni owns the copy in nl/en/tr.
- **`invalid_credentials` never auto-retries, anywhere, ever.** Three
  retries locks a real bank account. Enforced in the kit's retry policy,
  not left to adapters. `retriable: false` on this code is a compile-time
  constant.

---

## 6 · Agent protocol (internal)

Agents authenticate with a per-agent bearer token scoped to `/agent/v1/*`
and nothing else. Every call is outbound from the agent; the control
plane never initiates a connection to an agent.

```http
POST /agent/v1/enroll
  { "code": "AGNT-4F2K-8XQ1", "name": "Home VM", "capabilities": { … } }
  → { "agent_id": "agt_…", "token": "…", "heartbeat_seconds": 30 }

POST /agent/v1/heartbeat
  { "capabilities": { "providers": ["asn"], "runtimes": ["browser_persistent"],
                      "egress": { "country": "NL", "kind": "residential" },
                      "max_concurrency": 1 },
    "profiles": [ { "id": "prf_…", "provider": "asn", "healthy": true, "last_ok": "…" } ],
    "load": { "running": 0 } }
  → { "lease_ttl_seconds": 120, "provider_status": { … }, "revoked": false }

POST /agent/v1/jobs/lease            (long-poll, ≤ 30s)
  { "accept": ["login", "fetch", "refresh", "logout"] }
  → 200 { "job_id": "job_…", "provider": "jumbo", "kind": "fetch",
          "params": { "since": "2026-06-01", "include": ["items"] },
          "material": { … },              // UNSEALED, one-shot, never persisted by the agent
          "profile_id": "prf_…",          // T4 only
          "lease_expires_at": "…",
          "limits": { "timeout_seconds": 300, "politeness_ms": 800 } }
  → 204 (nothing to do — reconnect)

POST /agent/v1/jobs/{id}/renew        → extends the lease
POST /agent/v1/jobs/{id}/progress     { "step": "authenticating", "steps_done": [ … ] }

POST /agent/v1/jobs/{id}/challenge    (multipart/form-data)
  part "meta":  { "type": "image", "prompt_key": "…", "expires_at": "…",
                  "crop": { "x": 12, "y": 340, "w": 260, "h": 90 } }
  part "image": redacted PNG
  → { "challenge_id": "chl_…" }

GET  /agent/v1/jobs/{id}/answer       (long-poll until expires_at)
  → 200 { "challenge_id": "chl_…", "value": "492013" }
  → 408 challenge expired — fail the job

POST /agent/v1/jobs/{id}/result
  { "data": [ … ], "session_material": { … }, "provider_account": { … } }

POST /agent/v1/jobs/{id}/fail
  { "code": "provider_changed", "detail": "selector .challenge-number vanished",
    "artifacts": { "screenshot": "<base64 redacted png>", "dom_digest": "sha256:…" } }
```

Leases have a TTL renewed by heartbeat. **A dead agent's job returns to
the queue exactly once, then fails** — never a retry loop, because for a
`login` job a retry loop is an account lockout.

### 6.1 The adapter contract

Each provider is a plugin. This is the whole surface an adapter author
sees; adapters never touch the database, never call munni, and never
decide retry policy:

```csharp
public interface IProviderAdapter
{
    ProviderManifest Describe();
    Task<LoginResult>  LoginAsync(IJobContext ctx, CancellationToken ct);
    Task<FetchResult>  FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct);
    Task               LogoutAsync(IJobContext ctx, CancellationToken ct);
}

public interface IJobContext
{
    IReadOnlyDictionary<string, string> Inputs { get; }   // login only, secret-tainted
    SessionMaterial? Material { get; }                    // storageState, tokens, profile ref
    IBrowserLease Browser { get; }                        // lazily launched; T1 never touches it
    HttpClient Http { get; }                              // politeness limiter pre-installed

    void Progress(JobStep step);
    Task<string> AskAsync(Challenge challenge, CancellationToken ct);   // relays to the human
    void Emit(SessionMaterial material);                  // what gets sealed into the new bundle
}
```

`AskAsync` is the single call that implements the whole user requirement
about challenges: the adapter hands over a typed challenge and a
screenshot, and gets back the human's answer or a timeout. Everything
between — upload, redaction, webhook, SSE, munni, the client, the answer
coming back — is the platform's problem.

### 6.2 Keeping adapters alive

- **Fixture tests.** Every adapter ships recorded responses and DOM
  snapshots; parsing is tested fully offline in CI. This is what catches
  a shape change on the parse side without a live account.
- **Canaries.** A scheduled run against a dedicated test account per
  provider. Failure flips `provider_status` to `degraded` and alerts,
  before real users notice. The difference between "we knew" and "users
  told us".
- **Defensive parsing, with one exception.** Tolerate extra fields,
  tolerate a field moving or gaining an alias, never throw on an
  unexpected shape. The exception is **money**: a value whose unit is
  ambiguous is never guessed. Each adapter declares its unit per field
  and the kit asserts the result reconciles — see the shopping plan §4.2.
  Guessing wrong about cents-versus-euros corrupts financial data
  silently, which is the one failure mode worse than crashing.
- **`provider_changed` is a distinct, non-retriable error** that pages the
  operator. A scraper fleet without this signal is unmaintainable.

---

## 7 · Sealed bundle format

```
sb_v1.<kid>.<b64url nonce (12B)>.<b64url ciphertext>.<b64url tag (16B)>
```

- **AES-256-GCM.** Key selected by `kid`, from `BUNDLE_SEAL_KEY_<kid>`.
- **AAD** = `provider_id | subject | manifest_version`.
- **Plaintext** (compressed JSON):

```jsonc
{
  "v": 1,
  "session_id": "ses_…",
  "provider": "jumbo",
  "issued_at": "2026-07-27T10:00:12Z",
  "expires_at": "2026-08-26T10:00:12Z",
  "material": {
    // T1/T2: the tokens
    "access_token": "…", "refresh_token": "…",
    // T2/T3: the browser session
    "storage_state": "<playwright storageState json>",
    // T4: NO SECRET — only a pointer
    "agent_id": "agt_…", "profile_id": "prf_…"
  },
  "accounts": [ { "external_id": "…", "type": "savings" } ]   // what this session can reach
}
```

Rejection rules, in order: unknown `kid` → `session_expired`; AAD
mismatch (wrong subject, wrong provider, stale `manifest_version`) →
`session_expired`; `issued_at` older than `session.ttl_seconds` →
`session_expired`. All three produce the same client-visible outcome —
reconnect — so a probing attacker learns nothing from the distinction.

Key rotation is a `kid` bump: new bundles seal with the new key, existing
ones keep validating until their TTL expires, then die naturally. No mass
re-login event.

---

## 8 · Worked example — Lidl Plus, end to end

Lidl is the clearest illustration because it exercises everything: config
fields, a browser login, an out-of-band SMS challenge, and a refresh
token that makes every later fetch headless.

```jsonc
// 1. munni asks what Lidl needs
GET /v1/providers/lidl
→ { "runtime": "browser_once", "secret_custody": "client",
    "unattended_fetch": true, "login_needs_headed_agent": false,
    "logout": "none", "web_support": "ephemeral",
    "auth": { "flow": "password_sms",
              "config": [ {"key":"country","type":"select"}, {"key":"language","type":"select"} ],
              "steps": [ { "fields": [ {"key":"phone","type":"phone"},
                                       {"key":"password","type":"password"} ] } ],
              "challenges": ["mfa_code"] },
    "resources": [ { "id": "receipts", "params": ["since","until","include"] } ] }

// 2. first connect — munni rendered the form (and the two selects) from that manifest
POST /v1/lidl/login
X-Device-Class: native
{ "subject":"u_7Kf3…",
  "config": { "country":"NL", "language":"nl" },
  "inputs": { "phone":"+31612345678", "password":"…" } }
→ 202 { "session_id":"ses_a1", "state":"awaiting_input",
        "challenge": { "id":"chl_1", "type":"mfa_code",
                       "delivery":"sms", "length":6,
                       "prompt_key":"connect.challenge.sms_code",
                       "expires_at":"2026-07-27T10:05:00Z" } }
   ↳ meanwhile a browser sits open on the agent, waiting

// 3. the code arrives on the user's phone
POST /v1/lidl/login/ses_a1/answer  { "challenge_id":"chl_1", "value":"590287" }
→ 200 (accepted)

GET /v1/lidl/login/ses_a1
→ { "state":"active", "bundle":"sb_v1.k3.9f…", "expires_at":"2026-10-25T…" }
   ↳ the bundle seals the refresh token plus country/language. munni returns
     it; the native device persists it. Neither munni nor the connector
     stores anything.

// 4. a week later — no browser, no agent decision the caller can see
POST /v1/lidl/sessions/resume  { "subject":"u_7Kf3…", "bundle":"sb_v1.k3.9f…" }
→ { "ticket":"tkt_x9", "expires_in":900 }

GET /v1/lidl/receipts?since=2026-07-20&include=items
X-Connector-Ticket: tkt_x9
→ 200 { "data": [ /* receipts */ ], "cursor":"cur_88", "complete": true,
        "session": { "bundle":"sb_v1.k3.b7…", "rotated": true } }
   ↳ the refresh token rotated upstream, so the bundle did too;
     the device replaces the stored one

POST /v1/lidl/receipts/ack  { "cursor":"cur_88" }
→ 200   ↳ the connector forgets everything it just handed over
```

### 8.1 The same flow on web

Identical, with two differences:

```jsonc
POST /v1/lidl/login
X-Device-Class: web
…
→ { "state":"active", "bundle":"sb_v1.k3.9f…",
    "expires_at":"2026-07-27T11:00:12Z",     // capped at 1h for web
    "custody":"ephemeral" }
```

The client keeps that bundle in the tab session only. When the user
returns tomorrow there is no bundle, so munni shows *"sign in to sync
Lidl"* and step 2 runs again. Everything in between is the same code.

### 8.2 The same flow with a BYO agent

If the user has enrolled an agent, `secret_custody` for the persistent
variant is `agent`, and step 3's result seals a pointer rather than a
token:

```jsonc
GET /v1/lidl/login/ses_a1
→ { "state":"active", "bundle":"sb_v1.k3.c2…" }
   ↳ plaintext inside: { "agent_id":"agt_…", "profile_id":"prf_…" } and nothing else
```

That bundle is safe to hold anywhere, including a web tab or
`localStorage`, because it is not a credential — which is why web plus a
BYO agent is the strongest combination in the whole design.
