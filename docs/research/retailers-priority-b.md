# Retailer research — priority B (coolblue.nl, mediamarkt.nl, jumbo.com)

Researched 2026-07-27/28. No logins performed, no accounts created, no owner
credentials touched. Everything below comes from public repositories, public
OIDC discovery documents, and unauthenticated GETs of public pages. Where a
POST was made it was a single benign unauthenticated `{__typename}` probe,
noted inline.

**Headline:** the Jumbo operation document is **found and CONFIRMED from
source**, and it invalidates six defaults currently in `JumboOptions.cs`,
including the money unit that file itself flags as "the single most dangerous
value". Details in the Jumbo section and in "Corrections to JumboOptions".

---

## 1. jumbo.com

| field | |
| --- | --- |
| Retailer / domain | Jumbo — `www.jumbo.com` (NL) |
| Tier | `browser_once` — Auth0 browser login, then plain `http` GraphQL on the cookies |
| Auth | oauth+pkce underneath (Auth0), surfaced to the client as a **cookie session** (`user-session`, `auth-session`) |
| Evidence | https://github.com/vghoost360/Jumbo-API — last push **2026-02-22**, created 2026-02-22, 1 star, public, not a fork. Plus live public-page checks 2026-07-28. |
| Confidence | **CONFIRMED-from-source** for the operation documents, variables, headers and money format. **CONFIRMED-live** for the login chain and bot protection. |
| Base URL | `https://www.jumbo.com/api/graphql` |
| Order-history endpoint(s) | GraphQL ops `GetOnlineOrdersAndStoreReceipts` (list), `OrderPagesOrder` (online-order detail + line items), `GetDigitalReceipt` (store-receipt detail) — full documents below |
| Required headers | `Content-Type: application/json`, `apollographql-client-name: JUMBO_WEB-orders`, `x-source: JUMBO_WEB-orders`, `apollographql-client-version: master-v29.2.0-web`, `User-Agent:` a real browser UA, plus the session cookies. **No `jmb-device-id` is sent by the confirmed working client.** |
| Money format | **Orders: decimal string of euros** — `totalToPayMoneyType.amount`, `totals.totalToPay.amount`, `linePriceIncDiscount.amount` (e.g. `"29.83"`). **Not cents.** Catalogue prices (`price.price`, `promoPrice`) *are* cents. Store-receipt lines are comma-decimal strings (`"0,94"`). |
| Bot protection | **Akamai** — `akaas_as` confirmed set live by `www.jumbo.com`; `ak_bmsc` (Akamai Bot Manager) listed in the working client's cookie-capture list. Login page is **Auth0 Universal Login** carrying hCaptcha, reCAPTCHA *and* Turnstile assets. |
| Feasibility | **MEDIUM** — the whole GraphQL contract is now known and APQ is not required, so the fetch loop is straightforward; the cost is entirely in getting through an Auth0 login behind Akamai once. |

### The operation document — CONFIRMED

Source: `app/jumbo_client.py`, `get_orders_and_receipts()`, in the repo above.
This is the answer to the project's highest-value unknown.

```graphql
query GetOnlineOrdersAndStoreReceipts($ordersInput: OrdersInput!, $page: Int, $pageSize: Int) {
  storeReceipts: receiptOverview(page: $page, pageSize: $pageSize) {
    totalResults
    pageSize
    currentPage
    receipts {
      transactionId
      purchaseEndOn
      receiptSource
      store { storeId name }
      pointBalance
    }
  }
  onlineOrders: orders(input: $ordersInput) {
    orders {
      orderId
      customerId
      deliveryDate
      slotStartTime
      slotEndTime
      cutoffTime
      fulfilmentType
      status
      emailAddress
      branchName
      deliveryAddress { street postalCode houseNumber addition city }
      totalToPayMoneyType { amount currency }
    }
    totalCount
  }
}
```

Variables, exactly as sent:

```json
{
  "ordersInput": {
    "offset": 0,
    "limit": 10,
    "direction": "DESC",
    "sortBy": "deliveryDate",
    "statusCategory": "CLOSED"
  },
  "page": 0,
  "pageSize": 10
}
```

Points that matter for the adapter:

- **Two independent result sets in one operation, under aliases.** Online
  home-delivery/collection orders and in-store till receipts are different
  systems. Response paths are `data.onlineOrders.orders[]` and
  `data.storeReceipts.receipts[]`.
- **Two independent paginations.** Online orders page by `offset`/`limit`
  *nested inside `ordersInput`*. Store receipts page by top-level
  `page`/`pageSize`, and `page` is 0-based (the caller's default is `0` and the
  response echoes `currentPage`). A single offset counter cannot drive both.
- `statusCategory: "CLOSED"` is what restricts the list to completed orders.
- `receiptOverview` returns `totalResults`, so the receipt walk can be bounded
  properly instead of relying on a repeated-page heuristic.
- The receipt list carries **no total and no line items** — only
  `transactionId`, `purchaseEndOn`, `receiptSource`, store and points. A total
  for an in-store receipt requires the detail call.

### APQ — CONFIRMED NOT REQUIRED

The working client posts `{"query": …, "variables": …, "operationName": …}`
and never sends an `extensions.persistedQuery` object. There is no APQ
handshake anywhere in `graphql_request()`. Jumbo's router accepts arbitrary
documents from an authenticated session.

So the `PersistedQueryHash` machinery in `JumboOptions`/`JumboAdapter` is
**not needed**. It is harmless to keep as an escape hatch, but do not block on
capturing a hash — there is nothing to capture.

### Order detail (online orders — this is where line items live)

`OrderPagesOrder` — note the name, it is *not* `OrdersPageOrders`. I searched
GitHub for `OrdersPageOrders` and got 10 hits, none of them Jumbo-related; that
name is not publicly corroborated anywhere.

```graphql
query OrderPagesOrder($orderId: Float!, $mergeItemsWithSameSkuAndPrice: Boolean! = true) {
  order(orderId: $orderId, options: {mergeItemsWithSameSkuAndPrice: $mergeItemsWithSameSkuAndPrice}) {
    orderId customerId deliveryDate fulfilmentType
    items {
      lineId: lineNumber
      sku quantity orderedQuantity pickedQuantity unit
      linePriceExDiscount  { amount currency }
      linePriceIncDiscount { amount currency }
      pricePerUnit { price { amount currency } unit }
      promotions { id discount { amount currency } type scope description voucherCode }
      deposits  { sku quantity unitPrice { amount currency } description }
      surcharges { type value { amount currency } }
      substitution { substitutedBy substituteFor }
      details { id sku title subtitle image link category brand … }
    }
    totals {
      totalToPay    { amount currency }
      totalTax      { amount currency }
      itemSubtotal  { amount currency }
      itemDiscounts { amount currency }
      orderDiscounts{ amount currency }
    }
    paymentMethod
    fulfilmentData { reservationId startTime endTime storeId storeV2 { storeId name … } displayAddress { … } }
  }
}
```

`$orderId` is a **`Float!`**, not an `Int`/`ID`. Getting that wrong is a
schema validation error.

### Store-receipt detail — the unpleasant surprise

```graphql
query GetDigitalReceipt($transactionId: String) {
  receipt(transactionId: $transactionId) {
    receiptImage {
      image
      type
      receiptPoints { earned newBalance oldBalance redeemed }
    }
    store { name location { address { city houseNumber postalCode street } } }
    purchaseEndOn
    receiptSource
    customerDetails { customerId loyaltyCard { number } }
    transactionId
  }
}
```

**In-store receipts have no structured line items in the API.**
`receiptImage.image` is a string; when `receiptImage.type == "JSON"` it is a
receipt-*printer* layout document that has to be text-parsed. The working
client walks:

`documents[0].documents[0].printSections[].textObjects[].textLines[].texts[].text`

and then reconstructs items by scanning the flattened text lines:

- the items section starts at a line containing both `OMSCHRIJVING` and `BEDRAG`
- it ends at a line starting with `Totaal`, whose numeric field is the total
- a quantity line matches `^\s*(\d+)\s*[Xx]\s*(\d+[,.]\d+)` (e.g. `2 X 0,94`)
  and *amends the previously emitted item* rather than creating a new one
- `P` in the second text field flags a promotion
- a line whose description is `STATIEGELD` is a deposit
- payment method follows a line starting with `Betaald`
- VAT rows follow `BTW%` / `Bedrag excl`

All amounts there are Dutch comma decimals (`"0,94"`, `"29,83"`) converted with
`replace(",", ".")` then `float()`.

Consequence for the platform: for `receiptSource == "ONLINE"` you can get exact
structured line items from `OrderPagesOrder` (the transaction id is
`<orderId>-…`, and the working client extracts the order id with `^(\d+)-`).
For in-store receipts you get a text-parsed approximation, and reconciliation
of items-against-total should be treated as best-effort, not as a correctness
gate.

### Money format — CONFIRMED, and the current default is wrong

This is the value `JumboOptions.cs` flags as the most dangerous in the file.
Settled from `app/static/app.js` in the same repo, which renders live data:

| line | code | meaning |
| --- | --- | --- |
| 400 | `parseFloat(order.totalToPayMoneyType?.amount \|\| 0).toFixed(2)` then `€${amount}` | order total is **already euros** — no division |
| 475 | `order.totals?.totalToPay?.amount \|\| "0.00"` | euros, string, decimal point |
| 498 | `item.linePriceIncDiscount?.amount \|\| "0.00"` | euros, string, decimal point |
| 152, 327, 763, 799 | `€${(d.price.price / 100).toFixed(2)}` | **catalogue** prices are cents |

The same author divides catalogue prices by 100 and deliberately does not
divide order amounts. The `"0.00"` string defaults confirm the wire type is a
decimal string, not an integer.

So: **`MoneyType.amount` on orders and order lines is a decimal string of
euros. Catalogue `price.price` is minor units.** Two different conventions in
one schema, which is exactly how this trap gets sprung.

### Login chain — CONFIRMED live 2026-07-28, and it has moved

```
https://www.jumbo.com/account/inloggen
  302 → https://www.jumbo.com/api/auth/login?sourceUrl=/&triggerRegistrationFlow=BASIC_REGISTRATION
  302 → https://auth.jumbo.com/authorize
          ?redirect_uri=https://jumbo.com/api/auth/callback
          &client_id=rWRgjhmeqWGeMLyJwf2Zf863o18XiDk0
          &audience=https%3A%2F%2Fjumbo.com%2Fweb
          &scope=openid%20profile%20email%20offline_access
          &response_type=code&state=…
  302 → /u/login?state=…      ← Auth0 Universal Login
  200
```

- `https://www.jumbo.com/inloggen` — the value currently in `JumboOptions.LoginUrl` — returns **404**.
- `https://www.jumbo.com/mijn/account` also returns **404** (the Feb-2026 repo navigates there post-login; that part of it is already stale).
- The Auth0 login page does contain `id="username"`, `id="password"` and a `<form method="POST">`, so the repo's selectors still match the *current* page.
- The page ships hCaptcha (8 refs), reCAPTCHA (18 refs) and Turnstile (4 refs) assets. Auth0 activates one of these on risk score. A headless browser from a datacentre IP is a high-risk signal.
- Cookies observed on the unauthenticated walk: `country`, `language`, `akaas_as` (Akamai), `user-session`.

Jumbo is on **Auth0**, and `https://auth.jumbo.com/.well-known/openid-configuration`
advertises `refresh_token` in `grant_types_supported` and `offline_access` in
`scopes_supported`, with PKCE `S256`. The `/authorize` call above explicitly
requests `offline_access`.

> **This contradicts a load-bearing assumption in the codebase.**
> `JumboGraphQlErrors.Throw()` and `JumboAdapterTests` both assert "Jumbo has
> no refresh path, so 'sign in again' is the honest answer". At the OIDC layer
> a refresh path demonstrably exists. **CONFIRMED** that the tenant supports
> it; **UNVERIFIED** whether the connector can use it, because the web client
> `rWRgjhmeqWGeMLyJwf2Zf863o18XiDk0` redirects to a server-side callback
> (`/api/auth/callback`) and the tokens are exchanged into the `user-session`
> cookie server-side — the browser never holds a refresh token. Treating the
> session as non-refreshable is therefore still *correct behaviour today*, but
> the stated *reason* is wrong, and a mobile Auth0 client (public + PKCE) would
> very likely hand over a real refresh token.

### The mobile path — worth one hour before building anything

`mobileapi.jumbo.com` is a real, separate REST API used by several public
wrappers:

- https://github.com/peternijssen/python-jumbo-api — base `https://mobileapi.jumbo.com/v15/`, login `POST /users/login` (form-encoded `username`/`password`), token returned in the **`X-jumbo-token` response header**, orders at **`GET /users/me/orders?offset=0&count=10`** and `GET /users/me/orders/{orderId}`. Headers `User-Agent: Jumbo/8.6.2 (…)`, `X-jumbo-store: national`, `X-jumbo-assortmentid: ""`. Error `4014` = invalid credentials. **CONFIRMED-from-source**, but the source is old.
- https://github.com/RinseV/jumbo-wrapper — README states plainly: *"Authentication is currently not working, for more info see this issue"*. **CONFIRMED** (read on the repo page).
- Version has moved on (`v15` → `v17` referenced elsewhere), and `/users/login` with a raw password is exactly the endpoint an Auth0 migration retires.

**UNVERIFIED / likely dead:** I did not test it — testing requires credentials,
which is out of scope tonight. Given Jumbo migrated web login to Auth0, a
password-grant mobile endpoint is unlikely to have survived unchanged. But if
`mobileapi.jumbo.com` *does* still answer, it collapses Jumbo from
`browser_once` to `http` and removes Akamai and the captcha from the picture
entirely. That is a large enough prize to justify one credential-holding test
in the morning before any browser work is built.

---

## 2. coolblue.nl

| field | |
| --- | --- |
| Retailer / domain | Coolblue — `www.coolblue.nl` (NL/BE/DE) |
| Tier | `browser_once` — OIDC login in a browser, then session cookie. **Order endpoint shape unknown.** |
| Auth | **oauth+pkce** (OpenID Connect, IdentityServer-style), `code_challenge_method=S256`, `client_id=Webshop` |
| Evidence | **none found** for consumer order history. No public repo, no blog capture, no HA integration exists. Auth details are CONFIRMED-live from Coolblue's own public OIDC discovery document, 2026-07-28. |
| Confidence | **CONFIRMED-from-source** for the auth layer. **SPECULATIVE** for anything about how orders are actually fetched. |
| Base URL | `https://www.coolblue.nl` (app), `https://accounts.coolblue.nl` (identity) |
| Order-history endpoint(s) | **UNKNOWN.** Human-facing page is `https://www.coolblue.nl/mijn-coolblue-account/bestellingen`. No JSON/GraphQL order endpoint discovered; `/graphql` is 404, `api.coolblue.nl` does not resolve. |
| Required headers | Unknown. `_csrfSecret` cookie plus a `csrf` hidden form field are used on the login form, so a CSRF token is likely required on any state-changing call (reads may not need it). |
| Money format | Unknown — no response ever observed. |
| Bot protection | **none seen.** `server: CloudFront`. No Akamai/DataDome/Cloudflare-BM cookie on the homepage or login page. No captcha asset on the login form. |
| Feasibility | **MEDIUM** — the login is a clean, standards-compliant OIDC flow with refresh tokens and no visible bot wall, which is the friendliest front door of the three; but zero prior art means someone has to capture the order call themselves. |

CONFIRMED from `https://accounts.coolblue.nl/.well-known/openid-configuration`:

```
issuer                        https://accounts.coolblue.nl
authorization_endpoint        https://accounts.coolblue.nl/connect/authorize
token_endpoint                https://accounts.coolblue.nl/oauth/token
userinfo_endpoint             https://accounts.coolblue.nl/oauth/userinfo
grant_types_supported         refresh_token, authorization_code,
                              urn:ietf:params:oauth:grant-type:token-exchange
code_challenge_methods        S256, plain
scopes_supported              openid, email, profile, offline_access,
                              openid:customerid, openid:identityroleid,
                              ucp:scopes:checkout_session
```

The live redirect chain, CONFIRMED 2026-07-28:

```
https://www.coolblue.nl/mijn-coolblue-account/bestellingen
  301 → https://www.coolblue.nl/mijn-coolblue-account
  307 → https://www.coolblue.nl/inloggen?returnUrl=…
  302 → https://accounts.coolblue.nl/connect/authorize
          ?client_id=Webshop&response_type=code
          &redirect_uri=https://www.coolblue.nl/inloggen/oidc
          &scope=openid+email+profile+offline_access+openid:customerid
                 +openid:identityroleid+ucp:scopes:checkout_session
          &code_challenge=…&code_challenge_method=S256
          &state=…&nonce=…&ui_locales=nl
  200  (login form: hidden inputs `csrf`, `view`, `view_context`, `username`; `password` field;
        plus separate passwordless-login and password-reset forms)
```

Notable: `offline_access` is requested and `refresh_token` is a supported
grant, so a Coolblue connection is genuinely long-lived rather than needing a
fresh browser login each time — materially better than Jumbo. There is also a
**passwordless login** form (`form__request_passwordless_login`), which is a
second, possibly friendlier, route worth knowing about.

What I could *not* establish, and what someone must capture:

- Whether `/mijn-coolblue-account/bestellingen` is server-rendered HTML (in
  which case: parse HTML) or hydrated from a JSON endpoint.
- Whether the OIDC **access token** is usable as a bearer against any API, or
  whether the site only ever uses the `Coolblue-Session` cookie. The presence
  of `openid:customerid` as a scope hints at a resource server that keys off
  customer id, but that is inference, not evidence.

Explicitly ruled out as routes to consumer orders:

- `https://cpm-api.documentation.coolblue.nl/` — Coolblue Partner Marketplace
  API docs. **The docs themselves are private**: the URL 302s to
  `github.com/pages/auth`, i.e. an access-controlled GitHub Pages site. Seller-
  side, not consumer-side, regardless.
- Coolblue Business "ordering via API" — B2B punch-out for placing orders. Per
  Coolblue's own page it is a custom integration "only offered to customers who
  order from them often". Not a consumer purchase-history API.
- The four `coolblue-api` GitHub repos are all unrelated: two are the well-known
  Coolblue hiring assignment ("insurance API"), two are personal REST exercises.

---

## 3. mediamarkt.nl

| field | |
| --- | --- |
| Retailer / domain | MediaMarkt — `www.mediamarkt.nl` (MediaMarktSaturn) |
| Tier | `browser_interactive` — and tonight, effectively `none-found` |
| Auth | unknown (never reached a login form; the API tier challenges before auth is relevant) |
| Evidence | **none found** for consumer orders. All 10 `mediamarkt api` repos are catalogue/price scrapers or unrelated assignments; the only maintained ones (`simonneutert/fundgrube` 2025-02-01, `cetteup/grube.fund` 2025-10-14) target the public *Fundgrube* clearance feed, not accounts. `hjeroen-git/MediaMarktAPI` last pushed **2021-09-16**. |
| Confidence | **CONFIRMED-live** for the endpoint, the persisted-query manifest and the challenge. **UNVERIFIED** for everything about order data itself. |
| Base URL | `https://www.mediamarkt.nl` |
| Order-history endpoint(s) | GraphQL at **`https://www.mediamarkt.nl/api/v1/graphql`** (CONFIRMED — the SPA config on the orders page contains `"graphql":"/api/v1/graphql"`). The orders *page* is `https://www.mediamarkt.nl/nl/myaccount/orders`. The order operation name is **unknown**. |
| Required headers | Unknown. Apollo client; the app sets a persisted-query hash rather than a document (see below). |
| Money format | Unknown — no response ever observed. |
| Bot protection | **Cloudflare Bot Management + interactive challenge.** `server: cloudflare`, `__cf_bm` cookie, `cf-ray`. A single unauthenticated `{"query":"{__typename}"}` POST to `/api/v1/graphql` returned **HTTP 403** with **`cf-mitigated: challenge`** and a full Cloudflare interactive challenge page (`cType: 'interactive'`), Dutch copy: *"Sorry, dat was te snel voor ons. Om verder te gaan … vul de captcha hieronder in."* |
| Feasibility | **BLOCKED** — the API tier serves a Cloudflare captcha to a non-browser client, and the owner is asleep, so there is no one to solve it. |

Two independent blockers, either of which alone would sink a night:

1. **Cloudflare interactive challenge on the API path.** Not a soft
   fingerprint check — a literal captcha page, returned on the very first
   request, with no session or credentials involved. Confirmed by direct
   observation, not inferred.
2. **`"isPersistedQueryManifestActive": true`** — read straight out of the
   SPA's bootstrap config on `/nl/myaccount/orders`. A persisted-query
   *manifest* (as opposed to plain APQ) means the server accepts only hashes
   it already knows from the shipped manifest and **rejects arbitrary query
   documents**. You cannot write your own orders query. You would have to
   extract the right sha256 from their JS bundle — and re-extract it on every
   front-end deploy, which for a PWA of this size is frequent. The bundle path
   observed is versioned (`/assets/webmobile-pwa/ec9060f/…`), so the hash set
   is tied to a build id that rotates.

There is no MediaMarktSaturn cross-brand consumer API. I looked specifically
for a shared MSS surface across mediamarkt.nl / mediamarkt.de / saturn.de and
found only the shared *Fundgrube* clearance feed, which is public catalogue
data and contains no account information. MediaMarktSaturn's public developer
presence is a careers funnel — they even ship an `x-we-are-hiring` response
header — not a customer API programme.

---

## Recommended build order (value × feasibility)

1. **Jumbo — build it.** Highest value of the three (weekly grocery spend,
   itemised) and it is the only one where the contract is now fully known. The
   GraphQL work is close to mechanical from here. Before writing the browser
   step, spend one hour testing whether `mobileapi.jumbo.com/v17/users/login`
   still works with real credentials — if it does, Jumbo drops to tier `http`
   and you skip Akamai and the captcha entirely. That test costs an hour and
   could save the whole browser path.
2. **Coolblue — capture first, then build.** Second-highest value (large,
   itemised, receipt-worthy purchases) and the best-behaved front door: clean
   OIDC + PKCE, real refresh tokens, no bot wall observed, no captcha on the
   login form. But there is **zero prior art**, so the first task is a DevTools
   capture of `/mijn-coolblue-account/bestellingen` by the owner, not code.
   Do not guess the endpoint — this is precisely the situation that produced
   the wrong `ReceiptPaths` in `JumboOptions`.
3. **MediaMarkt — do not build.** See below.

## Do not attempt

- **MediaMarkt, tonight or otherwise on the current design.** A Cloudflare
  interactive captcha on the first unauthenticated API request, plus a
  persisted-query manifest that forbids your own queries, plus no public prior
  art whatsoever. Three independent hard walls. Even the browser-driven route
  needs a human to clear the captcha, which is the one thing that is
  unavailable tonight. It is also the lowest-value of the three — electronics
  purchases are infrequent, so the payoff per unit of pain is worst here. If
  it is ever revisited, it must be `browser_interactive` with an explicit
  human-in-the-loop captcha step, and it should be scheduled behind everything
  else.
- **Coolblue's documented APIs.** Both the Partner Marketplace API (docs are
  behind GitHub Pages auth) and the Business ordering API (invite-only B2B
  punch-out) are seller/procurement surfaces. Neither reaches a consumer's own
  order history. Do not spend time applying for access hoping otherwise.
- **Building the Jumbo APQ handshake.** CONFIRMED unnecessary — the router
  accepts plain documents. Do not block the adapter on "capturing a persisted
  query hash"; there is nothing to capture.
- **Trusting `github.com/RinseV/jumbo-wrapper` for auth.** Its own README says
  authentication does not work. It is still useful as a shape reference for the
  mobile REST API, but not as a login recipe.

---

## Corrections to `shop-connector/src/ShopConnector.Adapters/Jumbo/JumboOptions.cs`

Every item below is CONFIRMED-from-source or CONFIRMED-live. I changed no code;
these are for the owner to apply.

| current value | should be | why |
| --- | --- | --- |
| `TotalUnit = MoneyUnit.Minor` | **`MoneyUnit.Major`** (decimal euros) | `app.js:400/475` renders `amount` with no `/100`, defaults are `"0.00"`. This is the file's own "most dangerous value" — and it is currently wrong. |
| `ItemUnit = MoneyUnit.Minor` | **`MoneyUnit.Major`** | `linePriceIncDiscount.amount` defaults `"0.00"`, rendered undivided. |
| `LoginUrl = "https://www.jumbo.com/inloggen"` | **`https://www.jumbo.com/account/inloggen`** | the configured URL returns **404** live. |
| `LoginPathMarker = "inloggen"` | needs rethinking | login now leaves `jumbo.com` entirely for `auth.jumbo.com/u/login`. A path marker of `inloggen` will not match the Auth0 page; match on host `auth.jumbo.com` or on `/u/login` instead. |
| `ClientNameHeader = "JUMBO_MOBILE-orders"` | **`JUMBO_WEB-orders`** | the only publicly corroborated working value. GitHub code search for `JUMBO_WEB-orders` returns exactly 1 hit (the working client); `JUMBO_MOBILE` returns 86 hits, none Jumbo-related. `JUMBO_MOBILE-orders` is uncorroborated. |
| `SourceHeader = "JUMBO_MOBILE-orders"` | **`JUMBO_WEB-orders`** | same. |
| `ClientVersionHeader = "30.14.0"` | **`master-v29.2.0-web"`** | matches the web client name; a mobile version string alongside a web client name is an inconsistent fingerprint. |
| `OperationDocument` placeholder | the document quoted above | placeholder would not match the schema. |
| `OffsetVariable`/`LimitVariable` as top-level | **nested in `ordersInput`**, and store receipts use top-level `page`/`pageSize` instead | two separate paginations; a single top-level offset drives neither. This is the exact failure the `A_page_that_repeats_stops_the_walk` test was written to catch. |
| `ReceiptPaths = [data.getOnlineOrdersAndStoreReceipts, …]` | **`data.storeReceipts.receipts`** and **`data.onlineOrders.orders`** | none of the four guessed paths exist. |
| `jmb-device-id` header required | **not sent** by the working client | may be optional; keeping it is probably harmless but it is not a confirmed requirement, and `DeviceId` currently gates the fetch (`A_fetch_on_a_session_with_no_device_id_asks_for_a_new_login`). |
| "Jumbo has no refresh path" | reason is wrong, behaviour is right | Auth0 tenant supports `refresh_token` + `offline_access`. The *web* client keeps tokens server-side so the cookie really is all you get — but the comment as written would stop someone from ever revisiting a mobile PKCE client that does yield a refresh token. |

Also worth encoding: `receiptOverview` returns `totalResults`, so the receipt
walk can terminate on a count rather than on the repeated-page heuristic; and
in-store receipts need the `GetDigitalReceipt` + print-layout text parse before
they have any line items or total at all, which is a second round-trip per
receipt that the current design does not appear to budget for.

## Sources

- https://github.com/vghoost360/Jumbo-API (pushed 2026-02-22) — `app/jumbo_client.py`, `app/static/app.js`, `app/main.py`, `README.md`
- https://github.com/peternijssen/python-jumbo-api — `jumbo_api/jumbo_api.py`
- https://github.com/RinseV/jumbo-wrapper
- https://github.com/bartmachielsen/SupermarktConnector
- https://github.com/simonneutert/fundgrube (2025-02-01), https://github.com/cetteup/grube.fund (2025-10-14), https://github.com/hjeroen-git/MediaMarktAPI (2021-09-16)
- https://auth.jumbo.com/.well-known/openid-configuration
- https://accounts.coolblue.nl/.well-known/openid-configuration
- https://cpm-api.documentation.coolblue.nl/ (access-controlled)
- https://www.coolblue.nl/en/advice/what-is-ordering-via-api-at-coolblue-business.html
- Live unauthenticated header/redirect checks against `www.jumbo.com`, `www.coolblue.nl`, `www.mediamarkt.nl`, 2026-07-28
