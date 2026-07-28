# Retailer research — Home, DIY & Garden

Researcher slug: `home-diy`
Date: 2026-07-28 (overnight, unattended)
Scope: read-only. No logins, no accounts, no credentials used. Every probe below is an
unauthenticated GET/POST to a public endpoint, or a read of public source.

---

## Read this first — three corrections to the brief

1. **Praxis is NOT Intergamma.** Praxis belongs to **Maxeda DIY Group** (with Brico /
   BricoPlanit in BE/LU). Intergamma runs **Gamma and Karwei** only. The brief's
   "one platform may cover three" is wrong: it covers **two**. Confirmed by ownership
   sources *and* by the tech — gamma.nl and karwei.nl are byte-for-byte the same Vercel
   stack with mirrored `api.gamma.nl` / `api.karwei.nl` gateways, while praxis.nl is a
   completely different CloudFront/Express micro-frontend estate. Build Gamma+Karwei as
   one connector; build Praxis as a separate one.

2. **The IKEA client everybody links to is pointing at dead hosts.**
   `vrslev/ikea-api-client` (archived 2024-10-03) hardcodes
   `https://purchase-history.ocp.ingka.ikea.com/graphql` and `https://order.ikea.com/...`.
   **Both hostnames are NXDOMAIN today.** Verified twice — local resolver and Google
   public DNS-over-HTTPS (`Status: 3` for both, with the parent zones resolving fine).
   Anyone who builds from that README tonight builds against nothing. The *replacement*
   endpoint is in this document, read out of IKEA's live production bundle and probed.

3. **Blokker no longer sells online.** No cart, no login, no account, no checkout —
   `/nl/login/`, `/nl/account/`, `/nl/winkelwagen/` are all 404 and the homepage contains
   zero commerce vocabulary. It is a catalogue + store locator on the NextChapter
   platform. There is no purchase history to connect to. Do not schedule work for it.

**Nobody has published order-history reverse engineering for any Dutch home/DIY/garden
retailer.** I searched GitHub code + repos for every brand, every `api.<brand>.nl`
hostname, Home Assistant `custom_components`, and the literal endpoint strings. The only
retailer with meaningful prior art is IKEA, and that prior art is stale. Everything
below marked CONFIRMED is confirmed because **I read the retailer's own live JavaScript
or probed their own live endpoint**, not because a repo said so.

---

## IKEA

| field | |
| --- | --- |
| Retailer / domain | IKEA — `www.ikea.com/nl/nl` |
| Tier | `browser_once` — Playwright for login only, then pure HTTP GraphQL on the harvested cookies |
| Auth | Auth0-backed IdP session cookies (`accounts.ikea.com` / `nl.accounts.ikea.com`). **Not** a Bearer token — the live Apollo link is `credentials: "include"` |
| Evidence | Live source: `https://www.ikea.com/nl/nl/purchases/assets/index-BqVqyluC.js` (fetched 2026-07-28). Historical: `github.com/vrslev/ikea-api-client`, last push **2024-10-03**, **archived**, 90 stars — endpoints now dead |
| Confidence | **CONFIRMED-from-source** (IKEA's own bundle) **and CONFIRMED-live** (endpoint probed, returns 401 for the real operation) |
| Base URL | `https://cssom-prod.ingka.com/purchase-history/graphql` |
| Order-history endpoint(s) | GraphQL POST. Ops: `FullHistory`, `authenticated`, `DeliveryStatusInList` (bodies verbatim below) |
| Required headers | `Content-Type: application/json`; `Accept-Language: nl-nl`; `X-Client-Id: 93c183aa-8e15-4e80-8cbe-f812254c7933`; `X-CSSOM-build: order-card-fragment`; `Origin: https://www.ikea.com`; `Referer: https://www.ikea.com/nl/nl/purchases/`; plus session cookies (`credentials: include`) |
| Money format | **Not in `FullHistory`** — that op returns only `id`/`status`/`type`. Totals require the per-order detail query, which lives in a different bundle. The retired API used `Money { code, value }` plus a `formatted` string; assume similar but treat as **UNVERIFIED** until the detail op is captured |
| Bot protection | On the *login* page: Cloudflare Turnstile + hCaptcha + FriendlyCaptcha all referenced from the profile bundle. **On the GraphQL API host: none observed** — it answered a plain curl with no challenge |
| Feasibility | **EASY–MEDIUM** — the API is wide open once you hold cookies; the entire cost is getting through a captcha-guarded Auth0 login once |

### Verbatim, from IKEA's live bundle

```js
var T = {
  COUNTRY: "nl",
  LANGUAGE: "nl",
  APOLLO_CLIENT_NAME: "nl-NL",
  VERSION: "23.12.0",
  WEBAPICLIENTID: "93c183aa-8e15-4e80-8cbe-f812254c7933",
  GRAPHQLURI: "https://cssom-prod.ingka.com/purchase-history/graphql"
};
// Apollo link:
new _e({
  uri: T.GRAPHQLURI,
  fetch: t,
  credentials: "include",
  headers: {
    "Accept-Language": `${T.LANGUAGE}-${T.COUNTRY}`,
    "X-Client-Id": T.WEBAPICLIENTID,
    "X-CSSOM-build": "order-card-fragment"
  },
  batchMax: 3
})
```

Note `batchMax: 3` — this is Apollo's **BatchHttpLink**, so the browser sends a JSON
*array* of operations. A single-object POST works fine too (I proved it below), but if
you replay captured traffic expect arrays.

```graphql
query FullHistory($skip: Int!, $take: Int!, $status: PurchaseStatusFilter) {
  historyData(skip: $skip, take: $take, status: $status) {
    totalPurchases
    historicalPurchases { id status type }
  }
}

query authenticated {
  authenticatedV2 { authenticated customerType }
}

query DeliveryStatusInList($orderNumber: String!) {
  order(orderNumber: $orderNumber) {
    id
    deliveryMethods {
      statusV2 { deliveryStatusV2 tense }
      deliveryDate { ...OrderCardDeliveryDate }
      type
    }
  }
}
```

Also read from the profile bundle: `ORDER_HISTORY: "https://www.ikea.com/nl/nl/purchases/"`
— i.e. IKEA moved purchase history from the (now dead) `order.ikea.com` host onto
`www.ikea.com/{country}/{language}/purchases`.

### Live probes I ran (no credentials)

```
POST https://cssom-prod.ingka.com/purchase-history/graphql   (operation: authenticated)
  -> HTTP 200  {"data":{"authenticatedV2":{"authenticated":false,"customerType":"UNKNOWN"}}}

POST https://cssom-prod.ingka.com/purchase-history/graphql   (operation: FullHistory)
  -> HTTP 200  {"errors":[{"message":"Not authenticated",
                "extensions":{"errorCode":401,"classification":"OperationNotSupported"}}],
                "data":{"historyData":null}}
```

Both operations are registered and live. The API is gated **purely on session state** —
no WAF, no captcha, no signed request, no client secret. `authenticatedV2` is a free
session-validity probe: use it as the connector's "is this connection still alive?" check
without touching the user's order data.

Identity details worth knowing before you write the login step: the profile bundle
carries Auth0 namespaced claim URIs (`https://accounts.ikea.com/memberId`, `/partyUId`,
`/loyaltyPrograms`, `/customerType`, `/isEmailVerified`, `/CM_SMS`,
`/CM_SMSVerificationStatus`) and references `auth0.com/docs/...` error pages. IKEA's
identity is Auth0. The archived client's author gave up on scripted login in 2024 —
"now there's very advanced telemetry that I wouldn't be able to solve in a hundred
years." That is why this is `browser_once` and not `http`: drive a real browser for the
login, then never open a browser again.

---

## Gamma + Karwei (Intergamma) — one connector, two brands

| field | |
| --- | --- |
| Retailer / domain | Gamma — `www.gamma.nl`; Karwei — `www.karwei.nl` (Intergamma, shared platform) |
| Tier | `browser_interactive` |
| Auth | unknown — could not reach a login page without a browser |
| Evidence | Live headers (2026-07-28). Third-party: `github.com/Bortmon/Gammel`, last push **2025-07-14** — a Flutter app that calls `api.gamma.nl` / `api.karwei.nl` (stock only, no orders) |
| Confidence | **CONFIRMED-from-source** for the API hostnames and the challenge; **UNVERIFIED** for anything order-related |
| Base URL | Storefront `https://www.gamma.nl` / `https://www.karwei.nl`; API gateway `https://api.gamma.nl` / `https://api.karwei.nl`; account area `https://www.gamma.nl/my/` (`mijn.gamma.nl` 301s there) |
| Order-history endpoint(s) | **none found.** Not discoverable without a browser session |
| Required headers | For the public stock API (per Gammel): `Origin: https://www.gamma.nl`, `Referer: https://www.gamma.nl/`, `Cookie: PREFERRED-STORE-UID=<id>`, plus a browser `User-Agent` |
| Money format | unknown |
| Bot protection | **Vercel Attack Challenge / BotID.** `server: Vercel`, `x-vercel-mitigated: challenge`, `x-vercel-challenge-token: …`, HTTP **429** on every non-browser request |
| Feasibility | **HARD** — the storefront rejects all scripted HTTP; the interesting API is undocumented |

Every `www.gamma.nl` and `www.karwei.nl` request returned **429** with
`x-vercel-mitigated: challenge`, including one sent with a complete Chrome header set
(`sec-ch-ua`, `sec-fetch-*`, `Accept-Language: nl-NL`, `Upgrade-Insecure-Requests`). This
is not a rate limit you can wait out — it is a JS proof-of-work challenge that only a
real browser engine clears.

The exception, and the thing worth chasing later: **`api.gamma.nl` is not behind the
challenge.** It answers plainly with a NestJS-shaped JSON error:

```json
{"timestamp":"2026-07-28T00:25:20.127Z","path":"/foo","status":404,
 "error":"Not Found","requestId":"4e1b75de-1056113"}
```

Bortmon/Gammel confirms the live service pattern (`https://api.gamma.nl/stock/2/`,
`https://api.karwei.nl/stock/2/`) and, incidentally, confirms the shared platform: the
same client hits both brands with mirrored hostnames and near-identical headers. Gamma and
Karwei ship a well-regarded native app (built by Q42 / Elements). **That app's API is
almost certainly reachable over plain HTTP on `api.gamma.nl`, un-challenged.** Nobody has
published its routes. Finding them means capturing traffic from the app — a daytime job
with a phone and mitmproxy, not an unattended-night job. I did not blind-enumerate the
gateway beyond a handful of obvious service names (all 404) because guessing paths against
a live production API is noisy and low-yield.

---

## Praxis (Maxeda DIY Group — *not* Intergamma)

| field | |
| --- | --- |
| Retailer / domain | Praxis — `www.praxis.nl` |
| Tier | `browser_once` |
| Auth | cookie session (account area branded "VoordeMakers") |
| Evidence | Live probes + live frontend source (2026-07-28). No public project exists |
| Confidence | **CONFIRMED-from-source** for the routes and the redirect chain; **UNVERIFIED** for the JSON order payload |
| Base URL | `https://www.praxis.nl` |
| Order-history endpoint(s) | Page: `GET /voordemakers/myprofile/orders` → **302** → `/voordemakers/login?redirectUrl=/voordemakers/myprofile/orders`. Backing micro-service pattern is `/voordemakers/<service>/api/v1/<resource>` — proven by `/voordemakers/citadel-logout/api/v1/logout` (400, `application/json`) and `/voordemakers/citadel-login/api/v1/login` (403, `application/json`). The orders service name is not yet known |
| Required headers | Browser `User-Agent` is **mandatory** — default curl UA gets 403, Chrome UA gets 200. Session cookie thereafter |
| Money format | unknown |
| Bot protection | **None seen.** CloudFront + a User-Agent filter only. No JS challenge, no captcha on the pages I fetched |
| Feasibility | **MEDIUM** — clean architecture, no serious defences; the only unknown is the exact orders JSON route |

Stack: CloudFront → `server: prd01 webedge`, `x-powered-by: Express`, micro-frontends
served from `assets.praxis.nl` under three bundles (`citadel` = account/profile,
`frontier` = chrome/header/footer, `flexible-page` = CMS), Algolia search, Contentful CMS,
Kameleoon A/B, LaunchDarkly flags. Session cookies observed on first hit: `sessionId`,
`kameleoonVisitorCode`, `country=nl`.

Of the four `citadel-*` service names I tried, two exist and two 404 — so the naming
convention is real but `citadel-orders` / `citadel-profile` are not the right names. The
cheap way to finish this is one authenticated browser session with devtools open on
`/voordemakers/myprofile/orders`; the XHR will name itself. That is a five-minute job for
the owner tomorrow, and it would likely promote Praxis from `browser_once` to `http`.

---

## HEMA

| field | |
| --- | --- |
| Retailer / domain | HEMA — `www.hema.nl` |
| Tier | `browser_once` (could become `http` — see the OCAPI note) |
| Auth | cookie session, issued by Salesforce Commerce Cloud storefront login |
| Evidence | Live source + live probes (2026-07-28). No public project exists |
| Confidence | **CONFIRMED-from-source** — controller names read out of HEMA's own HTML, redirect chains probed |
| Base URL | `https://www.hema.nl` |
| Order-history endpoint(s) | **Legacy SFRA (live):** `GET /on/demandware.store/Sites-HemaNL-Site/nl_NL/OrderHistory-LoadMoreOrders` → 302 → `Login-Show` when unauthenticated. Sibling: `OrderHistory-LazyOrderInvoices`, `Order-ReorderItem`. **New frontend:** `GET /mijn-hema/aankopen` → 307 → `/_platform/auth/login?siteId=HemaNL&locale=nl-nl&return_url=%2Fmijn-hema%2Faankopen` → 307 → the same SFCC `Login-Show`. **Session probe:** `GET /_platform/auth/session` → `{"authenticated":false,"guest":true}`. **OCAPI (live, needs a client_id):** `https://www.hema.nl/s/HemaNL/dw/shop/{v21_3\|v22_10\|v23_2}/…` |
| Required headers | Browser `User-Agent`, `Accept-Language: nl-NL`, SFCC session cookies (`dwsid`, `sid`, `dwac_*`, `cqcid`). For OCAPI additionally `x-dw-client-id` or `?client_id=` |
| Money format | decimal, EUR. `SITE_CURRENCY: "EUR"` is set in the storefront config. OCAPI returns decimal numbers on `order_total` / `product_total` |
| Bot protection | **reCAPTCHA on login.** Two site keys in the page config: `RECAPTCHA_SITE_KEY: 6LdzlDsUAAAAAJhiTJUHsUDLyPuP1PmU39S8c0U9` and `RECAPTCHA_INVISIBLE_SITE_KEY: 6LdDtwAsAAAAAEgbvo2jWpI8-uQiu2NrkVsY9ZEz`. Cloudflare + CloudFront in front, but no challenge served to my requests |
| Feasibility | **MEDIUM** — well-understood platform; the invisible reCAPTCHA usually passes silently in a real browser but can escalate to an image challenge, and tonight nobody can solve one |

HEMA is the best-documented *platform* in this batch: it is Salesforce Commerce Cloud
(Demandware), site id `Sites-HemaNL-Site`, locale `nl_NL`. Identified from cookies
(`dwac_*`, `cqcid`, `cquid`, `__cq_dnt`, `dw_dnt`, `redirectOption=HemaNL^NL^nl_NL`) and
then confirmed by the hundreds of `/on/demandware.store/Sites-HemaNL-Site/nl_NL/…`
controller URLs in the homepage HTML.

**The OCAPI prize, and why I could not claim it.** HEMA's Open Commerce API is live:

```
GET https://www.hema.nl/s/HemaNL/dw/shop/v23_2/site
  -> HTTP 400  {"_v":"23.2","fault":{"type":"MissingClientIdException",
                "message":"The client ID is missing."}}
```

(same for `v21_3` and `v22_10`). OCAPI gives you the documented, stable, browser-free
route: `POST /customers/auth` with Basic credentials for a JWT, then
`GET /customers/{customer_id}/orders`. That is tier-1 `http` territory. It needs a
registered `client_id`, which is **not** anywhere in the storefront JavaScript — I grepped
the full homepage for UUIDs, `client_id`, `ocapi`, `scapi`, `slas` and found nothing,
because the storefront is server-rendered SFRA and never calls OCAPI from the browser.
The client_id will be inside the HEMA mobile app. Extracting it is a daytime task.
**If someone pulls that client_id, HEMA jumps to the top of this list.**

Note also that HEMA is mid-migration: `/mijn-hema/*` is a new composable frontend with its
own `/_platform/` BFF, but it still delegates authentication to the old SFCC storefront, so
one session cookie jar covers both. `/_platform/auth/session` is a perfect zero-cost
liveness check for the connector.

---

## Hornbach

| field | |
| --- | --- |
| Retailer / domain | Hornbach — `www.hornbach.nl` |
| Tier | `browser_interactive` at best; realistically **`none-found`** |
| Auth | unknown — never reached a real page |
| Evidence | Live response body (2026-07-28). No public project exists |
| Confidence | **CONFIRMED-from-source** for the bot protection; nothing else could be observed |
| Base URL | `https://www.hornbach.nl` |
| Order-history endpoint(s) | none reachable. `/mijn-account/bestellingen/` returns HTTP 200 but the body is the challenge page, not the site |
| Required headers | n/a |
| Money format | unknown |
| Bot protection | **F5 Distributed Cloud Bot Defense (Shape Security).** `<title>Client Challenge</title>`, assets under `/_fs-ch-<token>/`, bootstrap `/_fs-ch-<token>/script.js`, a `_fs_ch_st_*` cookie with `Max-Age=10`, and a lockdown CSP (`script-src 'self' 'sha256-…'`) |
| Feasibility | **BLOCKED** |

Every URL on hornbach.nl — homepage, login, order history — returns the same 3 KB
interstitial. F5/Shape is in the top tier of anti-automation: it fingerprints the JS
runtime, the event timing and the TLS stack, and it is specifically built to detect
headless and instrumented browsers. It is also the system most likely to escalate to an
account lock, and Hornbach is a low-frequency purchase for most households.

This is the "waste of a night" the brief asked me to name. **Do not attempt Hornbach.**

---

## Intratuin

| field | |
| --- | --- |
| Retailer / domain | Intratuin — `www.intratuin.nl` |
| Tier | `browser_once` |
| Auth | cookie session (`PHPSESSID` + Magento `form_key`). A customer Bearer token may also be obtainable — see below |
| Evidence | Live probes (2026-07-28). No public project exists, but the *platform* is fully documented by Adobe |
| Confidence | **CONFIRMED-from-source** that it is Magento 2 with the customer REST surface live; **UNVERIFIED** which route yields the order list |
| Base URL | `https://www.intratuin.nl` |
| Order-history endpoint(s) | **Storefront (works):** `GET /sales/order/history` (Magento 2 canonical route; exists — 301 normalises the trailing slash). Login at `POST /customer/account/loginPost`. **REST:** `POST /rest/V1/integration/customer/token` for a customer Bearer. **GraphQL: disabled** — `POST /graphql` 302s to `/` and `GET /graphql` returns the Magento 404 page |
| Required headers | Browser `User-Agent`; `Content-Type: application/json` for REST; `Authorization: Bearer <customer token>` for REST |
| Money format | decimal, EUR. Confirmed live: `GET /rest/V1/directory/currency` → `{"base_currency_code":"EUR","base_currency_symbol":"€",…}` |
| Bot protection | **None seen.** Fastly CDN (`x-served-by: cache-rtm-…-RTM`), no challenge, no captcha on the pages fetched |
| Feasibility | **MEDIUM** — undefended and on a standard platform, but GraphQL being switched off removes the clean order query |

Magento 2 confirmed three ways: the canonical `/customer/account/login/` and
`/sales/order/history` routes both resolve; the theme path `frontend/ISM/intratuin/nl_NL`
appears in the page; and the REST API answers publicly.

The decisive probe:

```
GET https://www.intratuin.nl/rest/V1/customers/me
  -> HTTP 401  {"message":"Gebruiker is niet gemachtigd tot %resources.",
                "parameters":{"resources":"self"}}
```

The route exists and is scoped to `self` — i.e. the customer-token REST surface is
switched on. That is a real, documented, browser-free authentication path.

The catch: **Magento core has no customer-scoped order-*list* REST endpoint.**
`GET /rest/V1/orders` is admin-scoped. Normally you would use GraphQL
(`{ customer { orders { items { number order_date total { grand_total { value currency } } } } } }`),
and Intratuin has GraphQL disabled at the edge. So the dependable route tonight is the
old-fashioned one: session login, then parse `/sales/order/history`. Worth ten minutes
tomorrow to check whether `/rest/V1/customers/me` returns anything order-shaped with a
customer token — if it does, Intratuin becomes `http`.

---

## Tuincentrum.nl

| field | |
| --- | --- |
| Retailer / domain | Tuincentrum.nl — `tuincentrum.nl` (note: `www.` 301s to apex) |
| Tier | `browser_once` |
| Auth | unknown, presumed cookie session |
| Evidence | Live probes (2026-07-28). No public project exists |
| Confidence | **UNVERIFIED** beyond "it is a real Nuxt webshop with a login" |
| Base URL | `https://tuincentrum.nl` |
| Order-history endpoint(s) | **none found.** `/login` 200, `/account` 200; `/account/orders`, `/account/bestellingen`, `/mijn-account/bestellingen`, `/mijn-bestellingen` all 404 |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | **none seen** |
| Feasibility | **MEDIUM** to build, **LOW** value |

A genuine transactional webshop (cart vocabulary present, title "Online planten kopen voor
tuin & huiskamer") on Nuxt/Vue. Undefended. But it is a single-site plant retailer with a
small customer base and low purchase frequency, and I could not find the order route by
inspection — the SPA resolves it client-side. The effort-to-value ratio is poor. Park it.

---

## Leen Bakker

| field | |
| --- | --- |
| Retailer / domain | Leen Bakker — `www.leenbakker.nl` (also `.be`) |
| Tier | `browser_once` |
| Auth | unknown, presumed cookie session against own-origin `/api/` routes |
| Evidence | Live source + probes (2026-07-28). No public project exists |
| Confidence | **CONFIRMED-from-source** for the stack and the account route; **UNVERIFIED** for the order endpoint |
| Base URL | `https://www.leenbakker.nl` |
| Order-history endpoint(s) | **partially found.** Account SPA at `/account/overview` (200) and `/account/wishlist`. Next.js data route `GET /_next/data/IHSPucl9VlUUolh8S3viB/account/overview.json` returns 200 JSON but carries only navigation/CMS props — the order data is fetched client-side from same-origin `/api/…` routes whose paths are not in the entry bundle |
| Required headers | Browser `User-Agent`; the Next.js `buildId` (`IHSPucl9VlUUolh8S3viB`) is required in `_next/data` URLs and **changes on every deploy** — never hardcode it, scrape it from `__NEXT_DATA__` |
| Money format | unknown |
| Bot protection | **none seen.** `server: Google Frontend` (App Engine / Cloud Run), no challenge |
| Feasibility | **MEDIUM** |

Next.js + Contentful CMS + Bloomreach search (`dxpapi.com`) + Bazaarvoice reviews, hosted
on Google. Completely undefended — the obstacle is purely that the order API path is
hidden inside a lazily-loaded route chunk. One authenticated devtools session names it.

---

## Blokker

| field | |
| --- | --- |
| Retailer / domain | Blokker — `www.blokker.nl` |
| Tier | **`none-found`** |
| Auth | n/a — there is no customer account |
| Evidence | Live probes + page source (2026-07-28) |
| Confidence | **CONFIRMED-from-source** |
| Base URL | `https://www.blokker.nl` |
| Order-history endpoint(s) | **none — the site has no webshop.** `/nl/login/`, `/nl/account/`, `/nl/my-account/`, `/nl/mijn-account/`, `/nl/winkelwagen/` all 404 |
| Required headers | n/a |
| Money format | n/a |
| Bot protection | none seen |
| Feasibility | **BLOCKED — nothing to build** |

The page identifies itself in an HTML comment: `Powered by NextChapter eCommerce` /
`Copyright © NextChapter Software B.V.`, assets on `cdn.nextchapter-ecommerce.com`,
ASP.NET session cookie. It has category browsing and a store finder, and **no** cart,
checkout, login, prices-in-basket or account links anywhere on the homepage — a grep for
the entire Dutch commerce vocabulary (`winkelwagen`, `bestellen`, `afrekenen`, `inloggen`,
`account`, `bezorg`, `prijs`) matched **zero** times.

Post-restructuring Blokker is a physical-store brand with an online catalogue. There is no
online purchase history in existence to read. Remove it from the roadmap.

---

## Bolia

| field | |
| --- | --- |
| Retailer / domain | Bolia — `www.bolia.com/nl-nl/` |
| Tier | `browser_once` |
| Auth | cookie session (ASP.NET / Optimizely) |
| Evidence | Live probes (2026-07-28). No public project exists |
| Confidence | **CONFIRMED-from-source** for the auth gate; **UNVERIFIED** for the order payload |
| Base URL | `https://www.bolia.com` |
| Order-history endpoint(s) | Account area `GET /nl-nl/mybolia/` → **302** → `/nl-nl/mybolia/inloggen/?returnUrl=%2fnl-nl%2fmybolia%2f&title=MyBOLIA`. Specific order sub-route not identified |
| Required headers | Browser `User-Agent`; cookies `.ASPXANONYMOUS`, `EPi:StateMarker` |
| Money format | unknown |
| Bot protection | reCAPTCHA referenced on the page; Cloudflare in front, no challenge served |
| Feasibility | **MEDIUM** to build, **LOW** value |

Optimizely/Episerver CMS with a Remix frontend on ASP.NET. Danish design furniture: a
handful of high-value orders per customer per decade. Correct engineering, wrong customer
volume. Low priority.

---

## FonQ

| field | |
| --- | --- |
| Retailer / domain | FonQ — `www.fonq.nl` (Etrias N.V. platform) |
| Tier | `browser_once` |
| Auth | cookie session |
| Evidence | Live probes + page attributes (2026-07-28). No public project exists |
| Confidence | **CONFIRMED-from-source** for the routes and the auth gate; **UNVERIFIED** for the payload |
| Base URL | `https://www.fonq.nl` |
| Order-history endpoint(s) | `GET /customer/account/orders/` — route exists (301 normalises the trailing slash). Auth gate proven on the sibling: `GET /customer/account/details` → **302** → `/customer/account/login`. Login page `GET /customer/account/login` → 200 |
| Required headers | Browser `User-Agent`, `Accept-Language: nl-NL`, session cookie |
| Money format | EUR — `data-currency="EUR"`, `data-locale="nl_NL"` on `<html>` |
| Bot protection | **none seen.** Cloudflare CDN, cached, no challenge |
| Feasibility | **MEDIUM** — undefended, routes already located |

Despite the Magento-looking `/customer/account/*` route names, this is **not** Magento —
`/rest/V1/directory/currency` returns the storefront HTML, not JSON. It is the in-house
**Etrias** platform (assets on `cdn.etrias.nl`), with `data-store-code="fonq_nl"` and
`data-store-id="303"` on the root element.

That store-code/store-id pattern is the interesting part: **Etrias also operates Wehkamp**,
and a multi-store platform usually means one connector shape covers several brands with
only the store code changing. Unverified, but cheap to test and a potentially large payoff
if someone else in this research batch is covering Wehkamp.

---

## Expert

| field | |
| --- | --- |
| Retailer / domain | Expert — `www.expert.nl` |
| Tier | `browser_once` |
| Auth | cookie session — Laravel (`expert_session` + `XSRF-TOKEN`, needs the `_token` CSRF field from the login form) |
| Evidence | Live probes + cookie fingerprint (2026-07-28). No public project exists |
| Confidence | **CONFIRMED-from-source** for the routes and the auth gate; **UNVERIFIED** for the payload |
| Base URL | `https://www.expert.nl` |
| Order-history endpoint(s) | `GET /customer/account/orders` → **302** → `/customer/account/login`. Also `/customer/account` → 302 → same. Login `GET/POST /customer/account/login`, register `/customer/account/register` |
| Required headers | Browser `User-Agent`; cookies `expert_session`, `XSRF-TOKEN`; on POST either `X-XSRF-TOKEN` (decrypted) or the `_token` hidden field from the form |
| Money format | unknown (server-rendered) |
| Bot protection | reCAPTCHA present somewhere on the site; **no challenge on the origin** — Cloudflare passes plain requests through |
| Feasibility | **MEDIUM**, and arguably the easiest non-IKEA build here |

Cookie shape is unmistakably Laravel — `XSRF-TOKEN` and `expert_session` are both Laravel's
encrypted `{"iv":…,"value":…,"mac":…,"tag":…}` envelope. Search is Tweakwise. A standard
Laravel form login (GET the form, lift `_token`, POST, keep the session cookie, then GET
the orders page) is one of the most predictable patterns in web automation, and there is no
JS challenge in the way. The order-history route is already confirmed to exist and to be
auth-gated — that is most of the reconnaissance done.

---

# Recommended build order

Ranked by **value × feasibility**, not by how interesting the platform is.

1. **IKEA** — the only retailer here with a confirmed, live, uncontested JSON API, exact
   headers, exact GraphQL operations, and a free session-liveness probe
   (`authenticatedV2`). High household penetration and genuinely itemised receipts.
   `browser_once`: one Playwright login through Turnstile, harvest cookies, then never
   open a browser again. Start here tonight; you can have `FullHistory` returning real
   data as soon as someone logs in. **Caveat to plan for: `FullHistory` returns no money.**
   Budget a second step to capture the per-order detail operation — send only the order
   IDs the list gives you, and expect to fetch detail per order.

2. **Expert** — Laravel form-login, confirmed auth-gated `/customer/account/orders`, no
   challenge, no captcha in the path. Low glamour, high completion probability. This is
   the one most likely to be *finished* rather than *started*.

3. **HEMA** — big basket counts, very frequent purchases, and a platform whose every
   behaviour is publicly documented. Confirmed `OrderHistory-LoadMoreOrders`. Sits at #3
   only because of the invisible reCAPTCHA on login. **Re-rank to #1 the moment somebody
   pulls the OCAPI `client_id` out of the HEMA app** — that converts it from
   `browser_once` to a documented `http` connector, which is the best outcome available in
   this entire batch.

4. **Intratuin** — Magento 2, undefended, customer REST surface confirmed live. Solid
   seasonal volume. Held back only because GraphQL is switched off, forcing HTML parsing of
   `/sales/order/history`.

5. **Praxis** — no bot protection worth the name (a User-Agent filter), confirmed
   auth-gated `/voordemakers/myprofile/orders`, and a clean `/voordemakers/<svc>/api/v1/`
   JSON micro-service convention. One authenticated devtools session names the orders
   service and this probably becomes `http`. Good value for the effort.

6. **FonQ** — undefended, routes located, decent NL home-and-living volume. Bonus: test
   whether the Etrias `store_code` pattern also unlocks Wehkamp before building it
   bespoke.

7. **Leen Bakker** — undefended and straightforward; only the API path is missing. Middling
   purchase frequency. Do it when you want an easy win.

8. **Gamma + Karwei** — genuinely high value (two big DIY brands, one connector) but the
   only route in tonight is a full `browser_interactive` session against Vercel's
   challenge. The right move is *not* to fight the website: spend a daytime hour capturing
   the mobile app against `api.gamma.nl`, which is not challenge-protected. Defer, then
   revisit with a phone.

9. **Bolia** — correct and buildable, but a customer buys a sofa once. Do it only when the
   backlog is empty.

10. **Tuincentrum.nl** — undefended but small, and the order route resisted inspection.
    Lowest-value real target.

---

# Do not attempt

- **Hornbach** — F5 Distributed Cloud Bot Defense (Shape Security) on every single URL.
  Confirmed from the served `Client Challenge` page, the `/_fs-ch-<token>/` asset paths and
  the 10-second `_fs_ch_st_*` cookie. Shape is purpose-built to detect instrumented and
  headless browsers, there is zero public work to stand on, and DIY-store purchase
  frequency is low. This is exactly the "Akamai-class defence with no public work" case:
  it would consume a night and produce nothing. Skip it entirely.

- **Blokker** — not a defensive problem, an existential one. The site has no cart, no
  checkout, no login and no accounts. There is no online purchase history to connect to.
  Delete it from the retailer list rather than scheduling it.

- **Gamma / Karwei via the website, tonight** — the Vercel challenge returns 429 to every
  scripted request including a fully-populated Chrome header set. Attacking it headlessly
  is a fight with a bot vendor, not with a retailer. Wait for the mobile-app capture.

- **Any IKEA work that starts from `vrslev/ikea-api-client`'s endpoints** — the repo is
  archived (2024-10-03) and both hostnames it targets are NXDOMAIN. Its *structure* is
  still a useful reference for the response shapes; its *URLs and its `Authorization:
  Bearer` auth model are wrong*. The live API is cookie-authenticated
  (`credentials: "include"`). Use the values in this document.

- **Blind path enumeration against `api.gamma.nl`, Praxis `citadel-*`, or HEMA
  `/_platform/*`** — I stopped after a handful of obvious guesses each. Hammering
  production API gateways with wordlists is noisy, is what bot-detection systems are tuned
  to catch, and risks getting the owner's IP or account flagged before a single connector
  ships. Every one of these unknowns is a five-minute devtools observation for a
  logged-in human. Ask tomorrow instead of guessing tonight.

---

# Method note

- All CONFIRMED claims come from either (a) reading a retailer's live production
  JavaScript, or (b) an unauthenticated probe of a live endpoint whose response I have
  quoted. Where I only have a third party's assertion, it is marked UNVERIFIED.
- GitHub was searched for code and repositories across every brand name, every
  `api.<brand>.nl` hostname, the literal string `purchase-history.ocp.ingka.ikea.com`,
  Home Assistant `custom_components`, and the usual `-cli` / `-api` / reverse-engineering
  patterns. **Result: no public order-history work exists for any retailer in this batch.**
  The one adjacent find, `Bortmon/Gammel` (last push 2025-07-14), is an employee-shift app
  that touches only Gamma's public stock API.
- Last-commit dates are recorded for every repository cited, per the brief. The IKEA
  repository's staleness turned out to be the single most important finding here.
