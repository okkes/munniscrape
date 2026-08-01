# ShopConnector.Adapters

The shopping providers. Each is an `IProviderAdapter` plus a manifest, and
nothing else — no routing, no database, no retry policy, no user-facing
prose. Adding a provider is a manifest and an adapter, never a change in the
consuming app.

Two columns, not one, because they are two questions and they disagree in
both directions. **Fetch unattended** is whether a stored credential can pull
data at three in the morning with nobody there. **Login needs a headed agent**
is whether connecting can hit a wall that only somebody sitting at the agent's
own browser can pass — false does not mean nobody is needed, it means whoever
is needed can be reached by relay or by a live view of the provider's own page,
from anywhere. Albert Heijn is yes/no; Coolblue is yes/yes.

| Provider | Id | Tier | Agent | Fetch unattended | Login needs headed agent | Custody |
| --- | --- | --- | --- | --- | --- | --- |
| Albert Heijn | `ah` | T2 `browser_once` | pooled, NL residential | yes | no — its page is streamed to the account's owner | client |
| Lidl Plus | `lidl` | T1 `http` | none (inline) | yes | no — the sign-in is in the human's own browser | client |
| Picnic | `picnic` | T1 `http` | none (inline) | yes | no | client |
| Coolblue | `coolblue` | T2 `browser_once` | pooled | yes | **yes** | client |
| bol.com | `bol` | T2 `browser_once` | pooled | **no** | **yes** | client |
| Jumbo | `jumbo` | T3 `browser_interactive` | pooled, NL residential | **no** | no — walled logins are streamed | client |
| Amazon.nl | `amazon-nl` | T3 `browser_interactive` | pooled | **no** | **yes** | client |
| Woo / Magento guest | `woo-guest`, `magento-guest` | T1 `http` | none (inline) | yes | no | client |
| Mock ×6 | `mock-store-*` | T1 / T4 | none / BYO | yes | no | client / agent |

No provider declares an upstream `logout`. Two adapters implement one and
neither can reach it: disconnect enqueues the logout job with no material,
because custody is `client` and the token lives in the sealed bundle on the
user's device.

Register them all:

```csharp
var registry = new ProviderRegistry(ShopAdapters.All(options, timeProvider));
```

`ShopAdapterOptions` is the binding target for a host configuration section.
Every unconfirmed value below is reachable from it, so correcting one after a
provider changes is a deploy-time edit rather than a release.

---

## Read this before enabling Jumbo

**The login is a hybrid, and both credentials are optional.** A real connect on
2026-07-31 sat on `auth.jumbo.com/u/login` for the full 180 seconds and failed
`provider_changed`. The page was carrying Auth0's
`captcha-provider="auth0_v2"` — Cloudflare Turnstile — whose token is minted by
the widget's own JavaScript in the browser that rendered it, against that
browser. There is nothing to photograph out and nothing to tap back, so the
relay could only ever have refused it. It never got that far: no selector
matched the widget, so no challenge was raised and the settle loop ran out.

Auth0 raises it on a **risk score**, so it is there some days and not others.
That is why the answer is not "always type" or "always stream" but both:

1. The username goes in. It is not a secret, so it may sit in the box while the
   page is photographed.
2. The wall is checked — **before the password is typed and before any click**.
3. No wall: the password goes in, the form is submitted, nobody is disturbed.
4. Wall, no credentials, or anything the submit fails to resolve: the page is
   streamed to whoever owns the account and they finish it themselves.

On a walled day the password never enters the DOM at all, which matters twice —
no attempt is spent against the account, and the redactor refuses to photograph
a page holding a secret, so a filled box would relay a live view of nothing.

The one outcome that is **not** escalated is a stated wrong password. That has
to reach the consumer as `invalid_credentials` so a stored credential is
dropped rather than re-submitted by machine tomorrow.

`JumboReturnWatcher` needed no change: "back on `jumbo.com` and off every login
marker" was already the terminal signal for both paths.

**Jumbo's GraphQL protocol is not settled, and the adapter cannot be called
correct until a live capture says so.** This is the blocking discovery task
the service plan names; it is not an implementation detail.

What is confirmed: the endpoint (`https://www.jumbo.com/api/graphql`), the
four headers, the operation name `GetOnlineOrdersAndStoreReceipts`, that
introspection is disabled, and that an Apollo Router sits in front so
automatic persisted queries may be in play.

What is **not** confirmed, and what a capture must settle:

| Unknown | Where it lives | What happens until then |
| --- | --- | --- |
| The operation document | `JumboOptions.OperationDocument` | The default is a placeholder that will not match Jumbo's schema. |
| Variable names | `JumboOptions.OffsetVariable`, `LimitVariable` | Wrong names most likely present as the same page repeating; the fetch loop stops on a page that adds nothing new rather than replaying it. |
| Response shape | `JumboOptions.ReceiptPaths` | No matching path raises `provider_changed`, which pages an operator and degrades the provider. It never returns an empty list, because "you have never shopped at Jumbo" is a worse lie than an outage. |
| APQ or plain documents | `JumboOptions.PersistedQueryHash` | Unset means plain documents. Set it and the adapter sends the hash alone first, falling back to the full document on `PersistedQueryNotFound` — which is also how APQ registers a document. |
| **Money units** | `JumboOptions.TotalUnit`, `ItemUnit` | See below. This is the dangerous one. |
| Payment tail | — | Emitted as explicit nulls. The plan marks Jumbo's payment fields unconfirmed; the consumer needs to know its match is weaker. |
| Login page selectors, CAPTCHA | `JumboOptions.*Selectors` | Candidate lists; a total miss is `provider_changed` with the list in the detail. |

### The money-unit hazard, stated plainly

Reconciliation catches an *inconsistent* pair of units — items in euros
against a total in cents fails loudly. It does **not** catch a *consistently
wrong* pair: if Jumbo states euros and both `TotalUnit` and `ItemUnit` say
`Minor`, every receipt is a hundredth of the truth and reconciles perfectly.
That is why the units are two separate declared settings and why the capture
is blocking. Do not infer them from a sync that looks plausible.

### Failure mapping

`403`, `502` and `504` map to `blocked_by_provider`, never
`invalid_credentials`. Telling a user their password is wrong when the truth
is bot protection sends them to reset a password that was fine, leaves the
block undiagnosed, and — because a credential failure is never retried — is
permanent for that session. The only path that may report
`invalid_credentials` is the login page stating one itself.

---

## Per-provider unconfirmed values

### Albert Heijn (`AlbertHeijnOptions`)

- `ClientId` — **unconfirmed.** The service plan says to read it from
  `gwillem/appie-go` v0.0.12 and not to assume a value from elsewhere. The
  default here exists to make the adapter runnable and must be verified
  before a real user is served.
- `ApiBaseUrl`, `ReceiptsPath` — unconfirmed. The plan states the token
  paths and that receipts are "list, then detail by transaction id", but not
  the origin or the receipt path.
- `ListTotalUnit` (`MajorDecimal`) vs `ItemAmountUnit` / `DetailTotalUnit`
  (`MajorString`) — the same API encodes the same number two ways across its
  two endpoints, which is exactly why the unit is per field.

Confirmed: authorize at `login.ah.nl`, redirect `appie://login-exit`,
exchange at `POST /mobile-auth/v1/auth/token`, refresh at
`POST /mobile-auth/v1/auth/token/refresh`.

### Lidl Plus (`LidlPlusOptions`)

- `ClientSecret` — **unconfirmed.** The token endpoint uses HTTP Basic with
  the client id plus a hardcoded secret component; the component is not in
  the plan and must be read from `yagueto/lidl-plus`. Empty sends
  `LidlPlusNativeClient:`.
- Login page selectors — unconfirmed candidate lists.
- Ticket payload field names — unconfirmed; every read takes aliases.
- `AmountUnit` (`MajorString`) — Lidl states comma-decimal strings.

Confirmed and **not to be "fixed"**: the ticket **list is `/api/v2/`** and
the ticket **detail is `/api/v3/`**. Different API versions on the same
provider is not a typo.

Also confirmed: `LidlPlusNativeClient`, `com.lidlplus.app://callback`, the
scope list (`offline_access` is what yields the refresh token), and the
header block (`App-Version: 999.99.9`, `Operating-System: iOs`,
`App: com.lidl.eci.lidl.plus`, `Accept-Language`, `Country`).

`auth.config` carries `country` and `language`. They are required: they
appear in the ticket URLs and in two headers, and nothing works without them.
Country is uppercased and language lowercased before use.

---

## Mock providers

Six identities, one adapter, zero network calls. They ship before any real
provider and stay useful after: they are how the control plane, the agent
protocol, the challenge relay and the consuming app get exercised end to end
with no account, no browser and no egress IP. Every output is deterministic
given a session id, so tests can assert on ids and content hashes.

| Id | Exercises | Notes |
| --- | --- | --- |
| `mock-store-simple` | the happy path | T1, instant |
| `mock-store-sms` | `mfa_code` relay | accepts `123456`, anything else is `mfa_failed` |
| `mock-store-captcha` | `image` relay | ships real (1×1) PNG bytes; accepts `MOCK1`, case-insensitive |
| `mock-store-slow` | SSE progress, lease renewal | walks seven progress steps with a configurable delay |
| `mock-store-broken` | the `provider_changed` alert path | login succeeds, every fetch fails |
| `mock-store-persistent` | T4 BYO routing | bundle holds `{agent_id, profile_id}` and no secret |

Password `wrong` fails any mock login with `invalid_credentials`. That path
exists so the rule that matters most — a credential failure is never retried
by anything — has an offline test that needs no account to lock.

`MockStoreOptions` shortens the slow profile's step delay and changes the
accepted answers, so a test suite can run the same adapters in milliseconds.

---

## Fixtures

`FixtureCatalog` exposes every recorded payload, embedded in the assembly and
also copied to the build output. Addressed as `"{provider}/{name}.json"`:

```csharp
var body = FixtureCatalog.Read("lidl/ticket-detail.json");
foreach (var name in FixtureCatalog.Names) { /* … */ }
```

A fixture file name may contain exactly one dot — MSBuild flattens folders
into dots and the friendly name is reconstructed by treating the last two
segments as `file.extension`.

| Fixture | Shape |
| --- | --- |
| `ah/token.json` | OAuth2 token response (`access_token`, `refresh_token`, `expires_in`) |
| `ah/receipts-list.json` | bare array; `transactionId`, `transactionMoment` (with offset), nested `total.amount.amount` as a **number in euros** |
| `ah/receipt-detail.json` | `receiptUiItems[]` with `type` (`product` / `bonus` / `divider` / `total` / `text`), `description`, `quantity`, `amount` as **comma-decimal strings**; `payments[]` with `maskedCardNumber` |
| `lidl/token.json` | OAuth2 token response |
| `lidl/tickets-page-1.json` | v2 envelope: `tickets[]`, `page`, `size`, `totalCount` |
| `lidl/tickets-page-2.json` | empty page — terminates pagination |
| `lidl/ticket-detail.json` | v3 detail: `itemsLine[]` with `currentUnitPrice`, `extendedAmount`, `discounts[]` (stated **positive**, negated on the way in), `payments[]`, `currency` as an object |
| `jumbo/orders-and-receipts.json` | `GetOnlineOrdersAndStoreReceipts` — two result sets under the confirmed aliases `data.onlineOrders.orders` and `data.storeReceipts.receipts`; order amounts are **decimal-string euros** (`"31.13"`), receipts state no total at all |
| `jumbo/walk-page-1.json`, `jumbo/walk-page-2.json` | the two independent paginations: `offset`/`limit` inside `ordersInput`, and a top-level zero-based `page` |
| `jumbo/order-detail.json`, `jumbo/order-detail-90118.json` | `OrderPagesOrder` — line items with `linePriceExDiscount`/`linePriceIncDiscount`, a promotion, a deposit and a surcharge |
| `jumbo/digital-receipt.json` | `GetDigitalReceipt` — an in-store receipt has **no structured items**; `receiptImage.image` is a receipt-printer layout carried as a JSON string, with an `OMSCHRIJVING`/`BEDRAG` header, a `2 X 0,94` quantity line and a `Totaal` terminator |
| `jumbo/digital-receipt-no-items.json`, `jumbo/digital-receipt-no-total.json`, `jumbo/digital-receipt-image-only.json` | the three ways that layout fails to read |
| `jumbo/graphql-errors.json` | `UNAUTHENTICATED` transported in a 200 body |
| `jumbo/storage-state.json` | Playwright storage state, including a session cookie (`expires: -1`) and a cookie for another domain that must not be sent |
| `mock/receipts.json` | the mock data set — see below |

Every real-provider fixture is built so its line items and discounts sum
exactly to its stated total. A change that breaks reconciliation therefore
breaks the offline suite, which is the point.

### `mock/receipts.json`

```jsonc
{
  "currency": "EUR",
  "merchant": "Mock Store",
  "receipts": [{
    "external_id": "mock-2026-07-19-0001",
    "purchased_at": "2026-07-19T17:42:00+02:00",   // always a real offset
    "store_name": "Mock Store Utrecht CS",
    "total_minor": 1085,                            // minor units, named so
    "payment": { "method": "card", "card_last4": "1234", "iban_tail": null },
    "items": [{
      "name": "Melk halfvol 1L", "quantity": 2,
      "unit_price_minor": 119, "total_minor": 238,
      "discount": { "amount_minor": -30, "label": "2e halve prijs" }
    }]
  }]
}
```

Amounts are named `*_minor` and read as `MoneyUnit.Minor`. The fixture
follows the same declare-never-infer rule as the real adapters, where it
costs nothing — a fixture whose unit had to be guessed would teach the wrong
habit to everyone who copied it.

**The third receipt (`mock-2026-06-28-0003`) deliberately does not
reconcile**: one item of 329 against a stated total of 500. It is emitted
with `reconciled: false` rather than dropped, and a test should assert
exactly that. Silently dropping it would hide a real purchase; silently
trusting it would hand over a total we know disagrees with its own contents.

---

## Terms-of-service reality check

These adapters read **only the authenticated user's own purchase history**,
only on a run that user initiated or scheduled, one connection at a time,
never more often than `limits.min_interval_seconds`. They implement the read
paths and nothing else — no baskets, no orders, no coupons, no checkout. That
restraint is partly scope and partly risk: Albert Heijn keeps a server-side
"active order" whose state a client that touches order endpoints can leave
inconsistent, and a connector that only reads cannot corrupt a user's
account.

The app headers each provider requires are sent because the API needs them to
route, not to disguise anything. There is **no CAPTCHA solving, no
fingerprint spoofing, no proxy rotation and no retry through a block**. A
CAPTCHA is relayed to the human who owns the account; that is acting as a
user's agent. Solving it would be abuse.

If a provider deliberately refuses us the adapter reports
`blocked_by_provider`, the connection stops, and the provider's status flips.
There is no escalation path and there is not meant to be one.

**Disable path.** Any provider here can be switched off without a deploy:

```http
POST /v1/admin/providers/{id}/status   { "state": "paused", "reason_key": "…" }
```

Every session for that provider then pauses and users get a real message
instead of a mystery spinner. `provider_changed` from any adapter flips the
same switch automatically, and a `blocked_by_provider` from one user's
session is information about the shared egress — it pauses the provider for
everyone during the cool-down rather than burning the IP for the next person.
