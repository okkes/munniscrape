# Platform sweep — one adapter, many retailers

Research date: **2026-07-28**. Researcher: platform sweep (`retailers-platforms`).
Nothing in this document was obtained by logging in. Every live capture below is
an **unauthenticated** GET/POST against a public endpoint, reproducible with the
commands in §10. Every source-code claim was read from the raw file, not the README.

---

## 0 · The headline, before anything else

Three things came out of this sweep that change what should be built first.

**(1) The "platform adapter" idea is real for Magento and Shopware, and half-real
for Shopify.** Magento's `customer { orders }` and Shopware's `/store-api/order`
are *core* — every shop running the platform has them, at the same path, with the
same schema. One adapter, N retailers. Confirmed from platform source, and
confirmed live against a Dutch shop for Magento.

**(2) Shopify has no global client id — but the per-shop client id is publicly
discoverable in two unauthenticated HTTP requests.** This is the single most
useful thing I found. See §1. It converts Shopify from "needs merchant
cooperation, therefore impossible" into "needs a discovery step, therefore a
normal T2/T3 adapter".

**(3) The order-confirmation e-mail is not a scraping fallback. It is a
capability-token carrier.** On **four** of the major platforms the confirmation
mail contains a token that unlocks a *structured, unauthenticated, machine-readable
order fetch* — no login, no password, no captcha, no browser:

| Platform | What the mail carries | What it unlocks | Status |
| --- | --- | --- | --- |
| WooCommerce | `order-received/{id}/?key=wc_order_xxx` | `GET /wp-json/wc/store/v1/order/{id}?key=…&billing_email=…` | **CONFIRMED-from-source** |
| Magento | order number + billing e-mail + surname | `guestOrder(input:{number,email,lastname})` GraphQL | **CONFIRMED-from-source** |
| Magento 2.4.6+ | order `token` in the status link | `guestOrderByToken(input:{token})` GraphQL | **CONFIRMED-from-source** (schema); mail contents UNVERIFIED |
| Shopware 6 | `/account/order/{deepLinkCode}` | `POST /store-api/order` with a `deepLinkCode` equals-filter + guest auth | **CONFIRMED-from-source** |
| Shopify | `order_status_url` | order status page (HTML, browser) | UNVERIFIED |

That is the cheapest, least-defended, most durable path to line-item data that
exists anywhere in this project. It should be prioritised over almost everything
else. See §8.

---

## 1 · Shopify — Customer Account API

| field | |
| --- | --- |
| Retailer / domain | **Shopify** (platform) — any `*.myshopify.com` and its custom domain |
| Tier | `browser_once` (T2) — realistically; see "the client_id problem" below |
| Auth | **oauth+pkce** (S256), passwordless e-mail OTP at the login step |
| Evidence | Live unauthenticated capture 2026-07-28 against `allbirds.com` (shop_id `11044168`), `dailypaperclothing.com` (`6171881`), `intersport.nl` (`58562052201`). Vendor docs: `shopify.dev/docs/api/customer`. No third-party reverse-engineering project exists — searched GitHub, none found. |
| Confidence | **CONFIRMED-from-source** for discovery, endpoints, client-id discoverability, header name. **UNVERIFIED** for token lifetime and refresh-token persistence (cannot test without logging in). |
| Base URL | Discovered per shop. Two forms seen live: `https://accounts.<shop-domain>/customer/api/2026-07/graphql` (custom accounts subdomain) and `https://shopify.com/<shop_id>/account/customer/api/2026-07/graphql` (default). **Never hardcode — read it from discovery.** |
| Order-history endpoint(s) | GraphQL, one endpoint. Operation: `query { customer { orders(first: 50) { nodes { id name number processedAt createdAt financialStatus totalPrice { amount currencyCode } lineItems(first: 100) { nodes { title quantity } } } } } }`. Field names `name`, `number`, `processedAt`, `createdAt`, `totalPrice`, `lineItems`, `financialStatus` confirmed from the Customer API `Order` object docs. |
| Required headers | `Authorization: <access_token>` — **no `Bearer` prefix** in Shopify's Customer Account API; `Content-Type: application/json`; `Origin: https://<storefront>` and a real `User-Agent` are required for public clients. Confirmed live: omitting Authorization returns HTTP 401 `{"errors":[{"message":"Missing Authorization Header"}]}`. |
| Money format | `MoneyV2 { amount, currencyCode }`. `amount` is the `Decimal` scalar → **a decimal string**, e.g. `"49.95"`, not cents. Order total = `totalPrice.amount`. |
| Bot protection | Cloudflare in front of `shopify.com` and `accounts.*` (server: cloudflare, cf-ray). No captcha observed on the discovery or API endpoints. The **login** step is passwordless e-mail OTP, which is a human-in-the-loop step, not a captcha. |
| Feasibility | **MEDIUM** — the API itself is clean OAuth2+PKCE with `refresh_token` in `grant_types_supported`, but no third party can register a redirect URI, so login has to run through a browser against the shop's own first-party client. |

### 1.1 · Discovery — confirmed live, both files

Two well-known documents on the **storefront** domain, both public:

```
GET https://<storefront>/.well-known/openid-configuration
GET https://<storefront>/.well-known/customer-account-api
```

Live response (intersport.nl, 2026-07-28), trimmed:

```json
{"issuer":"https://shopify.com/authentication/58562052201",
 "token_endpoint":"https://shopify.com/authentication/58562052201/oauth/token",
 "authorization_endpoint":"https://shopify.com/authentication/58562052201/oauth/authorize",
 "end_session_endpoint":"https://shopify.com/authentication/58562052201/logout",
 "jwks_uri":"https://shopify.com/authentication/58562052201/.well-known/jwks.json",
 "scopes_supported":["openid","email","customer-account-api:full","customer-account-mcp-api:full"],
 "response_types_supported":["code"],
 "code_challenge_methods_supported":["S256"],
 "grant_types_supported":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:jwt-bearer"]}
```

```json
{"graphql_api":"https://shopify.com/58562052201/account/customer/api/2026-07/graphql",
 "mcp_api":"https://shopify.com/58562052201/account/customer/api/mcp"}
```

`allbirds.com` and `dailypaperclothing.com` return the same shape but with the
merchant's own accounts subdomain (`accounts.allbirds.com`,
`account.dailypaperclothing.com`) as the host. **The shop_id is in `issuer`.**

`refresh_token` is in `grant_types_supported` on all three shops. That is what
makes this a plausible T2 (`browser_once`) provider rather than T3.

### 1.2 · The client_id problem — and the way through it

Shopify's docs say the `client_id` comes from the merchant's Customer Account
API / Headless channel settings. Read literally, that kills a consumer
aggregator: you cannot ask 50 merchants to register your redirect URI.

**But the shop's own first-party client id is public.** Requesting
`https://shopify.com/<shop_id>/account` unauthenticated returns a redirect chain
that leaks it in the query string. Captured live:

| shop | shop_id | first-party `client_id` |
| --- | --- | --- |
| allbirds.com | `11044168` | `5a1f8862-a609-4491-860e-4d61f9fb4117` |
| intersport.nl | `58562052201` | `830b7743-2ffb-4f4a-919e-a8744eb77316` |

They differ → **the client id is per-shop, not global.** That is the answer to
the question in the brief, and it is a fact, not a guess. But it is a *discoverable*
per-shop value, obtained with zero authentication in one request. A universal
Shopify adapter is therefore: `storefront → shop_id → client_id → OAuth`.

The registered `redirect_uri` for that client is the shop's own callback
(`https://accounts.allbirds.com/callback`, live capture). We cannot substitute
ours. So the flow must be driven in a browser that lands on the merchant's
callback, after which the connector harvests the session/token the hosted account
UI holds. **Whether that token can be persisted and refreshed headlessly
afterwards is UNVERIFIED** — it is the one question that decides T2 vs T3, and it
cannot be answered without a real login. Treat it as the first experiment when
someone with an account is awake.

The authorize call the hosted UI makes, captured live:

```
https://shopify.com/authentication/58562052201/oauth/authorize
  ?client_id=830b7743-2ffb-4f4a-919e-a8744eb77316
  &locale=nl-NL
  &nonce=<uuid>
  &redirect_uri=https%3A%2F%2F…%2Fcallback
  &response_type=code
  &scope=openid+email+customer-account-api%3Afull
  &state=<opaque>
```

### 1.3 · Classic customer accounts are gone

Shopify **deprecated legacy customer accounts in February 2026**. The old
`POST /account/login` (email+password) + Liquid `/account/orders` path is not
something to build on. New accounts are passwordless: a 6-digit code e-mailed on
each login, sessions then live up to 365 days. UNVERIFIED (vendor blogs and
partner write-ups, not primary source), but consistent with everything captured
live — every shop probed redirected into the OAuth/OTP flow, none offered a
password form.

**Note the synergy:** the login OTP arrives *by e-mail*. A connector that already
has IMAP access to the user's mailbox (§8) can complete the Shopify OTP without
asking the human anything. That is a genuine architectural argument for building
the e-mail connector first.

### 1.4 · The Customer MCP endpoint — unauthenticated `tools/list`

`https://shopify.com/<shop_id>/account/customer/api/mcp` answers
`{"jsonrpc":"2.0","method":"tools/list"}` **without any authentication** (HTTP 200,
captured live). Tools exposed:

- `get_most_recent_order_status`
- `get_order_status` (input: `order_number`)
- `get_store_credit_balances`
- `request_return` ← **a write. Never call it.**

Only the listing is unauthenticated; invoking them needs the same customer token.
It is not a shortcut around auth, but it is a useful liveness/capability probe and
it confirms the endpoint exists on shops that have never heard of headless.

---

## 2 · Shopify — the Shop app / "Login with Shop"

| field | |
| --- | --- |
| Retailer / domain | **shop.app** (Shopify's consumer app) |
| Tier | `browser_interactive` (T3) at best |
| Auth | phone/e-mail + OTP, federated into every Shopify merchant |
| Evidence | Live capture of the federation bounce in the allbirds account flow: `https://shop.app/accounts/bounce?client_id=fac9ad3e-1e23-4487-b7cd-7691a6013040&redirect_uri=…/services/login_with_shop/buyer/complete…`. **No public reverse-engineering project found** — GitHub searched for `shop.app`, shop-app order history, Shopify consumer API; only merchant-side tooling exists. |
| Confidence | CONFIRMED that the federation exists and its client id is `fac9ad3e-1e23-4487-b7cd-7691a6013040`. **SPECULATIVE** on everything about its order API. |
| Base URL | `https://shop.app` (server-rendered SPA), assets on `shopify-assets.shopifycdn.com/shopifycloud/shop-client/` |
| Order-history endpoint(s) | **Unknown.** `GET https://shop.app/orders` returned HTTP **429** on an anonymous request — rate-limited before it even redirected. |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | aggressive anonymous rate limiting (429 on first hit), Cloudflare |
| Feasibility | **HARD** — but the prize is the largest in this entire document. |

**Why this matters more than any single retailer.** The Shop app shows a consumer
*every order they have ever placed on any Shopify store*, in one list. That is
cross-retailer purchase history from a single authentication — exactly what this
platform exists to produce. And the federation is real: the allbirds login flow
*bounced through shop.app* before landing on the merchant's authorize endpoint,
meaning a live Shop session logs you into merchant accounts without an OTP.

**Why it is not tonight's job.** Zero public prior art, an SPA whose API is only
discoverable by watching an authenticated session, and a 429 on the first
anonymous request. This is a "spend a day with a real account and a proxy" task,
not a "read a repo and write an adapter" task. Log it as the highest-value
unexplored lead in the project and move on.

---

## 3 · Magento / Adobe Commerce — the genuine multi-retailer win

| field | |
| --- | --- |
| Retailer / domain | **Magento Open Source / Adobe Commerce 2.3+** (platform) |
| Tier | `http` (T1) where reCAPTCHA is off · `browser_once`/`browser_interactive` where it is on — **per shop**, see §3.3 |
| Auth | **password** (`generateCustomerToken(email, password)`) → 1-hour bearer token |
| Evidence | `magento/magento2` @ `2.4-develop`, read raw: [`app/code/Magento/SalesGraphQl/etc/schema.graphqls`](https://github.com/magento/magento2/blob/2.4-develop/app/code/Magento/SalesGraphQl/etc/schema.graphqls) and [`app/code/Magento/CustomerGraphQl/etc/schema.graphqls`](https://github.com/magento/magento2/blob/2.4-develop/app/code/Magento/CustomerGraphQl/etc/schema.graphqls). Branch is actively maintained (2.4-develop, current). Plus a **live unauthenticated capture against `www.dille-kamille.nl` on 2026-07-28**. |
| Confidence | **CONFIRMED-from-source** — schema, mutation, guest paths, header names. |
| Base URL | `https://<shop-domain>/graphql` — a single endpoint, always this path, core module, on by default |
| Order-history endpoint(s) | `POST /graphql`, operation: `query { customer { orders(pageSize: 50, currentPage: 1) { total_count items { number order_date status total { grand_total { value currency } } items { product_name product_sku quantity_ordered product_sale_price { value currency } } } } } }`. Also `query { customerOrders }` (deprecated). Guest: `guestOrder(input:{number,email,lastname})`, `guestOrderByToken(input:{token})`. Auth: `mutation { generateCustomerToken(email:"…", password:"…") { token } }`. |
| Required headers | `Content-Type: application/json` (mandatory); `Authorization: Bearer <customer_token>`; `Store: <store_view_code>` (optional, e.g. `default` / `nl`); `Content-Currency: EUR` (only if not the store default); `X-ReCaptcha: <google token>` **if the shop has reCAPTCHA on the login form**; `X-Captcha` for Magento's built-in CAPTCHA. |
| Money format | **decimal float**, in `Money { value: Float, currency: CurrencyEnum }`. Order total = `total.grand_total.value` (`Money!`). Line total = `items[].product_sale_price.value` (`Money!`). Legacy `grand_total: Float` on `CustomerOrder` is deprecated — do not use it. |
| Bot protection | **None on `/graphql` itself.** Optional Google reCAPTCHA on the login mutation, per shop. Some shops sit behind Cloudflare/Akamai at the CDN. |
| Feasibility | **EASY** where reCAPTCHA is off, **MEDIUM** where it is on. Best value-per-hour in this entire sweep. |

### 3.1 · Confirmed schema (read from source, not docs)

`SalesGraphQl/etc/schema.graphqls`, lines 25–33 — this is core Magento, in every install:

```graphql
type Customer {
    orders (
        filter: CustomerOrdersFilterInput,
        currentPage: Int = 1,
        pageSize: Int = 20,
        sort: CustomerOrderSortInput,
        scope: ScopeTypeEnum
    ): CustomerOrders @resolver(class: "Magento\\SalesGraphQl\\Model\\Resolver\\CustomerOrders") @cache(cacheable: false)
}
```

`CustomerOrder` fields (confirmed): `id, order_date, status, number, items,
total, payment_methods, shipping_method, token, email, credit_memos, invoices,
shipments, returns` — plus deprecated `order_number`, `created_at`, `grand_total`,
`increment_id`.

`OrderItemInterface` (confirmed): `product_name, product_sku, product_url_key,
product_type, status, product_sale_price: Money!, discounts, selected_options,
quantity_ordered, quantity_shipped, quantity_refunded, quantity_invoiced`.

`OrderTotal` (confirmed): `grand_total: Money!, base_grand_total: Money!,
subtotal_incl_tax, subtotal_excl_tax, grand_total_excl_tax, total_tax, taxes,
total_shipping, discounts`.

`CustomerGraphQl/etc/schema.graphqls` line 21 (confirmed):

```graphql
generateCustomerToken(email: String!, password: String!): CustomerToken
type CustomerToken { token: String }
```

Token lifetime is **1 hour** by default (Adobe docs, admin-configurable). There is
**no refresh token** — this is a re-login-every-hour design, or rather
re-login-per-fetch-run. That is fine for a nightly job and a disaster for
anything chatty. It also means the password must be retained, which is a policy
question for the platform, not a technical one.

### 3.2 · The guest path — no login at all

Also core, also confirmed from the same file (lines 6–7, 309–324):

```graphql
guestOrder(input: GuestOrderInformationInput!): CustomerOrder!
guestOrderByToken(input: OrderTokenInput!): CustomerOrder!

input GuestOrderInformationInput { number: String!  email: String!  lastname: String! }
input OrderTokenInput { token: String! }
```

`number`, `email` and `lastname` are all present in the order confirmation
e-mail. **This returns the full `CustomerOrder` — line items, totals, everything —
with no password, no token, no captcha, no browser.** For any Magento shop, the
e-mail connector plus this query is a complete receipt pipeline. This is the
single most exploitable finding in the sweep.

Caveat, stated honestly: I did not execute a `guestOrder` query against a live
shop, because doing so would mean asserting someone else's order number. The
schema is CONFIRMED; the runtime behaviour and any rate limiting on it are
**UNVERIFIED**.

### 3.3 · Live evidence on a Dutch shop, and why tier is per-shop

`www.dille-kamille.nl` — all unauthenticated, 2026-07-28:

- `GET /graphql?query={__typename}` → `{"data":{"__typename":"Query"}}` — GraphQL is on.
- Introspection is **open**: `{__type(name:"CustomerOrder"){fields{name}}}` returned the full field list above.
- `{customer{orders(pageSize:1){total_count}}}` → `{"errors":[{"message":"De huidige klant is niet geautoriseerd.","extensions":{"category":"graphql-authorization"}}]}` — Dutch-localised, correct auth scoping, endpoint alive.
- `GET /customer/account/login/` HTML contains `Magento_ReCaptchaFrontendUi`, `recaptcha-popup-login` → **reCAPTCHA is enabled on this shop's login** → `generateCustomerToken` will need `X-ReCaptcha` → **T3, browser required.**

`www.chasin.nl` — `GET /graphql?query={__typename}` → `{"data":{"__typename":"Query"}}`,
and its `/customer/account/login/` page contains **no** reCAPTCHA markers →
plausibly **T1, pure HTTP login**. UNVERIFIED without an actual login attempt.

The lesson for the manifest: **for Magento, tier is a per-shop property detected
at connect time, not a per-platform constant.** The detection is a single
unauthenticated GET of `/customer/account/login/` looking for
`Magento_ReCaptcha*`. Build that probe into the adapter.

---

## 4 · Shopware 6 — clean, cheap, and the second-best platform win

| field | |
| --- | --- |
| Retailer / domain | **Shopware 6** (platform) |
| Tier | `http` (T1) — plain e-mail+password over HTTP, no browser, no captcha in core |
| Auth | **password** → `sw-context-token` (opaque session token) |
| Evidence | `shopware/shopware` @ `trunk`, read raw: [`src/Core/Checkout/Order/SalesChannel/OrderRoute.php`](https://github.com/shopware/shopware/blob/trunk/src/Core/Checkout/Order/SalesChannel/OrderRoute.php), [`src/Core/Checkout/Customer/SalesChannel/LoginRoute.php`](https://github.com/shopware/shopware/blob/trunk/src/Core/Checkout/Customer/SalesChannel/LoginRoute.php), [`src/Core/PlatformRequest.php`](https://github.com/shopware/shopware/blob/trunk/src/Core/PlatformRequest.php). Trunk is current (v6.8 feature flags present in the code I read). |
| Confidence | **CONFIRMED-from-source** |
| Base URL | `https://<shop-domain>/store-api` |
| Order-history endpoint(s) | `POST /store-api/account/login` body `{"email":"…","password":"…"}` → `ContextTokenResponse` containing `contextToken`. Then `GET` or `POST /store-api/order` (POST accepts a Criteria body: `{"limit":50,"page":1,"associations":{"lineItems":{},"transactions":{}},"sort":[{"field":"orderDateTime","order":"DESC"}]}`). |
| Required headers | `sw-access-key: <sales channel access key>` (**public** — Shopware embeds it in the storefront JS/`window` config; harvest it once per shop from a public page), `sw-context-token: <token from login>`, `Content-Type: application/json`. Optional: `sw-language-id`, `sw-currency-id`. Constant names confirmed in `PlatformRequest.php` lines 18–21. |
| Money format | **decimal float**. `order.amountTotal` (gross), `order.amountNet`, `order.currencyId`, plus `lineItems[].totalPrice` / `unitPrice`. |
| Bot protection | **None in core.** Core has a `RateLimiter` on `LOGIN_ROUTE` (per e-mail+IP), `LOGIN_USER`, `LOGIN_CLIENT` and `GUEST_LOGIN` — confirmed in `LoginRoute.php` lines 53–59 and `OrderRoute.php`. Pace accordingly; do not retry-storm. |
| Feasibility | **EASY** — the cleanest customer-scoped order API of any platform here. |

Confirmed scoping (`OrderRoute::load`, lines ~93–101): if a customer is in the
context, the criteria gets
`new EqualsFilter('order.orderCustomer.customerId', $context->getCustomerId())`;
if not, and there is no deep-link filter, it throws `customerNotLoggedIn`. So
`/store-api/order` is genuinely and only the signed-in user's own orders.

**Guest / e-mail-token path (confirmed in the same method):** if the criteria
carries an `EqualsFilter` on `order.deepLinkCode`, the route accepts an
unauthenticated caller and validates via `GuestAuthenticator::validate($order, $request)`,
rate-limited under `RateLimiter::GUEST_LOGIN` keyed on `deepLinkCode + client IP`.
`filterOldOrders()` restricts this to orders inside `deepLinkExpireDays`
(**default 30 days**, constructor arg). The deep link code is the `/account/order/{code}`
link in Shopware's order confirmation mail. So: recent orders yes, history no.

**Caveat worth stating:** `/store-api/order` also supports `POST` and is a route
that can *change* payment on an order via sibling routes. Read `GET`/`POST` order
only. Never touch `/store-api/order/payment`.

**NL prevalence: UNVERIFIED and probably modest.** Shopware is dominant in DE and
growing in NL, but none of the ~98 Dutch retail domains I fingerprinted
responded on `/store-api/context`. The adapter is cheap enough that it is worth
building anyway, but do not sell it as covering the Dutch top-50.

---

## 5 · WooCommerce

| field | |
| --- | --- |
| Retailer / domain | **WooCommerce** (WordPress plugin) |
| Tier | `browser_interactive` (T3) for the account path · `http` (T1) for the e-mail-token path |
| Auth | **cookie session** (WordPress `wp-login.php` / `wc_login_form`) — or **no auth at all** with an order key |
| Evidence | `woocommerce/woocommerce` @ `trunk`, read raw: [`plugins/woocommerce/src/StoreApi/Routes/V1/Order.php`](https://github.com/woocommerce/woocommerce/blob/trunk/plugins/woocommerce/src/StoreApi/Routes/V1/Order.php) and [`plugins/woocommerce/src/StoreApi/Utilities/OrderAuthorizationTrait.php`](https://github.com/woocommerce/woocommerce/blob/trunk/plugins/woocommerce/src/StoreApi/Utilities/OrderAuthorizationTrait.php). Current. |
| Confidence | **CONFIRMED-from-source** |
| Base URL | `https://<shop-domain>/wp-json/wc/store/v1` |
| Order-history endpoint(s) | **There is no list endpoint.** The Store API exposes exactly one order route: `GET /wp-json/wc/store/v1/order/{id}` (regex `'/order/(?P<id>[\d]+)'`, confirmed). Order *history* is only at the themed HTML page `/mijn-account/orders/` (`/my-account/orders/`). The merchant REST API `/wp-json/wc/v3/orders` needs a **merchant** consumer key/secret — useless to a consumer. |
| Required headers | none special. The single-order route is authorised by **query params**: `?key=<wc_order_key>&billing_email=<email>`. Confirmed in `OrderAuthorizationTrait::is_authorized()` — `$request->get_param('key')`, `$request->get_param('billing_email')`, then `validate_order_key()` + a `strcasecmp` on the order's billing e-mail. Both values are in the order-received URL and the confirmation mail. |
| Money format | integer **minor units** as a string, with a `currency_minor_unit` field alongside (Store API convention). Confirm per-field when implementing. |
| Bot protection | whatever the host bolts on (Wordfence, Cloudflare). Nothing in core. |
| Feasibility | **MEDIUM** as an e-mail-token adapter, **HARD** as a login adapter (every WooCommerce theme renders `/my-account/orders/` differently — there is no stable DOM). |

**Verdict:** do not build a WooCommerce *login* adapter. Do build WooCommerce
into the **e-mail-token** adapter (§8), where it is one of the four confirmed
platforms and where it is genuinely trivial.

---

## 6 · Lightspeed eCom and CCV Shop — merchant-only, skip

| field | Lightspeed eCom C-Series | CCV Shop |
| --- | --- | --- |
| Retailer / domain | `*.webshopapp.com` / `shoplightspeed.com` | `*.ccvshop.nl` |
| Tier | `browser_interactive` (T3) | `browser_interactive` (T3) |
| Auth | cookie session on the shop's own `/account/` | cookie session on `/mijn-account/` |
| Evidence | Vendor docs (`ecom-support.lightspeedhq.com` — API keys created in the merchant back office, Advanced/Professional plan required); `SEOshop/API-PHP-Client` is a **merchant** client. | Vendor docs (`demo.ccvshop.nl/API/Docs/`), `jacobdekeizer/ccvshop-client`, `Simply-Translate/Connector-CCVShop` — all **merchant** clients using a public/private API key pair from the back office. |
| Confidence | UNVERIFIED (vendor docs read, no source) | UNVERIFIED (vendor docs + third-party client READMEs) |
| Base URL | `https://api.webshopapp.com/{lang}/` | `https://<shop>/API/` |
| Order-history endpoint(s) | `GET /orders.json` — **merchant scope, all customers' orders.** No consumer-scoped endpoint exists. | `GET /orders` — **merchant scope.** No consumer-scoped endpoint exists. |
| Required headers | HTTP Basic with merchant key/secret | `x-hash` HMAC + `x-public` merchant key |
| Money format | decimal | decimal |
| Bot protection | none seen | none seen |
| Feasibility | **BLOCKED** for a consumer connector — the only API is merchant-scoped, and the consumer's own order page is theme-rendered HTML with no stable structure. | **BLOCKED**, same reason. |

Both platforms serve a long tail of small Dutch shops, not the top-50. Even if
they were feasible they would be low value. **Do not attempt.**

---

## 7 · Payment platforms — Klarna, iDEAL, Afterpay/Riverty, Thuiswinkel

### 7.1 · Klarna

| field | |
| --- | --- |
| Retailer / domain | **Klarna** — `app.klarna.com` (web), Klarna mobile app |
| Tier | `browser_interactive` (T3) at absolute best |
| Auth | **password+otp** — `login.klarna.com`, plus device binding; the mobile app additionally does attestation |
| Evidence | Live read of the public JS bundle `https://x.klarnacdn.net/klapp/assets/web/main-4345f91e3415a12b.js` (10.5 MB, fetched 2026-07-28). **No public reverse-engineering project exists** — GitHub search for a Klarna consumer client returned only `fossabot/klarna-api-unofficial` (**last push 2018-09-08**) and `mnording/KlarnaOfflineSDK` (**2017-09-21**), both merchant-side and both dead. Every current Klarna repo (`klarna/*`, `juspay/hyperswitch`, `saleor-app-payment-klarna`) is merchant payment integration. |
| Confidence | **CONFIRMED** that these service paths exist in the shipped bundle. **SPECULATIVE** on request/response shapes, auth scheme, and whether any of it is reachable outside the app. |
| Base URL | `https://app.klarna.com` |
| Order-history endpoint(s) | From the bundle's config object: `ORDERS_SERVICE_BASE_URL: "/api/orders"`. Neighbouring BFFs: `/api/post_purchase_bff`, `/api/pext_orders_bff`, `/api/consumer_wallet_bff`, `/api/app_home_bff`, `/api/manual_transactions_bff`, `/api/auth`, `/api/auth_bff`, and — the killer — **`/api/app_attestation`**. |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | `/api/app_attestation` implies Play Integrity / Apple App Attest on the mobile path. Web path behind `login.klarna.com` with OTP and device binding. |
| Feasibility | **HARD, and low value even if solved.** |

**The coverage argument kills it before the engineering argument does.** Klarna
only ever sees purchases *paid with Klarna*. In the Netherlands the dominant
consumer payment rail is iDEAL, not BNPL. A Klarna connector would return a thin,
biased slice of a Dutch user's purchases while costing more engineering than
Magento, Shopware and the e-mail connector combined. **Do not attempt.**

### 7.2 · iDEAL / Wero

| field | |
| --- | --- |
| Retailer / domain | **iDEAL** (Currence / EPI, migrating to Wero) |
| Tier | `none-found` |
| Auth | n/a |
| Evidence | none found — iDEAL is a payment *initiation* scheme; the consumer never holds an iDEAL account, only a bank account. All iDEAL APIs are acquirer/merchant-side. |
| Confidence | CONFIRMED by absence — there is nothing to authenticate to. |
| Base URL | — |
| Order-history endpoint(s) | **none exist** |
| Required headers | — |
| Money format | — |
| Bot protection | — |
| Feasibility | **BLOCKED.** An iDEAL payment appears as a bank transaction — which this project's *bank* connector already reads. There is no line-item data anywhere in the iDEAL rail. Nothing to build. |

### 7.3 · Afterpay / Riverty

| field | |
| --- | --- |
| Retailer / domain | **Riverty** (formerly AfterPay NL), `riverty.app` / `my.riverty.com` |
| Tier | `browser_interactive` (T3) |
| Auth | unknown (consumer portal login) |
| Evidence | `https://my.riverty.com/` → 302 → 301 → `https://riverty.app/nl-nl/` (live, 2026-07-28). No public API, no reverse-engineering project found. |
| Confidence | UNVERIFIED |
| Base URL | `https://riverty.app` |
| Order-history endpoint(s) | unknown |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | none seen at the edge |
| Feasibility | **HARD**, and — as with Klarna — covers only Riverty-financed purchases, a small and skewed slice. **Do not attempt.** |

### 7.4 · Thuiswinkel Waarborg

| field | |
| --- | --- |
| Retailer / domain | **Thuiswinkel.org / Thuiswinkel Waarborg** |
| Tier | `none-found` |
| Auth | n/a |
| Evidence | It is a certification and dispute-resolution body. It certifies webshops; it never sees a transaction, an order, or a line item. |
| Confidence | CONFIRMED by category — no purchase data exists to fetch. |
| Base URL | — |
| Order-history endpoint(s) | **none exist** |
| Required headers | — |
| Money format | — |
| Bot protection | — |
| Feasibility | **BLOCKED.** The only thing Thuiswinkel offers this project is a *public list of certified Dutch webshops*, which is a decent seed list for platform fingerprinting and nothing more. |

---

## 8 · E-mail — the universal fallback, and the best idea in this document

| field | |
| --- | --- |
| Retailer / domain | **The user's own mailbox** — Gmail, Outlook/Microsoft 365, generic IMAP |
| Tier | `http` (T1) — **no browser, ever** |
| Auth | **oauth+pkce** for Gmail and Microsoft Graph; **password / app-password** for generic IMAP |
| Evidence | Google Gmail API + `developers.google.com/workspace/gmail/markup/reference/order` (schema.org `Order` JSON-LD in mail); Microsoft Graph `/me/messages`. **OSS prior art: effectively none** — see below. |
| Confidence | CONFIRMED for the transport and for the platform token formats (§0); UNVERIFIED for how many NL retailers embed JSON-LD. |
| Base URL | `https://gmail.googleapis.com/gmail/v1` · `https://graph.microsoft.com/v1.0` · `imap.<host>:993` |
| Order-history endpoint(s) | Gmail: `GET /users/me/messages?q=…` then `GET /users/me/messages/{id}?format=full`. Graph: `GET /me/messages?$search=…`. IMAP: `SEARCH`/`FETCH`. |
| Required headers | Gmail/Graph: `Authorization: Bearer <token>`. IMAP: none. |
| Money format | whatever the retailer wrote — **this is the whole problem**, and §0 is the whole answer |
| Bot protection | **none. There is no bot protection on a mailbox.** |
| Feasibility | **MEDIUM to build, EASY to keep running.** Nothing here churns the way a retailer front-end does. |

### 8.1 · Why this beats scraping, properly argued

The naive version of "parse order confirmation e-mails" is bad: HTML mail
templates change, currency formats vary, Dutch and English mix, and you end up
with a regex farm that breaks weekly. That version deserves its reputation.

The version worth building is different. **Treat the mailbox as a token store,
not as a data source.** For four confirmed platforms (§0) the mail contains a
capability token, and the token unlocks a first-party structured API that returns
clean, typed, line-item data. The parser only has to be good enough to extract
*a URL or an order number* — an easy, robust, high-recall job — and the hard part
is then done by the retailer's own API.

Concretely, per platform, all CONFIRMED from source in §3–§5:

- **WooCommerce** → pull `{id}` and `key=wc_order_*` out of the order-received URL → `GET /wp-json/wc/store/v1/order/{id}?key=…&billing_email=…` → full order JSON, no auth.
- **Magento** → pull the order number → `guestOrder(input:{number, email, lastname})` → full `CustomerOrder` with line items, no auth.
- **Shopware 6** → pull the `deepLinkCode` from `/account/order/{code}` → `POST /store-api/order` with a `deepLinkCode` filter → full order, no auth, within 30 days.
- **Shopify** → the `order_status_url`; and separately, the mailbox lets the connector **self-service the 6-digit login OTP** (§1.3), which is what makes an unattended Shopify fetch possible at all.

Second, independent of any platform: **schema.org `Order` JSON-LD embedded in the
mail itself** (Google's "Email Markup"). Retailers add it so Gmail can render an
order card. Where present it gives `merchant`, `orderNumber`, `orderStatus`,
`price`, `acceptedOffer[]` as structured data with zero parsing. Free line items.
**How many NL retailers actually emit it is UNVERIFIED** and is the first thing to
measure — one pass over a real mailbox answers it, and that pass costs nothing.

Only where both of those fail do you fall back to per-retailer HTML templates,
and at that point you are writing template extractors for a handful of large
retailers rather than for all fifty.

### 8.2 · The open-source landscape is empty, and that is worth knowing

I searched GitHub properly for this. There is **no serious, general-purpose,
multi-retailer order-confirmation e-mail parser in open source.** What exists:

| repo | last push | what it is |
| --- | --- | --- |
| `kjaymiller/actual-transaction-email-splitter` | 2026-05-22 | forwards order-confirmation mails into Actual Budget; single-purpose |
| `alexpricedev/paypal-email-parser` | 2026-06-11 | PayPal receipts → Google Sheets, deterministic HTML parsing |
| `connielin07/gas-uber-eats-parser` | 2026-05-17 | Uber Eats only, Apps Script |
| `swjain/gmail-receipt-scrapper` | old | Swiggy/UberEats only, Apps Script |
| `jawj/ocado-orders` | 2022-04-08 | Ocado only |
| `receiptor` (PyPI) | — | Gmail fetch + LLM structuring; a helper, not a parser |
| `NikhilaPusapelly/receipt-data-extractor` | 2026-05-27 | config-driven regex/XPath/OCR engine — closest thing to a reusable base |

Every one is a hobby script for one merchant. `EmilTholin/gmail-api-parse-message`
and `stephenlacy/parse-gmail-email` solve MIME unwrapping and are worth vendoring;
nothing solves the actual problem. **Nobody has built this.** Which is either the
opportunity or the warning, depending on temperament — but it does mean "someone
has already done this" is false here, and you should not spend the night looking.

### 8.3 · The one real cost, stated plainly

Gmail's `gmail.readonly` is a **restricted scope**. Shipping it in a production
app means Google's OAuth verification plus a **CASA security assessment**, which
is money and weeks, not an afternoon. Mitigations, in order of preference:

1. **Generic IMAP first.** No verification, no review, works with Gmail
   app-passwords, Outlook, and every Dutch ISP mailbox. Ship this.
2. Microsoft Graph `Mail.Read` — a normal delegated scope, ordinary consent.
3. Gmail API last, only once the product justifies the assessment.

Starting with IMAP also means the connector is testable tonight against a
throwaway mailbox with no third-party approval in the loop.

---

## 9 · What the Dutch retail sample actually runs on

~98 Dutch retail domains probed unauthenticated on 2026-07-28 for
`/graphql?query={__typename}`, `/.well-known/customer-account-api`,
`/store-api/context` and `/wp-json/wc/store/v1/*`.

| observation | domains |
| --- | --- |
| **Magento GraphQL open and answering** | `www.dille-kamille.nl`, `www.chasin.nl` |
| **Shopify with new customer accounts** | `www.intersport.nl` (shop_id `58562052201`), `www.dailypaperclothing.com` (`6171881`) |
| **Salesforce Commerce Cloud (SFCC)** | `www.hema.nl`, `www.omoda.nl`, `www.etos.nl` |
| **WooCommerce** | `www.wibra.nl` |
| **Shopware 6** | none detected |
| **Edge-defended / challenged anonymous requests** | `www.debijenkorf.nl` (Cloudflare "Just a moment…"), `www.bruna.nl` (Access Denied), `www.bol.com` / `www.zalando.nl` / `www.kruidvat.nl` / `www.trekpleister.nl` / `www.iciparisxl.nl` (Akamai headers), `www.thuisbezorgd.nl` (403) |
| **Own bespoke platform** | `bol.com`, `coolblue.nl`, `wehkamp.nl`, `zalando.nl`, `ah.nl`, `jumbo.com`, `picnic.app`, `mediamarkt.nl` (SAP), `ikea.com` |

**Be honest about what this means.** The Dutch top-50 is dominated by **bespoke
platforms and SFCC**, not by Magento or Shopify. The platform-adapter thesis is
correct and worth building, but it wins on the *mid-market* — the Dille & Kamille
and Chasin tier — not on the household names. The household names are exactly the
retailers where the **e-mail connector** is the only thing that will ever work.

Two methodological caveats, so nobody over-reads the table: an absent
`/graphql` response does not prove a shop is not Magento (the endpoint can be
disabled or blocked at the CDN), and an absent `/.well-known/customer-account-api`
does not prove a shop is not Shopify (that file only appears once new customer
accounts are enabled). The table is a floor, not a census.

---

## 10 · Recommended build order (value × feasibility)

1. **E-mail connector (IMAP first), as a token harvester.** — §8.
   Universal coverage, zero bot protection, nothing to churn, and it is the
   *prerequisite* for the Shopify OTP path. Build the URL/order-number extractor
   and the schema.org JSON-LD reader; do **not** build per-retailer HTML
   templates yet. Highest value in this document by a wide margin.
2. **Magento `guestOrder` / `guestOrderByToken` adapter.** — §3.2.
   Bolts straight onto (1). No login, no captcha, no browser, full line items,
   confirmed from source. Perhaps 40 lines of adapter. Do it the same night as (1).
3. **WooCommerce Store API single-order adapter.** — §5.
   Same shape as (2), same effort, confirmed from source
   (`OrderAuthorizationTrait::is_authorized`).
4. **Magento authenticated adapter** (`generateCustomerToken` → `customer{orders}`),
   with the per-shop reCAPTCHA probe deciding T1 vs T3 at connect time. — §3.3.
   Gives full history rather than per-mail orders.
5. **Shopware 6 adapter.** — §4. Cleanest API here and cheap to write; ranked
   fifth only because NL prevalence is unproven.
6. **Shopify adapter** — discovery → per-shop `client_id` → browser OAuth once,
   with the mailbox supplying the OTP. — §1. Genuinely useful, materially more
   work, and one unresolved question (token persistence) that must be answered
   with a live account before committing a day to it.
7. **Shop app / Login with Shop investigation** — §2. Not a build. A timeboxed
   spike with a real account and a proxy. Highest ceiling of anything here.

---

## 11 · Do not attempt

| target | why not |
| --- | --- |
| **Klarna** | No public reverse-engineering work exists (newest relevant repo: 2018). `/api/app_attestation` in the shipped bundle means device attestation on mobile. Login is OTP + device binding. And even fully solved it only covers Klarna-paid purchases, which in NL is a minority of a minority. Worst effort-to-coverage ratio in the sweep. |
| **iDEAL / Wero** | There is no consumer API and no consumer account. iDEAL payments already show up in the bank connector, with no line items and no way to get any. Nothing exists to build against. |
| **Afterpay / Riverty** | No public API, no prior art, and the same narrow-coverage problem as Klarna. |
| **Thuiswinkel Waarborg** | A certification body. It holds no purchase data. Useful only as a public seed list of Dutch webshops for fingerprinting. |
| **Lightspeed eCom C-Series** | Only a merchant-scoped API key exists; no consumer scope at all. The consumer's own order page is theme-rendered HTML with no stable structure. |
| **CCV Shop** | Same as Lightspeed: merchant-only API, long-tail shops, negligible overlap with the top-50. |
| **WooCommerce `/my-account/orders/` scraping** | Every theme renders it differently. There is no stable DOM across shops, so it is a per-shop adapter wearing a platform adapter's clothes. Use the e-mail-token path instead. |
| **Anything behind Akamai with no public work** | `bol.com`, `zalando.nl`, `kruidvat.nl`, `trekpleister.nl`, `iciparisxl.nl`. A night spent here produces nothing. Reach these through the mailbox. |
| **Shopify `Bearer` prefix** | Stated explicitly because it is the kind of thing that costs an hour: the Customer Account API takes `Authorization: <token>` **without** `Bearer`. Magento takes `Authorization: Bearer <token>` **with** it. |

---

## 12 · Reproducing every live capture

All unauthenticated. All safe to re-run. None of them touch an account.

```bash
# Shopify: discovery (shop_id is in "issuer")
curl -s https://www.intersport.nl/.well-known/openid-configuration
curl -s https://www.intersport.nl/.well-known/customer-account-api

# Shopify: leak the shop's own first-party client_id (per-shop, not global)
curl -s -L -D - -o /dev/null https://shopify.com/58562052201/account | grep -oiE 'client_id=[0-9a-f-]{36}'

# Shopify: confirm the auth header name
curl -s -X POST -H 'Content-Type: application/json' -d '{"query":"{customer{id}}"}' \
  https://shopify.com/58562052201/account/customer/api/2026-07/graphql
# -> 401 {"errors":[{"message":"Missing Authorization Header"}]}

# Shopify: unauthenticated MCP tools/list
curl -s -X POST -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
  https://shopify.com/58562052201/account/customer/api/mcp

# Magento: is GraphQL on?
curl -s 'https://www.dille-kamille.nl/graphql?query=%7B__typename%7D'

# Magento: is the order type there?
curl -s -X POST -H 'Content-Type: application/json' \
  -d '{"query":"{__type(name:\"CustomerOrder\"){fields{name}}}"}' \
  https://www.dille-kamille.nl/graphql

# Magento: per-shop tier probe — reCAPTCHA on the login form?
curl -s https://www.dille-kamille.nl/customer/account/login/ | grep -oi 'Magento_ReCaptcha[A-Za-z]*'
```

Source files read in full for this document:

- `magento/magento2` @ `2.4-develop` — `app/code/Magento/SalesGraphQl/etc/schema.graphqls`, `app/code/Magento/CustomerGraphQl/etc/schema.graphqls`
- `shopware/shopware` @ `trunk` — `src/Core/Checkout/Order/SalesChannel/OrderRoute.php`, `src/Core/Checkout/Customer/SalesChannel/LoginRoute.php`, `src/Core/PlatformRequest.php`
- `woocommerce/woocommerce` @ `trunk` — `plugins/woocommerce/src/StoreApi/Routes/V1/Order.php`, `plugins/woocommerce/src/StoreApi/Utilities/OrderAuthorizationTrait.php`
- `https://x.klarnacdn.net/klapp/assets/web/main-4345f91e3415a12b.js` (Klarna consumer web bundle)
