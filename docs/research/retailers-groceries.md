# Retailer research — grocery & drugstore

Researcher: `groceries` slug. Date: 2026-07-28. Nothing was logged into; no accounts
created; no credentials touched. All probing was unauthenticated GETs against public
hosts, plus reading public source.

## Headline findings (read this first)

1. **Picnic is a clean, unprotected mobile JSON API. Build it first.** Everything is
   CONFIRMED from an actively-maintained client (last commit 2026-07-02).
2. **The "Etos and Gall & Gall are Ahold Delhaize, so try an AH-shaped API" hypothesis
   is CORRECT — but not where you'd look.** Their *websites* are Salesforce Commerce
   Cloud behind Akamai (a dead end). Their *mobile* APIs at `api.etos.nl` and
   `api.gall.nl` are byte-for-byte the same gateway as `api.ah.nl`, and are **not**
   Akamai-blocked. This is the highest-value discovery here.
3. **Kruidvat is a waste of a night.** Akamai Bot Manager plus a waiting room, and the
   API host 403s at the edge on *every* path including static JS. Do not attempt.
4. Two of the four "leads" that a README would have handed you are **stale or wrong**:
   the Picnic Python client everyone cites is 3 years old and has no 2FA at all, and the
   Zooplus Cozy konnector's login path no longer exists. Details in each section.

---

## Picnic — picnicinternational.com

| field | |
| --- | --- |
| Retailer / domain | Picnic — `storefront-prod.nl.picnicinternational.com` |
| Tier | `http` (no browser) |
| Auth | password (MD5) + SMS OTP when the account has 2FA enabled |
| Evidence | https://github.com/MRVDH/picnic-api — last commit **2026-07-02**. Cross-checked against https://github.com/MikeBrink/python-picnic-api — last commit **2023-05-07** |
| Confidence | **CONFIRMED-from-source** (read `lib/http-client.js`, `lib/domains/auth/service.js`, `lib/domains/delivery/service.js`, `lib/domains/cart/types.d.ts`) |
| Base URL | `https://storefront-prod.{nl|de|fr}.picnicinternational.com/api/15` |
| Order-history endpoint(s) | `POST /deliveries/summary` body = status filter array, e.g. `[]` or `["COMPLETED"]` → slim list. `GET /deliveries/{deliveryId}` → full detail **with line items**. Legacy `POST /deliveries` also exists in the older Python client. |
| Required headers | `User-Agent: okhttp/4.9.0`; `Content-Type: application/json; charset=UTF-8`; `Accept-Language: nl` (`de`/`fr` by country); `x-picnic-auth: <token>`. Only for `/scenario`, `/position` and the 2FA calls: `x-picnic-agent: 30100;1.236.1-15553;` and `x-picnic-did: <16 hex chars>` |
| Money format | **cents, integers.** `OrderLine.display_price`, `OrderLine.price`, `OrderArticle.price` (typed in source as "Base per-unit price in cents"), `Order.total_price`, `checkout_total_price`, `total_savings`, `total_deposit` |
| Bot protection | none seen |
| Feasibility | **EASY** — plain JSON over HTTPS, no captcha, no edge protection, stable path since at least 2020 |

**Login flow (CONFIRMED).** `POST /user/login` with body
`{"key": <email>, "secret": md5_hex(password), "client_id": 30100}`. The auth token is
returned in the **response header** `x-picnic-auth`, not the body. Send it back as the
`x-picnic-auth` request header. It is refreshed opportunistically: every response may
carry a new `x-picnic-auth` header which the client swaps in — so persist the latest one.

**2FA (CONFIRMED, and the trap).** The login response body carries
`second_factor_authentication_required: true`. Then `POST /user/2fa/generate`
`{"channel":"SMS"}`, and `POST /user/2fa/verify` `{"otp": "<code>"}`. Verify returns
**HTTP 204 with an empty body** and the new token again in the `x-picnic-auth` header.
Both 2FA calls require the `x-picnic-agent`/`x-picnic-did` headers.

**Read this before you copy the popular client.** `MikeBrink/python-picnic-api` is what
the Home Assistant integration is based on and what most blog posts point at. Its last
commit is 2023-05-07 and its source contains **no 2FA handling whatsoever** — it will
simply fail once an account is enrolled. It also still sends `okhttp/3.9.0` and the old
agent string `30100;1.15.183-14941;`. Use MRVDH's values above.

**Line-item trap.** `POST /deliveries/summary` returns `DeliveryOrder` objects that have
`total_price` but **no `items` array**. You must call `GET /deliveries/{id}` per delivery
to get `orders[].items[]` (`OrderLine` → `items[]` of `OrderArticle` with `name`,
`unit_quantity`, `price`). Budget for N+1 requests.

---

## Etos — etos.nl

| field | |
| --- | --- |
| Retailer / domain | Etos (Ahold Delhaize) — `api.etos.nl` |
| Tier | `browser_once` (browser only to capture the OAuth `code`; everything after is `http`) |
| Auth | oauth authorization-code, custom-scheme redirect. **PKCE not observed** in the AH flow |
| Evidence | No Etos-specific public project exists. Architecture identity established by my own probes (below). AH flow read from https://github.com/salujayatharth/ah-api — last commit **2026-02-14** — and https://gist.github.com/jabbink/8bfa44bdfc535d696b340c46d228fdd1 |
| Confidence | **CONFIRMED-from-source** that `api.etos.nl` is the same gateway build as `api.ah.nl`. **UNVERIFIED** that the receipt GraphQL operations resolve on Etos, and **UNVERIFIED** which `clientId` the Etos app uses |
| Base URL | `https://api.etos.nl` |
| Order-history endpoint(s) | `POST /graphql`. AH operations (unverified on Etos): `posReceiptsPage(pagination: OffsetLimitPagination!)`, `posReceiptDetails(id: String!)`, `posReceiptPdf(id: String!)` |
| Required headers | `User-Agent: Appie/9.27.0` (AH value; Etos app UA unknown), `Content-Type: application/json`, `Accept: application/json`, `Authorization: Bearer <access_token>` |
| Money format | AH schema returns an object `{ amount, formatted }` per money field (`totalAmount`, `total`, `price`). **UNVERIFIED** whether `amount` is decimal or cents — must be checked against one real response |
| Bot protection | `www.etos.nl`: **Akamai Bot Manager** (`_abck`, `bm_sz`) + Cloudflare + reCAPTCHA. `api.etos.nl`: **none observed** — clean JSON errors, no edge challenge |
| Feasibility | **MEDIUM** — the transport is easy and unprotected; the unknowns are the app's `clientId` and whether Etos exposes the same receipt operations |

**How the gateway identity was established (my own probes, reproducible).** Identical
responses across all three hosts:

| path (GET) | api.ah.nl | api.etos.nl | api.gall.nl |
| --- | --- | --- | --- |
| `/graphql` | 401 | 401 | 401 |
| `/mobile-auth/v1/auth/token` | 405 | 405 | 405 |
| `/mobile-services/v1/receipts` | 404 | 404 | 404 |

All three return the identical `WWW-Authenticate: Bearer realm="oauth", error="unauthorized",
error_description="Missing valid security token"` on `/graphql`, the identical
`{"error":"not_found","error_description":"404 NOT_FOUND"}` body, and an
`x-correlation-id` header. The 405 on `/mobile-auth/v1/auth/token` leaks the upstream
rewrite — the body reports `"path":"/v1/auth/token"` on all three, i.e. the gateway
strips the `/mobile-auth` prefix and routes to the same `mobile-auth` service. 405 also
proves the route exists and wants POST.

**AH token flow (CONFIRMED from `app/client.py`).**
- `POST /mobile-auth/v1/auth/token` body `{"clientId":"appie","code":"<code>"}` → `access_token`, `refresh_token`, `expires_in` (default 7200)
- `POST /mobile-auth/v1/auth/token/refresh` body `{"clientId":"appie","refreshToken":"<rt>"}`
- `POST /mobile-auth/v1/auth/token/anonymous` body `{"clientId":"appie"}` (device token, no user)
- The `code` comes from `https://login.ah.nl/secure/oauth/authorize?client_id=appie&redirect_uri=appie://login-exit&response_type=code`, which 303s to the custom scheme with `?code=`.

**On the `clientId` — do not guess.** `login.etos.nl` exists and runs the same IdP.
I probed it with a valid/invalid discriminator (a real `client_id` 302s to `/login`, a
bogus one returns 400). Results: `etos`, `etosapp`, `etos-app`, `mijnetos` all → **400**;
`appie` → **302**. The same holds on `login.gall.nl` (`gall`, `gallgall`, `gall-app`,
`gallengall` → 400; `appie` → 302). I cannot tell from outside whether `appie` is
genuinely accepted for the Etos brand or whether the client registry is simply shared
across the IdP deployment. **Pull the real `clientId` out of the Mijn Etos APK before
building.** This is exactly the class of mistake the AH `client_id` incident was.

**Also note:** GraphQL introspection on `api.ah.nl` was **disabled in March 2026**
(reported in the jabbink gist comments), so you cannot introspect the Etos schema to
confirm the operations. And the legacy REST `/mobile-services/v1/receipts` is dead —
my 404 above independently confirms that claim.

---

## Gall & Gall — gall.nl

| field | |
| --- | --- |
| Retailer / domain | Gall & Gall (Ahold Delhaize) — `api.gall.nl` |
| Tier | `browser_once` |
| Auth | oauth authorization-code (same IdP software as Etos/AH) |
| Evidence | Same as Etos — no Gall-specific project exists; identity established by probe table above |
| Confidence | **CONFIRMED-from-source** for gateway identity; **UNVERIFIED** for `clientId` and receipt operations |
| Base URL | `https://api.gall.nl` |
| Order-history endpoint(s) | `POST /graphql` — same AH operations, unverified |
| Required headers | as Etos: `Authorization: Bearer <token>`, `Content-Type: application/json`, `Accept: application/json`, a UA |
| Money format | `{ amount, formatted }` — **UNVERIFIED** decimal vs cents |
| Bot protection | `www.gall.nl`: **Akamai Bot Manager** (`_abck`, `ak_bmsc`, `bm_ss`) + reCAPTCHA on page. `api.gall.nl`: **none observed** |
| Feasibility | **MEDIUM** — identical to Etos; build both behind one connector once the AH shape is proven |

`login.gall.nl/` redirects to the SFCC controller
`/on/demandware.store/Sites-gall-nl-Site/nl_NL/Login-OAuthLogin`, confirming the website
and the mobile API share one identity provider. Etos and Gall should be **one code path
with a brand parameter** (`api.{brand}.nl` + `login.{brand}.nl`), not two connectors.

---

## Kruidvat — kruidvat.nl

| field | |
| --- | --- |
| Retailer / domain | Kruidvat (A.S. Watson) — `www.kruidvat.nl`, `api.kruidvat.nl` |
| Tier | `none-found` (effectively BLOCKED) |
| Auth | SAP Commerce OAuth2 password grant at `/authorizationserver/oauth/token` — unreachable |
| Evidence | **none found** — no public reverse-engineering project for Kruidvat exists. Platform identified from my own probes |
| Confidence | CONFIRMED-from-source for the platform and the block; SPECULATIVE for anything behind it |
| Base URL | `https://api.kruidvat.nl` |
| Order-history endpoint(s) | SAP Commerce OCC convention `GET /api/v2/kvn/users/current/orders` — **prefix `/api/v2/` confirmed** from the Angular bundle (`prefix:"/api/v2/"`), baseSite uid `kvn` confirmed from the SSR state. Never reachable to verify |
| Required headers | unknown — could not get past the edge |
| Money format | unknown |
| Bot protection | **Akamai Bot Manager** (`_abck`, `ak_bmsc`, `bm_mi`, `bm_s`, `bm_so`, `bm_sz`) **plus an ASW waiting room** (`X-Waiting-Room-Access-Token`, a JWT with `"sub":"asw-waiting-room"`) |
| Feasibility | **BLOCKED** — every single path on `api.kruidvat.nl` returns Akamai `Access Denied` (`errors.edgesuite.net`) to a non-app client, including `/authorizationserver/oauth/token` and even a static `main-*.js` bundle |

It is a Spartacus (SAP Commerce Cloud Angular) storefront, so the OCC order API almost
certainly exists and would be lovely — `GET /api/v2/kvn/users/current/orders` returning
clean JSON with line items. You cannot reach it. The edge denies the API host outright
and the storefront additionally gates behind a waiting room. Defeating this needs
mobile-app TLS/JA3 fingerprint mimicry and Akamai sensor generation, which is a
multi-day project with a short shelf life. **Skip it.**

---

## Pets Place — petsplace.nl

| field | |
| --- | --- |
| Retailer / domain | Pets Place (Ijsvogel Retail) — `www.petsplace.nl` |
| Tier | `browser_once` |
| Auth | password via HTML form + `form_key` + **invisible** reCAPTCHA; cookie session afterwards |
| Evidence | **none found** — no public project. Platform and all endpoint facts from my own probes |
| Confidence | **CONFIRMED-from-source** for platform, which APIs are disabled, and the captcha config; UNVERIFIED for the logged-in page markup |
| Base URL | `https://www.petsplace.nl` |
| Order-history endpoint(s) | `GET /sales/order/history/` (HTML; 302s to `/customer/account/login/referer/<base64>` when signed out), detail at `GET /sales/order/view/order_id/{id}/`. Login posts to `/customer/account/loginPost` |
| Required headers | ordinary browser headers + session cookies; `form_key` must be taken from the login page and posted back |
| Money format | **decimal string in HTML**, Dutch formatting (`€ 12,34` — comma decimal separator). No JSON money field available |
| Bot protection | **invisible reCAPTCHA** on login, sitekey `6Ld94q0ZAAAAAPeUh1M5aBSQ5gpJ9ePCDMmkL2tK`, `size: "invisible"`, `badge: bottomright`. No Akamai/DataDome. New Relic RUM present |
| Feasibility | **MEDIUM** — a real browser passes invisible reCAPTCHA without any human interaction, so this is automatable tonight; but it is HTML parsing, so it is brittle |

**It is Magento 2 (Adobe Commerce), theme `ISM/ijsvogel`** — confirmed by a genuine
Magento REST error body: `GET /rest/V1/store/storeConfigs` → 401
`{"message":"The consumer isn't authorized to access %resources.","parameters":{"resources":"Magento_Backend::store"}}`.

**Both JSON routes are closed — I checked, do not assume otherwise:**
- `POST /graphql` → **404**. Magento GraphQL is not enabled. So the documented
  `customer { orders { ... } }` query, which is the normal way to do this on Magento, is
  unavailable.
- `GET /rest/V1/integration/customer/token` → **404** `{"message":"Verzoek komt niet overeen met een route."}`.
  The customer token endpoint is disabled, so there is no bearer-token path either.
- `GET /rest/V1/orders` → 401 requiring `Magento_Sales::actions_view`, an **admin** ACL.
  Not reachable with a customer identity even if a token existed.

That leaves the HTML storefront as the only route. The good news is the captcha is
invisible, so `browser_once` (log in with Playwright, keep the cookie jar, then fetch
subsequent pages with plain HTTP) is realistic.

---

## Zooplus — zooplus.nl

| field | |
| --- | --- |
| Retailer / domain | Zooplus — `www.zooplus.nl` |
| Tier | `browser_once` |
| Auth | **Keycloak OIDC** authorization-code, `client_id=shop-myzooplus-prod-zooplus`, `scope=openid` |
| Evidence | https://github.com/konnectors/zooplus — repo `pushed_at` 2026-07-24 but that is Renovate noise; **last change to `src/index.js` was 2020-05-04** ("fix: authentication change") |
| Confidence | **CONFIRMED-from-source** for the konnector's parsing approach and for the current OIDC redirect (my own probe); the konnector's login path is **confirmed stale** |
| Base URL | `https://www.zooplus.nl` (konnector targets `.fr`; the `.nl` site is the same application) |
| Order-history endpoint(s) | `GET /account/orders/overview` (HTML). Per-year URLs come from the `<option value>`s of `#year-selector-filter`. Rows are `.order-overview-table__row`. Invoice PDFs at `/account/orders/invoice...` |
| Required headers | `Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8` — the konnector has an explicit 2018 commit "fix: add missing Accept header, now needed by zooplus". Cookie jar required |
| Money format | **decimal string, comma separator, currency as trailing character** (e.g. `12,34 €`). The konnector does `parseFloat(raw.slice(0,-2).replace(',','.'))` and takes `slice(-1)` as the currency |
| Bot protection | **none seen** — istio-envoy + CloudFront, no Akamai/DataDome/captcha cookies on the login redirect |
| Feasibility | **MEDIUM** — unprotected and scrapeable, but the published connector is six years stale and gives you order *totals only*, no line items |

**The stale-lead correction.** The konnector authenticates by GETting
`https://www.zooplus.fr/web/sso/login` and regexing an `"actionUrl"` out of the HTML.
That path is gone. Today `GET /account/orders/overview` on `www.zooplus.nl` 302s to:

```
https://login.zooplus.nl/auth/realms/zooplus/protocol/openid-connect/auth
  ?response_type=code&client_id=shop-myzooplus-prod-zooplus
  &redirect_uri=https%3A%2F%2Fwww.zooplus.nl%2Fweb%2Fsso-myzooplus%2Flogin
  &state=<uuid>&login=true&ui_locales=nl-NL&scope=openid
```

Note `sso-myzooplus`, not `sso`. The konnector's Keycloak error-code handling
(`keycloak.login.error.invalid_credentials`) is still the right idea — it was Keycloak
in 2020 and it is Keycloak now — but the scraping of the login form must be rewritten.
The order-table selectors are also 2018-era and unverified against the current markup.

**Scope limit worth knowing up front:** the overview page yields date, total, order
number and a PDF link — **no line items**. Line items would require parsing the invoice
PDF or an order-detail page the konnector never touched.

---

## Recommended build order (value × feasibility)

1. **Picnic** — EASY, CONFIRMED end to end, cents integers, no bot protection, real
   line items. Nothing else here is close. Build tonight.
2. **Etos + Gall & Gall as one brand-parameterised connector** — MEDIUM. Highest
   *upside* on the list because the transport is a clean unprotected JSON gateway and
   two retailers fall out of one implementation. Gate the work behind one cheap
   experiment: pull `clientId` from the Mijn Etos APK, do one `/graphql` call, and see
   whether `posReceiptsPage` resolves. If it does, this is nearly free. If it doesn't,
   stop — do not start scraping `www.etos.nl`, it is behind Akamai.
3. **Pets Place** — MEDIUM. Automatable with Playwright tonight (invisible reCAPTCHA
   needs no human), but it is HTML parsing with decimal-comma money and will break.
   Lower value than the above.
4. **Zooplus** — MEDIUM but **lower value than its feasibility suggests**: totals only,
   no line items, and the one public connector needs a rewrite rather than a port. Do it
   only if order totals without lines are still worth ingesting.

## Do not attempt

- **Kruidvat.** Akamai Bot Manager plus an A.S. Watson waiting room, and `api.kruidvat.nl`
  returns `Access Denied` at the Akamai edge on *every* path — I could not even fetch a
  static JavaScript bundle. There is no public reverse-engineering work to stand on. The
  SAP Commerce OCC order endpoint behind it is genuinely nice and you will not reach it.
  This is precisely the "waste of a night" case.
- **`www.etos.nl` and `www.gall.nl` as browser scrapes.** Both are Salesforce Commerce
  Cloud behind Akamai Bot Manager (`_abck`/`bm_sz`) with reCAPTCHA on the login page.
  The order page is `/on/demandware.store/Sites-{etos-nl|gall-nl}-Site/nl_NL/Order-History`
  and it 302s to an OAuth login. Going through the website throws away the single biggest
  advantage you have here — that `api.etos.nl` is *not* bot-protected. Use the API host.
- **Magento GraphQL / REST customer-token on Pets Place.** Both return 404; they are
  switched off. Don't spend time on the "documented Magento API" — it isn't there.
- **`MikeBrink/python-picnic-api` as your reference implementation.** 2023, no 2FA,
  stale agent strings. It is what Home Assistant uses and it will mislead you.

## Claim ledger

CONFIRMED-from-source (I read the code or observed the HTTP response myself):
Picnic base URL, API version 15, login body and MD5, `x-picnic-auth` response-header
mechanism, 2FA endpoints, delivery endpoints, cents money format; the ah.nl/etos.nl/gall.nl
gateway-identity probe table; AH token endpoints and GraphQL receipt operations; the
`login.*.nl` client_id 302/400 discriminator results; Kruidvat's Akamai denial, `/api/v2/`
prefix and `kvn` baseSite; Pets Place being Magento with GraphQL and customer-token both
404 and an invisible reCAPTCHA sitekey; Zooplus's current Keycloak redirect and the
konnector's 2020-05-04 last logic commit.

UNVERIFIED (claim read, not proven): that `posReceiptsPage`/`posReceiptDetails` resolve on
`api.etos.nl`/`api.gall.nl`; the correct `clientId` for the Etos and Gall apps; whether AH's
money `amount` is decimal or cents; that AH disabled GraphQL introspection in March 2026;
Zooplus's current order-table CSS selectors.

SPECULATIVE: anything about Kruidvat's actual order payload.
