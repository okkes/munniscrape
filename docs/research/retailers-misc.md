# Retailer research — "the remainder"

Retailers covered: **Wehkamp, Belsimpel, Apple, Decathlon, Bever, Van Dijk Educatie, Greetz, V&D**

Researched: 2026-07-28 (overnight). Author: research agent `misc`.

## How to read this document

* **CONFIRMED-from-source** = I downloaded and read the actual JavaScript bundle / HTTP
  response / repository source and quoted it. The literal strings in this document were
  copy-pasted out of live production artefacts on 2026-07-28.
* **UNVERIFIED** = I read a claim about it (a README, a search result, a doc page) but did
  not read the code that proves it.
* **SPECULATIVE** = my inference from architecture. Treat as a hypothesis to test, not a fact.

**Nothing in this document was obtained by logging in.** No account was created, no
credential was used, no captcha was solved. Every observation comes from anonymous GETs of
public pages, public JS bundles, public `robots.txt`, public GitHub repositories, and
(for V&D only) anonymous GraphQL *schema introspection*, which is a public schema read and
returns no customer data.

---

## Headline: there is no prior art for any of these eight

I searched GitHub (repository search across `wehkamp`, `belsimpel`, `greetz`, `bever`,
`asadventure`, `vandijk educatie`, `decathlon`, `secretsales`, plus `<retailer> api`,
`<retailer> reverse engineering`, `<retailer>-cli`, Home Assistant `custom_components`)
and the web. **For seven of the eight retailers there is no public reverse-engineering
project of any kind.** The only hits were unrelated (Apify product scrapers, university
assignments, employees' personal repos, a "Wehkamp mail spammer" from 2023).

The one genuine exception is **Apple**, where two small, recent repos exist — and reading
their source *confirms the bad news* rather than offering a shortcut (both drive a visible
browser and scrape the DOM; neither found an API).

So: the evidence in this document is almost entirely first-hand reading of the retailers'
own production JavaScript. That is a stronger form of evidence than a stale README, but it
means I could not observe anything that only exists *after* login. Where that bites, I have
said so explicitly rather than guessing.

---

## Bever — bever.nl

| field | |
| --- | --- |
| Retailer / domain | Bever — `www.bever.nl` (Yonderland group) |
| Tier | `http` for the API surface; **`browser_once` if you must use the OAuth path** |
| Auth | **Two paths exist.** (a) `password` — direct `POST api/oauth/login` with a static Basic client credential, returns a cookie session. (b) `oauth+pkce` via `account.bever.nl/authorization-server` |
| Evidence | No repo — none found. First-hand read of production bundle `https://www.bever.nl/application-resource/react/ECDEV-FE.33-20260724085702/prd/aem.js` (build stamp **2026-07-24**, downloaded 2026-07-28, 4.73 MB) |
| Confidence | **CONFIRMED-from-source** for auth + endpoint list. **UNVERIFIED** for the order *list* endpoint (not present in this bundle). |
| Base URL | `https://www.bever.nl/` (the bundle sets `servicesApi: "/"`, so all `api/...` paths are same-origin, no leading slash in the source) |
| Order-history endpoint(s) | **Order list: NOT FOUND** — see note below. Confirmed order endpoints are tracking-only: `GET api/sh/order/details?hash=<hash>` and `GET api/sh/order/timeline?hash=<hash>`. Adjacent confirmed account endpoints: `GET api/me`, `GET api/authenticated`, `GET api/customer/profile/details`, `GET api/customer/deliveryaddress` |
| Required headers | Login: `Authorization: Basic Y3VzdG9tZXItYXBwOmN1c3RvbWVyLXNlY3JldA==` (decodes to `customer-app:customer-secret`), `withCredentials` (cookie jar). Order tracking: `Authorization: apikey 50f157ef-620d-4cf2-9fb8-bfa0ead3f013`. All calls carry query params `market=nl&mainWebshop=bever&shopId=143&language=nl` |
| Money format | **UNVERIFIED.** Product model uses `prices.SELL.original` (numeric); order money shape unknown until you can see a real order response |
| Bot protection | **AWS WAF** on `account.bever.nl` — confirmed `HTTP 202` + `x-amzn-waf-action: challenge`. This is the *JS/proof-of-work* challenge, **not** a CAPTCHA. `www.bever.nl/api/*` is **clean**: anonymous `GET /api/authenticated` returned a plain `401`, no interstitial |
| Feasibility | **EASY–MEDIUM** — the password login is a single JSON POST with a hardcoded client credential and there is no captcha on it, but the order-list endpoint still has to be discovered from a logged-in session |

### Exact source (copy-pasted from the production bundle)

```js
zy = `Basic Y3VzdG9tZXItYXBwOmN1c3RvbWVyLXNlY3JldA==`,
By = {
  authenticated: `api/authenticated`,
  login: `api/oauth/login`,
  registeredCheckout: `api/oauth/login/checkout`,
  logout: `api/oauth/logout`,
  forgot_password: `api/credentials/forgot`,
  login_social: `api/oauth/login/social`,
  login_guest: `api/buy/login`,
  login_return_guest: `api/returnannounce/login`
}
// login(e, t, n, r, i):
//   method POST, url `${servicesEndpoint}${By.login}`,
//   headers { Authorization: zy },
//   params { ...defaultRequestParams, keepSignedIn: i },
//   withCredentials: true,
//   data { username: n, password: r }
```

```js
O8e = `apikey 50f157ef-620d-4cf2-9fb8-bfa0ead3f013`,
k8e = { details: { client: `api/sh/order/details` },
        timeline: { client: `api/sh/order/timeline` } }
// both: GET, headers { Authorization: O8e }, params { ...defaultRequestParams, hash }
```

### The OAuth path (confirmed by live redirect, 2026-07-28)

`GET https://www.bever.nl/auth/login` returns `302` to
`https://account.bever.nl/authorization-server/oauth2/authorize?code_challenge=…&code_challenge_method=S256`
and sets a cookie `oauth_state` whose value is URL-encoded JSON `{"codeVerifier":"…","cancelUrl":"/"}`.
The full parameter set, taken verbatim from an `href` in `https://www.bever.nl/account.html`:

```
https://www.bever.nl/auth/login
  ?response_type=code
  &client_id=1c873c93-2e45-4eee-baa4-d44eb0651e03
  &scope=openid
  &redirect_uri=https%3A%2F%2Fwww.bever.nl%2Fnl%2Fauth.html
  &finishRegistration=https%3A%2F%2Fwww.bever.nl%2Faccount%2Fregistration-page.html
  &translationLang=nl_nl&shopId=143&market=nl&mainWebshop=bever&language=nl&anaLang=nl
```

Note the PKCE `code_verifier` is generated by a **CloudFront function** and stashed in a
cookie — i.e. the site itself does not do PKCE in the browser. If you replicate the OAuth
flow you must solve the AWS WAF challenge on `account.bever.nl`. **Prefer path (a).**

### Why the order list is missing, and where to look next

`aem.js` is the *shop* bundle (PDP / basket / checkout / wishlist / customer profile).
The my-account order overview is a separate, login-gated micro-frontend that is never
served to an anonymous client — I fetched `/account.html` and the legacy
`clientlib-application.*.js` and neither contains it. Given the naming convention proven
above, the order list is almost certainly `api/customer/order…` or `api/sh/order/…` on the
same origin with the same `defaultRequestParams`. **That is SPECULATIVE — do not build
against it.** Fifteen minutes with a real session and devtools open will settle it.

### This is one connector for six shops

Yonderland runs a single Adobe-AEM-plus-microservices platform (the clientlib path is
literally `etc.clientlibs/platform-asadventure/`, and the CDN is `cdn.yonderland.com`).
I verified the sibling: `GET https://www.cotswoldoutdoor.com/auth/login` returns the
**identical** `302` shape to `https://account.cotswoldoutdoor.com/authorization-server/oauth2/authorize?code_challenge=…&code_challenge_method=S256`.
CONFIRMED for Cotswold; the same pattern is expected (UNVERIFIED) for **A.S.Adventure,
Juttu, Snow+Rock and Runners Need**. Only `shopId` / `market` / `mainWebshop` change.
This is the single highest-leverage finding in this document.

---

## V&D — vd.nl

| field | |
| --- | --- |
| Retailer / domain | V&D — `www.vd.nl` (owned by **Secret Sales**, UK, since 2025) |
| Tier | **`http`** — no browser at any point |
| Auth | `password` → Magento customer bearer token. (Okta mutations also present, see caveat) |
| Evidence | No repo — none found. Live GraphQL **schema introspection** of `https://www.vd.nl/graphql`, 2026-07-28 |
| Confidence | **CONFIRMED-from-source** (the schema itself is the source) |
| Base URL | `https://www.vd.nl/graphql` — Adobe Commerce / Magento 2 with a PWA-Studio "Venia" storefront; origin is `https://backend.secretsales.com/` |
| Order-history endpoint(s) | Standard Magento 2: `mutation { generateCustomerToken(email: String!, password: String!) { token } }` then `query { customer { orders(pageSize: 20, currentPage: 1) { total_count page_info { current_page total_pages } items { id number order_date status total { grand_total { value currency } subtotal { value currency } } items { product_name product_sku quantity_ordered product_sale_price { value currency } } } } } }` |
| Required headers | `Content-Type: application/json`, **`Store: nl_nl`**, `Authorization: Bearer <token from generateCustomerToken>` |
| Money format | **decimal (Float)**. `Money { value: Float, currency: CurrencyEnum }`. Order total is `customer.orders.items[].total.grand_total.value`, currency at `.currency` |
| Bot protection | Cloudflare fronts `www.vd.nl` (the HTML storefront is an SPA), but `/graphql` answered plain `curl` POSTs with **no challenge at all**. No `recaptchaV3Config` field exists on the `Query` type, which means Magento's reCAPTCHA-for-GraphQL module is **not** enabled — no `X-ReCaptcha` header needed |
| Feasibility | **EASY** — this is a documented Adobe API with a published schema, and I verified the exact fields exist on this deployment |

### Verified transcript

```
POST https://www.vd.nl/graphql   {"query":"{__typename}"}
  -> {"data":{"__typename":"Query"}}

POST .../graphql  {"query":"{ storeConfig { store_code store_name base_currency_code locale website_name } }"}
  -> {"storeConfig":{"store_code":"default","store_name":"Secret Sales UK",
                     "base_currency_code":"GBP","locale":"en_GB","website_name":"Main Website"}}

same query with header  Store: nl_nl
  -> {"storeConfig":{"store_code":"nl_nl","store_name":"SecretSales NL",
                     "base_currency_code":"EUR","locale":"nl_NL"}}
```

`Customer` type has an `orders` field (confirmed). `Mutation` type has
`generateCustomerToken(email: String!, password: String!)` (confirmed, with those exact
argument names and non-null types), plus `revokeCustomerToken`, `requestPasswordResetEmail`,
`changeCustomerPassword`. `CustomerOrder` has `number`, `order_date`, `status`,
`total: OrderTotal`, `items: [OrderItemInterface]`, `invoices`, `shipments` (all confirmed).

### Two caveats you must not skip

1. **The schema also exposes `generateOktaLogin`, `generateCustomerTokenFromOkta` and
   `refreshCustomerTokenFromOkta`.** That strongly suggests the *storefront* signs users in
   through Okta, and it is possible that accounts provisioned via Okta have no local Magento
   password, in which case `generateCustomerToken` would reject them. **UNVERIFIED.** Test
   this first; it is a five-minute test and it decides whether this connector is EASY or dead.
2. **V&D is now a Secret Sales storefront**, and the NL store view identifies itself as
   "SecretSales NL". The brand your users typed is V&D; the data you get back is the
   Secret Sales NL order book. Fine for a spending tracker, but label it honestly in the UI.

**Strategic note:** the connector you write here is *generic Magento 2 GraphQL*. The same
code — swap base URL and `Store` header — works against any Adobe Commerce shop. There are a
lot of those in NL. Build it as `MagentoGraphQlConnector`, not as `VdConnector`.

---

## Wehkamp — wehkamp.nl

| field | |
| --- | --- |
| Retailer / domain | Wehkamp — `www.wehkamp.nl` |
| Tier | **`browser_once`** (realistically; possibly `browser_interactive` — see the bot-score note) |
| Auth | `password` **+ risk-based Cloudflare Turnstile**. The captcha token is *conditional*, not mandatory |
| Evidence | No repo — none found. First-hand read of `https://www.wehkamp.nl/login/assets/main-DLIjpnPk.js` and of `window.__APOLLO_STATE__` / `window.__SETTINGS__` in `https://www.wehkamp.nl/login/`, both fetched 2026-07-28 |
| Confidence | **CONFIRMED-from-source** for the login call and the bot protection. **UNVERIFIED** for the order-history endpoint |
| Base URL | `https://www.wehkamp.nl` — API gateway pattern is `https://www.wehkamp.nl/service/<service-name>/…` |
| Order-history endpoint(s) | **NOT DETERMINED.** The order page is `https://www.wehkamp.nl/mijn/bestellingen/`, which returns a *server-side* `302` to `/login?redirectUrl=…` for anonymous clients, so its SPA bundle is unreachable without a session. Login endpoint is confirmed: `POST /service/authentication/turnstile` |
| Required headers | `Content-Type: application/json`, `Accept: application/json, text/plain`, `credentials: include`. Body `{"username":…,"password":…,"jwt_id":<value of the `csrf` cookie>}` plus optional `"captchaToken"` |
| Money format | **UNVERIFIED** |
| Bot protection | **Cloudflare Bot Management + Cloudflare Turnstile.** Sitekey `0x4AAAAAACHKa8buj6Hv-rUS` (it is passed as `interactionRef` — see below). Cookies `__cf_bm`, `_cfuvid` |
| Feasibility | **MEDIUM** — the login call itself is trivial; the obstacle is Cloudflare's opinion of your client, and the order endpoint is still unknown |

### Exact source (copy-pasted from `main-DLIjpnPk.js`)

```js
async function Ue(e, n, s, a, u = "") {
  try {
    const i = { username: e, password: n, jwt_id: bt.load("csrf") };
    s && (i.captchaToken = s);
    const c = await ae(`${u}/authentication/turnstile`, {
      method: "POST",
      credentials: "include",
      headers: { Accept: "application/json, text/plain", "Content-Type": "application/json" },
      body: JSON.stringify(i)
    });
    const f = await c.json();
    if (c.ok) { const b = f?.shopper_id; return Promise.resolve(b) }
    let p;
    switch (f.key) {
      case "auth.failed.account-blocked":     p = "account blocked"; break;
      case "auth.failed.invalid-credentials": p = "invalid credentials"; break;
      …
```

The base `u` comes from the page's own Apollo cache, fetched live:

```js
window.__APOLLO_STATE__ = { "ROOT_QUERY": { "getEnvironment": {
  "interactionRef": "0x4AAAAAACHKa8buj6Hv-rUS",
  "authenticationServiceUrl": "/service/authentication" } } }
```

so the full URL is **`POST https://www.wehkamp.nl/service/authentication/turnstile`**.
Success body contains `shopper_id`.

### The trap: `interactionRef` is the Turnstile sitekey

Do not be misled by the name. In the same bundle:

```js
Ve = (e, n) => ({ action: e, appearance: "execute", execution: "execute",
                  id: e, refreshExpired: "auto", sitekey: n, size: "flexible", theme: "light" });
…
ce = Ve(`login${e.type === S.REGISTER ? "-nieuwe-klant" : ""}`, e.interactionRef);
```

`interactionRef` is passed straight into `sitekey`. `0x4AAAAAA…` is the Cloudflare Turnstile
sitekey format. So the sitekey is **`0x4AAAAAACHKa8buj6Hv-rUS`** — CONFIRMED.

### The other trap: Wehkamp publishes your bot score

Every page ships this, and I got it on a plain `curl`:

```js
window.dataLayer = [{"webview": false}, {"bot_score": "1", "verified_bot": "false"}]
```

Cloudflare bot scores run 1–99 where **1 means "definitely automated"**. A stock HTTP client
scores 1. Since the widget's visibility is conditional (`p ? "display-block" : "is-hidden"`)
and `captchaToken` is only attached when present (`s && (i.captchaToken = s)`), the flow is
**risk-based**: a trustworthy client logs in with no captcha at all; a score-1 client will
be shown Turnstile.

This is genuinely useful for scoping: **you have a live oracle for whether your automation
looks human.** Point your Playwright context at any Wehkamp page and read
`window.dataLayer[1].bot_score` before you ever attempt a login. If you can get that number
up into browser territory, this is `browser_once` (solve Turnstile invisibly once, keep the
`token` cookie). If it stays at 1, it is `browser_interactive` and someone has to click.

### Session model

Anonymous `GET` already sets a JWT session cookie. Decoded header/payload (RS256,
`kid 144122c9-7cd3-4318-85f0-aa6e5677a4bd`):

```json
{ "iss": "gateway", "sub": "<uuid>", "aut": "anonymous",
  "iss_reason": "initial", "pre_auth_shopper": null, "iat": …, "exp": …, "nbf": …, "jti": … }
```

`Max-Age` is 17 280 000 s (**200 days**) on `Domain=wehkamp.nl`. Alongside it: `identifier`
(uuid, 200 days) and `consent`. After login the gateway presumably re-issues `token` with a
different `aut` claim. A 200-day cookie is excellent news for a connector: log in once,
refresh for months. **The 200-day lifetime is CONFIRMED from the `Set-Cookie` header; the
post-login claim change is SPECULATIVE.**

Confirmed gateway services seen in public bundles: `/service/authentication`,
`/service/account/password/reset`, `/service/basket/`, `/service/wishlist`,
`/service/product-info/graphql`, `/service/header-footer/v1/…`, `/service/content-entry`,
`/service/lastviewed`, `/service/recommender-fetch`, `/service/jsbucket/v1/exception/create`.
None of these is the order service. I deliberately did **not** brute-force guesses like
`/service/orders` — that is exactly the behaviour Cloudflare Bot Management is watching for,
and one devtools session tomorrow gives you the answer for free.

---

## Greetz — greetz.nl

| field | |
| --- | --- |
| Retailer / domain | Greetz — `www.greetz.nl` (**Moonpig Group** platform — see below) |
| Tier | **`browser_once`** |
| Auth | `password` — and also e-mail OTP code, magic link, and Google/Apple OIDC |
| Evidence | No repo — none found. First-hand read of Next.js chunks under `https://www.greetz.nl/static/purchase/cce12ae25e879600405232bea3b581fd89659cd5/_next/static/chunks/`, fetched 2026-07-28 |
| Confidence | **CONFIRMED-from-source** for every auth endpoint and both base URLs. **UNVERIFIED** for the orders GraphQL operation |
| Base URL | `AUTH_URL = https://www.greetz.nl/auth` and `API_URL = https://api.greetz.nl` — **both read verbatim out of the live page config**, not guessed |
| Order-history endpoint(s) | **NOT DETERMINED.** `https://www.greetz.nl/nl/account/orders/` `302`s to `/nl/account/login/?returnUrl=…`; the account micro-frontend is never served anonymously. The data layer is Apollo Client against `/graphql` on `api.greetz.nl` |
| Required headers | `Content-Type: application/json`. Session is carried in cookies (below), not a bearer header, on the browser side. `x-mnpg-auth-key` exists but is a **server-to-server** secret (`AUTH_SECRET_HEADER`) — a browser client never sends it |
| Money format | **UNVERIFIED** |
| Bot protection | **Cloudflare.** Ordinary `GET`s of pages passed cleanly for `curl`, but `POST https://www.greetz.nl/auth/token` returned a **Cloudflare Managed Challenge** ("Just a moment…", `challenges.cloudflare.com` CSP). So `/auth/*` specifically is defended |
| Feasibility | **MEDIUM** — clean, modern, well-shaped auth API with no captcha in the code path, sitting behind a Cloudflare challenge you must clear in a browser first |

### Exact source

Config, read out of `https://www.greetz.nl/nl/account/login/`:

```json
"API_URL":"https://api.greetz.nl"
"AUTH_URL":"https://www.greetz.nl/auth"
```

Auth client, from chunk `3329-64d50a01a9122dd4.js`:

```js
login: async n => {
  let { email: o, password: r, flowId: s } = n;
  let n2 = await e({ path: "/login",
                     body: JSON.stringify({ email: o, password: r, region: t, flowId: s }) });
  if (n2.ok) return { success: true };
  // 400/401 -> errorMessage ; 403 -> accountLocked unless errorCode === IDENTITY_INACTIVE
}
```

Other confirmed paths on the same client (all `POST`, all relative to `AUTH_URL`):

| path | body |
| --- | --- |
| `/login` | `{email, password, region, flowId}` |
| `/account-lookup` | `{email}` → `200` with `{state, password}` \| `404` if no account |
| `/code/generate` | `{email, action, flowId}` → `{flowId}` |
| `/code/login` | `{flowId, email, code}` |
| `/code/register` | `{flowId, email, code, firstName, lastName, source}` |
| `/magic/login` | `{token}` |
| `/validate-email` | `{email}` |
| `/oidc/providers` | `GET` |
| `/oidc/start` | provider payload |
| `/oidc/login/code` | `{upstreamParameters}` |
| `/token?ce=<cookiesEnabled>` | returns `{accessToken, accessTokenExpiresIn, refreshToken, refreshTokenExpiresIn, isLoggedIn}` |

`/code/login` is an **e-mail OTP**. That is a second, captcha-free route into the account
that needs no password — but it needs mailbox access, so it is not automatable tonight.

### Session cookies (observed anonymously on a first request)

```
mnpg_access_token   = <opaque 32 chars>          Max-Age=10800   HttpOnly Secure SameSite=Lax
mnpg_refresh_token  = greetz-prod:<opaque>       Max-Age=15780000 (≈182 days)
mnpg_session_id     = <uuid>                     Max-Age=2592000
mnpg_is_authenticated = false
mnpg_web_uid        = <hex>                      Max-Age=31536000
```

A 3-hour access token with a **182-day refresh token** is a very good connector shape.

### `mnpg` = Moonpig — Greetz runs the Moonpig platform

The static assets are served from `static.web-explore.prod.moonpig.net`, every cookie is
prefixed `mnpg_`, and the refresh token is namespaced `greetz-prod:`. This is CONFIRMED.
Practical consequence: whatever you build for Greetz is very likely to port to
**moonpig.com** by changing the host and the `region` field in the login body — the `region`
parameter in the login call is the tell. **UNVERIFIED for moonpig.com specifically**, but
worth ten minutes once Greetz works.

---

## Van Dijk Educatie — leerlingen.vandijk.nl

| field | |
| --- | --- |
| Retailer / domain | VanDijk (Van Dijk Educatie) — webshop is `leerlingen.vandijk.nl`. `www.vandijk.nl` is only a WordPress/Elementor marketing site — **do not point the connector at it** |
| Tier | **`browser_once`** most likely; possibly `http` if the B2C policy allows ROPC |
| Auth | **`oauth+pkce` via Azure AD B2C** |
| Evidence | No repo — none found. Confirmed from the live `Content-Security-Policy-Report-Only` header on `leerlingen.vandijk.nl`, 2026-07-28 |
| Confidence | **CONFIRMED** that the identity provider is Azure AD B2C. **UNVERIFIED** for tenant name, policy (`p=B2C_1…`) and `client_id` — I could not reach the login screen anonymously |
| Base URL | `https://leerlingen.vandijk.nl` (ASP.NET MVC on Azure; `Request-Context: appId=cid-v1:0d372b3a-6fd1-4e04-b1df-c53a80ee5a73`) |
| Order-history endpoint(s) | Pages: `/account/bestellingen` (orders) and `/account/facturen` (invoices), plus `/account/mijn-gegevens`. Both `302` to `/` when anonymous. Login entry point is `/boekenlijst/inloggen?returnUrl=%2Faccount`, which also `302`s to `/` without a school/booklist context |
| Required headers | Session cookie `ShoppingBasket=<opaque>` (`Secure; HttpOnly; SameSite=None`) is issued on first request. Post-auth cookie set unknown |
| Money format | **UNVERIFIED** |
| Bot protection | **None seen.** No CDN challenge, no reCAPTCHA/hCaptcha/Turnstile anywhere in the markup or bundles. Only Cookiebot (consent, not defence) |
| Feasibility | **MEDIUM** — Azure AD B2C is a well-trodden, thoroughly documented path and there is no bot defence at all, but the login flow starts from a school/booklist selection that I could not reach anonymously |

### The evidence, verbatim

From the live response headers of `GET https://leerlingen.vandijk.nl/account/bestellingen`:

```
Content-Security-Policy-Report-Only:
  connect-src 'self' *.b2clogin.com *.vandijk.nl *.studieshop.be
              *.google-analytics.com *.google.com *.cookiebot.com
              *.monitor.azure.com *.applicationinsights.azure.com
              *.services.visualstudio.com *.azure-api.net *.cookiebot.eu;
  frame-src   'self' *.googletagmanager.com *.cookiebot.eu *.cookiebot.com
              *.google-analytics.com *.powerbi.com vandijk.eziner.nl
              *.b2clogin.com *.google.com login.microsoftonline.com;
```

`*.b2clogin.com` + `login.microsoftonline.com` in `frame-src` **and** `connect-src` is an
unambiguous Azure AD B2C signature. `*.azure-api.net` means there is an Azure API Management
gateway behind it — that is where the order JSON will come from.

Note `*.studieshop.be` in the same CSP: the Belgian sibling **StudieShop** shares this
platform, so one connector covers both. CONFIRMED from the header; the exact Belgian host
is UNVERIFIED.

### What to do first

Azure B2C flows are boringly scriptable *when the policy permits it*: the sign-in page posts
to `/{tenant}/{policy}/SelfAsserted/confirmed` with an `x-csrf-token` header taken from the
`x-ms-cpim-csrf` cookie, and there is no captcha by default. Some tenants also enable the
**ROPC** policy (`/oauth2/v2.0/token` with `grant_type=password`), which would make this pure
`http`. Both are worth ten minutes of testing. First job tomorrow: open the login page with
a session and capture the `b2clogin.com` URL — tenant, policy and `client_id` all fall out of
that one URL.

---

## Belsimpel — belsimpel.nl

| field | |
| --- | --- |
| Retailer / domain | Belsimpel — `www.belsimpel.nl` (trades internationally as **Gomibo**: `gomibo.nl/.de/.be/.fr`) |
| Tier | `http` (probable) |
| Auth | **`password+otp`** — the front-end ships a two-factor component |
| Evidence | No repo — none found. First-hand read of `https://www.belsimpel.nl/assets/react/accountcontrols.js` and `/assets/react/app.js`, fetched 2026-07-28 |
| Confidence | **CONFIRMED-from-source** that a 2FA step exists and that there is no captcha or CDN defence. **UNVERIFIED** for the login endpoint and the orders endpoint |
| Base URL | `https://www.belsimpel.nl` |
| Order-history endpoint(s) | **NOT DETERMINED.** The account SPA route names are confirmed: `accountControlsLogin`, `accountControlsLoginOrderstatus`, `accountControlsTwoFactorAuthentication`, `accountControlsPasswordReset(Request)`, `accountControlsRegistration`, `accountControlsActivationEmailSent`. The only `/API/` path in the public bundles is `/API/vergelijk/Exchange` |
| Required headers | Plain session cookies: `PHPSESSID`, `ABST`, `ab_store`. Server is bare `nginx`, no CDN |
| Money format | **UNVERIFIED** |
| Bot protection | **None seen** — zero occurrences of `recaptcha`/`captcha`/`turnstile`/`hcaptcha`/`datadome` in the login page HTML or the account bundle; no CDN challenge headers |
| Feasibility | **MEDIUM** technically, but see the value warning |

`robots.txt` is worth quoting because it hands you the public API surface for free:

```
Allow: /API/vergelijk/*
Disallow: /API/exposure/*
Disallow: /orderstatus?from=*
Disallow: /inloggen?from=*
```

There is also a **guest order-status flow** at `https://www.belsimpel.nl/orderstatus` —
"check your order without an account", log in with an order number. That is per-order, not
history, so it does not serve the platform, but it is a useful fallback and it needs no
account.

**Value warning, stated plainly:** Belsimpel sells phones and subscriptions. A typical
household transacts there once every two or three years. Even a perfect connector adds
roughly one row per user per 30 months. The 2FA step alone will cost more engineering than
that data is worth. Build it last or not at all.

---

## Apple — reportaproblem.apple.com / secure.store.apple.com

| field | |
| --- | --- |
| Retailer / domain | Apple — **two separate systems**: `reportaproblem.apple.com` (App Store, iTunes, subscriptions, iCloud) and `secure.store.apple.com/nl/shop/order/list` (hardware) |
| Tier | **`browser_interactive`** |
| Auth | Apple ID via `idmsa.apple.com/IDMSWebAuth` — **`password+otp` with mandatory two-factor** |
| Evidence | [`breus-labs/apple-invoice-downloader`](https://github.com/breus-labs/apple-invoice-downloader) — last commit **2025-12-12**, 5 stars. [`lhylhy2024/AppleGrabber`](https://github.com/lhylhy2024/AppleGrabber) — last commit **2025-12-19**. **I read both sources, not just the READMEs.** |
| Confidence | **CONFIRMED-from-source** |
| Base URL | `https://reportaproblem.apple.com/` — redirects anonymously to `https://idmsa.apple.com/IDMSWebAuth/signin?appIdKey=20379f32034f8867d352666ff2904d2152d5ff6843ee2db5ab5df863c14b1aef&path=%2F__logged_in%2Freportaproblem.apple.com` (verified live) |
| Order-history endpoint(s) | **There is no API. Neither project found one.** Both scrape the DOM. Selectors, verbatim from `apple-invoice-downloader-v2.js`: `button[data-auto-test-id="RAP2.PurchaseList.PurchaseHeader.Button.ToggleDisclosure"]`, `button[data-auto-test-id="RAP2.PurchaseList.PurchaseDetails.Button.ViewReceipt"]`, `select[data-auto-test-id="RAP2.FilterPurchases.Select.FamilyMember"]`. The list is **infinite-scroll** |
| Required headers | n/a — cookie jar only. Apple sets `dslang`, `dawsp` (`Domain=idmsa.apple.com`), `dq-auth-retry`; `Server: daiquiri/5` |
| Money format | **String scraped from DOM text**, locale-formatted (`22.99` + separate `EUR`). Both projects reassemble it into a filename. No structured amount is available |
| Bot protection | No captcha observed, but Apple IDMS does heavy device fingerprinting and **enforces 2FA on every untrusted session** |
| Feasibility | **HARD** |

### The honest answer to "is an Apple ID order history reachable at all?"

**Not tonight, and not without the account owner physically present the first time.**

1. **There is no Apple order API of any kind** — no partner, no affiliate, no customer API.
   The best-resourced public attempts (two independent projects, both from December 2025)
   both concluded that browser automation of the DOM is the only route. That is a strong
   negative result, not an absence of evidence.
2. **Apple 2FA cannot be bypassed.** It is not a captcha and not a risk score; it is a
   six-digit code pushed to a trusted device. No amount of header-crafting avoids it. The
   working project runs `chromium.launch({ headless: false })` and its README says, in as
   many words, step 2 is *"Du loggst dich manuell bei Apple ein (inkl. 2FA)"*.
3. **The mitigation is session persistence, and it genuinely works.** The project saves
   Playwright `storageState` to `apple-session.json` and reuses it indefinitely ("Die Session
   ist gespeichert — kein Login mehr nötig"). So the cost is *one* interactive login per user,
   then long unattended runs until Apple expires the trust token. That is a real product
   pattern — it is just not one you can prototype while the owner is asleep.
4. **You would be building two connectors, not one.** `reportaproblem.apple.com` gives you
   App Store / subscriptions / iCloud. Hardware purchases live at
   `secure.store.apple.com/nl/shop/order/list` behind a *different* sign-in
   (`/nl/shop/signIn/orders`, verified live). Most users' Apple euros are in the second one.
5. **Line items are weak.** Even on success you get a product name, a date and a
   locale-formatted amount scraped from text, plus a PDF. No SKUs, no structured totals, no
   tax breakdown.

My recommendation: **do not attempt Apple for the MVP.** If you later decide the
subscription data is worth it, budget it as its own project with an explicit interactive
onboarding step, and set expectations that it is DOM-fragile — Apple renames `data-auto-test-id`
attributes without notice and every rename silently breaks ingestion.

---

## Decathlon — decathlon.nl

| field | |
| --- | --- |
| Retailer / domain | Decathlon — `www.decathlon.nl` |
| Tier | **`browser_interactive`** (and only if a real browser clears the challenge) |
| Auth | unknown — I could not reach a login page at all |
| Confidence | **CONFIRMED** for the bot protection. **SPECULATIVE** for everything else — I have no evidence about Decathlon's order API and will not invent any |
| Evidence | **None found.** Decathlon's own GitHub org publishes only sports/vision APIs; [`Decathlon/open-apis-documentations`](https://github.com/Decathlon/open-apis-documentations) last pushed **2022-05-20** and contains exactly three products: `decathlon-login`, `sport-activities`, `sport-vision` |
| Base URL | `https://www.decathlon.nl` |
| Order-history endpoint(s) | unknown. `robots.txt` disallows `/api/*`, `/*/account/`, `/*/login`, `/checkout/` and — telling — `/_Incapsula_Resource*`, a fossil from an earlier Imperva deployment |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | **Cloudflare Managed Challenge, site-wide.** Verified: `GET https://www.decathlon.nl/` → `HTTP 403`, `cf-mitigated: challenge`, `challenges.cloudflare.com` CSP, plus an `accept-ch` list demanding a dozen `Sec-CH-UA-*` client hints. `https://api.decathlon.net/` → Cloudflare `403 Attention Required` firewall block |
| Feasibility | **HARD** |

### Read this before anyone suggests "just try it"

Decathlon challenges **the homepage** to a plain HTTP client. Not the login page — the
homepage. That is the most aggressive posture of any retailer in this batch, and it is a
deliberate, tuned configuration (they are also demanding high-entropy client hints, which is
how they fingerprint headless browsers).

There is also, as far as I can determine, **no public reverse-engineering work on Decathlon's
consumer order API anywhere.** Zero repos, zero blog posts, zero endpoint captures.

One clarification that matters, because it is an easy and expensive mistake: web searches
surface a Decathlon "Orders API" and "Customers API". Those belong to the **Decathlon
Marketplace seller programme** — they let a third-party *merchant* manage orders placed
against *their own* listings. They do not expose a shopper's own purchase history and they
are not accessible to a consumer's credentials. **UNVERIFIED** in the sense that I could not
open the portal (`developers.decathlon.com` did not resolve from here), but the distinction
is important enough that I am flagging it rather than letting the search snippet stand.

Decathlon is high-value — people genuinely shop there several times a year. It is also the
single worst effort-to-outcome ratio in this batch. That combination is exactly the trap
worth naming out loud.

---

## Recommended build order

Ranked by **value × feasibility**, with the reasoning stated so you can disagree with it.

| # | Retailer | Value | Feasibility | Why this rank |
| --- | --- | --- | --- | --- |
| 1 | **Bever** (+ Yonderland siblings) | Medium | EASY–MEDIUM | Best leverage in the batch. Confirmed password login with a hardcoded client credential, no captcha, `/api/*` returns a clean `401` to plain HTTP. And the *same* connector should cover A.S.Adventure, Cotswold, Snow+Rock, Runners Need and Juttu — I verified Cotswold uses an identical auth redirect. One night's work, up to six shops. Only open question is the order-*list* path. |
| 2 | **V&D / vd.nl** | Low | EASY | Low value on its own, but it is a *free* build: standard Magento 2 GraphQL, schema verified live, money format confirmed, no captcha, no browser. Do it because you end up owning a reusable `MagentoGraphQlConnector` that snaps onto every Adobe Commerce shop in NL. Test the Okta caveat first. |
| 3 | **Wehkamp** | **High** | MEDIUM | The most valuable retailer here — genuine repeat purchasing across a broad catalogue. Login is a single confirmed JSON POST, the session cookie lasts **200 days**, and Turnstile is risk-based rather than mandatory. Costs a browser and one endpoint-discovery session. Worth it. |
| 4 | **Greetz** | Medium | MEDIUM | Clean, modern auth API with every endpoint confirmed, a 182-day refresh token, and no captcha in the code path — but `/auth/*` sits behind a Cloudflare challenge, so it is `browser_once`. Bonus: probably ports to moonpig.com. |
| 5 | **Van Dijk Educatie** | Low–Medium | MEDIUM | Seasonal (schoolbooks, once a year) but chunky amounts, and parents care about exactly this kind of expense. Azure AD B2C is thoroughly documented and there is **no bot protection at all**. Could collapse to `http` if ROPC is enabled. Cheap to find out. |
| 6 | **Belsimpel** | **Low** | MEDIUM | Technically the softest target in the batch — bare nginx, PHP sessions, no captcha, no CDN. But users buy a phone every two or three years. 2FA makes it more work than the data justifies. |
| 7 | **Decathlon** | High | **HARD** | See below. |
| 8 | **Apple** | Medium | **HARD** | See below. |

---

## Do not attempt

### Decathlon — do not attempt tonight, and think hard before attempting at all

Cloudflare Managed Challenge on **every URL including `/`**, confirmed by
`cf-mitigated: challenge` on the homepage, plus an `accept-ch` client-hint list built to
catch headless browsers. `api.decathlon.net` is behind a hard Cloudflare firewall block.
There is **no public reverse-engineering work in existence** to build on. You would be
starting from absolute zero against a tuned anti-automation stack, at night, with nobody
awake to clear a challenge. This is precisely the "waste of a night" case. Revisit only if
you decide to invest in a properly fingerprinted browser stack — and treat it as a project,
not a task.

### Apple — do not attempt for the MVP

There is no API. The two credible public projects both drive a **visible** browser and scrape
the DOM, and both require a **manual interactive 2FA login**. That is unachievable while the
owner is asleep, by definition. The workable pattern (one interactive login, then persisted
`storageState`) is real but needs product design around consented onboarding, and it is two
connectors, not one, because hardware orders live on a different host behind a different
sign-in. The payload is also weak — DOM-scraped strings and PDFs, no structured line items.

### Guessing endpoints — do not

For **Wehkamp**, **Bever** and **Greetz** I deliberately did not brute-force candidate paths
(`/service/orders`, `api/customer/orders`, …). Sequential 404-probing against Cloudflare Bot
Management and AWS WAF is the exact signature those systems exist to catch, and burning
reputation on this project's IP for a guess would be a bad trade. Each of those three
endpoints is a five-minute discovery with one real logged-in session and devtools open.
**Do that first thing tomorrow; do not ship code written against a guess.**

---

## Loose ends worth someone's morning

* **Bever order-list path** — 5 min with a session. Unblocks rank #1.
* **Wehkamp order service name** under `/service/<name>` — 5 min with a session. Unblocks rank #3.
* **Wehkamp bot score as an oracle** — read `window.dataLayer[1].bot_score` from your Playwright context *before* attempting login. Tells you `browser_once` vs `browser_interactive` for free, and is a useful canary for the whole platform.
* **Greetz orders GraphQL operation** on `api.greetz.nl/graphql` — 5 min with a session.
* **V&D Okta caveat** — does `generateCustomerToken` accept a real customer, or are all accounts Okta-provisioned? Decides EASY vs dead.
* **Van Dijk B2C parameters** — capture the `b2clogin.com` URL once; tenant + policy + `client_id` all fall out of it. Then test whether ROPC is enabled.
* **Yonderland siblings** — confirm A.S.Adventure / Snow+Rock / Runners Need / Juttu use the same `shopId`/`market`/`mainWebshop` pattern. Highest-leverage follow-up in this document.

## One environment note, so nobody loses an hour to it

`curl` on this machine cannot complete a TLS handshake to `*.vandijk.nl`:
`SSL certificate problem: self signed certificate in certificate chain`. This is **local** —
Git-for-Windows' bundled CA store versus something intercepting TLS on this box — not a
Van Dijk block. PowerShell's `Invoke-WebRequest`, which uses the Windows certificate store,
connects fine. Use PowerShell (or fix the CA bundle) for anything on that host.
