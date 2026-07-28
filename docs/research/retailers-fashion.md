# Retailer research — Fashion & Beauty (NL)

Researcher slug: `fashion`
Date: 2026-07-28
Scope: Zalando, Zalando Lounge, H&M, About You, Shoeby, CoolCat, G-Star RAW, Suitsupply,
Scotch & Soda, Omoda, Van Haren, Rituals, Douglas, ICI PARIS XL, de Bijenkorf, Bonprix, Otto.

**Rules observed:** no logins, no accounts, no credentials touched, no writes. All probes were
unauthenticated GET/HEAD requests to public URLs. No credentials were ever transmitted — including
to login endpoints, where I sent GET only to test *route existence*, never POST. No files changed
outside this one.

---

## Executive summary — read this before anything else

Three structural findings that matter more than any individual table below.

**1. There is no public reverse-engineering work on fashion order history. Anywhere. For any of these 17.**
This is not a gap in my search; it is a property of the market. The Home Assistant ecosystem — which
is the single richest source of reverse-engineered Dutch retailer APIs — covers Albert Heijn and
Jumbo because *groceries have a recurring-delivery use case worth automating*. Nobody builds a
dashboard for "when does my H&M parcel arrive." Consequently every project I found for these
retailers is one of three things, none of which is order history:
  - a **product catalogue** scraper (Apify, Stevesie, RapidAPI/apidojo, ShoppingScraper),
  - a **merchant/seller** integration (Zalando zDirect, Otto Market, SCAYLE, SFCC OCAPI),
  - a **checkout bot** (AIO sneaker bots).
The checkout bots turned out to be the only genuinely useful source, because they are the only
category that authenticates as a real customer.

**2. Two named traps that will cost a day each if you hit them.** Both are exactly the
README-vs-code failure mode this project has already been bitten by three times:
  - **"Zalando orders API"** → every search result is the **zDirect / Partner / Order Event API**,
    which returns orders where *you are the seller on the marketplace*. It is not, and cannot be
    made into, the signed-in shopper's own purchase history. `thoreg/m13`'s
    `src/zalando/services/orders.py` and `src/otto/services/orders.py` are both this. CONFIRMED by
    reading the source: it processes OEA (Order Event API) seller webhooks with `store_id` and
    Connected Retail fulfilment fields.
  - **"Rituals API"** → every result is the **Perfume Genie smart diffuser**, an IoT product.
    CONFIRMED by reading `milanmeu/pyrituals` source: base URL is
    `https://rituals.apiv2.sense-company.com`, endpoints `/apiv2/account/token`, `/apiv2/account/hubs`,
    `/apiv2/hubs/{hub}/sensors/{sensor}`. It reports perfume fill level and battery. It has zero
    relationship to rituals.com the webshop. There is an HA integration, a Node-RED package, a .NET
    wrapper and custom firmware for it — all diffuser, all useless here.

**3. The actual opportunity is not the two "valuable" retailers — it is the Salesforce Commerce Cloud cluster.**
While sweeping platforms I confirmed that **Shoeby, Omoda and Suitsupply all run Salesforce Commerce
Cloud (Demandware)**. SFCC is a documented, uniform, publicly-specified platform with predictable
order-history routes and weak bot defence. One connector implementation covers all three, and would
extend to any other SFCC retailer added later. Details and the client_id caveat are in the SFCC
playbook section. **Scotch & Soda is Shopify**, which is similarly uniform.

Blunt verdict on the brief's premise: **Zalando and H&M are the valuable ones and they are also the
two you should not build tonight.** Both sit behind Akamai Bot Manager, both returned 403 to a
plain request, and the only project that successfully logs into Zalando does so by *paying a
third-party sensor-generation service*. Build the SFCC cluster instead — lower value per retailer,
but three retailers for one implementation and a near-certain result.

### Preference-order outcome across all 17

| Tier | Count | Retailers |
| --- | --- | --- |
| 1 — documented API (customer order history) | **0** | none. Not one. |
| 2 — mobile app API via existing public project | **0** | none found for any of the 17 |
| 3 — browser | 14 | everything worth attempting |
| none-found / not worth it | 3 | CoolCat, Zalando Lounge, Van Haren |

---

## Priority retailers

### Zalando

| field | |
| --- | --- |
| Retailer / domain | Zalando — `www.zalando.nl`, auth on `accounts.zalando.com` |
| Tier | `browser_interactive` |
| Auth | password (email+password) via SSO host; cookie session + XSRF token |
| Evidence | `Dante-v2/PhoenixAIO` `scripts/zalando.py`, last push **2025-02-07**; `giaco8020/Site-Module-Python-Bot` `zalando.py`, last push **2024-06-30**. Both read in full. Zalando's own `zalando/shop-api-documentation` is **archived 2018-08-03** and was catalogue-only. |
| Confidence | CONFIRMED-from-source for login/cart/checkout endpoints and the Akamai requirement. **SPECULATIVE for order history — no public project retrieves it.** |
| Base URL | `https://www.zalando.nl` (storefront), `https://accounts.zalando.com` (auth) |
| Order-history endpoint(s) | **No JSON endpoint is published anywhere.** HTML route `GET /mijn-account/bestellingen` CONFIRMED to exist — it returns Zalando's own rendering-engine HTML shell under a 403 bot block, not a 404. Login is `POST https://accounts.zalando.com/api/login` (PhoenixAIO, 2025) and `POST /api/reef/login` (2024 repo) — the auth host moved between the two, so treat `accounts.zalando.com` as current. |
| Required headers | `x-xsrf-token: <value of the frsx cookie>` on state-changing calls (CONFIRMED from source); otherwise full genuine browser header set. Akamai `_abck` + `bm_sz` cookies mandatory. |
| Money format | unknown — no public capture of an order payload exists |
| Bot protection | **Akamai Bot Manager — CONFIRMED, three independent signals.** `HEAD /` → `HTTP 403`, `server: AkamaiNetStorage`. `GET /api/reef/login` → `HTTP 000` (connection dropped with no response = the documented Akamai TLS-fingerprint silent drop). PhoenixAIO implements the full `_abck` sensor loop and **outsources payload generation to `https://ak01-eu.hwkapi.com/akamai/generate`**, retrying while the cookie contains the failure marker `~-1~||-1||~-1`. |
| Feasibility | **HARD** — highest-value retailer on the list, but you must defeat Akamai sensor validation *and* then discover the order endpoint yourself, because nobody has published it. |

Notes. The checkout-bot repos are low-star throwaway accounts, so treat them as leads about
*structure*, not guarantees about *current* behaviour — but the two independent repos a year apart
agree on the `/api/...` JSON surface and on Akamai, which is why I rate those CONFIRMED. Neither
touches order history, because a checkout bot has no reason to. A real Playwright session will get
through where curl does not, since the sensor JS runs for free in a genuine browser; the cost is
that Zalando is `browser_interactive` forever — you cannot cache a session and go headless-HTTP.

### H&M

| field | |
| --- | --- |
| Retailer / domain | H&M — `www2.hm.com/nl_nl` |
| Tier | `browser_interactive` |
| Auth | password; H&M Club membership account. Unknown whether OTP is enforced on new devices — **UNVERIFIED**, I did not log in. |
| Evidence | **none found for order history.** Catalogue-only work: Stevesie, Apify (`misceres/h-m-scraper`), RapidAPI `apidojo/hm-hennes-mauritz`, `Diggernaut/configs` (marked *Legacy*), `fadi426/H-M-API` (a student rebuild, not H&M's API). |
| Confidence | CONFIRMED-from-source that no order endpoint is public; UNVERIFIED for anything account-side |
| Base URL | `https://www2.hm.com` (storefront). `api.hm.com` resolves and returns `HTTP 200` with the bare body `api.hm.com` — a health/placeholder response, no documentation, no discoverable routes. |
| Order-history endpoint(s) | none published. The only documented H&M endpoint anywhere is `GET /hmwebservices/service/products/search/hm-{country}/Online/{language}` — **products, not orders**. |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | **Akamai — CONFIRMED.** `HTTP 403`, `server: AkamaiGHost`, and it sets `akavpau_www2_nl_nl`, `akainst`, `akamref`, plus `akamai-grn` and `server-timing: ak_p` response headers. |
| Feasibility | **HARD** — Akamai, and zero prior art of any kind on the account side. |

Legitimate alternative worth flagging to the owner: H&M operates a **GDPR data-access self-service**
(`/customer-service/requestmyreport.html`) whose standard package explicitly includes *online
order-related information and in-store purchases linked to membership*, delivered as a
password-protected ZIP. That is a supported, non-adversarial path to the same data. It is manual,
email-delivered and has days of latency, so it is no basis for a live connector — but if the goal is
backfilling a user's history once, it beats fighting Akamai. Flag as a product decision, not an
engineering one.

---

## The SFCC cluster — the actual recommendation

All three confirmed by two independent signals: Salesforce Commerce Cloud session cookies
(`dwsid`, `dwanonymous_*`, `dwac_*`, `cqcid`, `usid_*`) on the storefront, **and** a live OCAPI
endpoint that answers `MissingClientIdException` rather than `SiteNotFoundException` — which proves
the site ID is real. I verified the negative case too: `Sites-Shoeby` returned
`SiteNotFoundException`, so the check genuinely discriminates.

### Shoeby

| field | |
| --- | --- |
| Retailer / domain | Shoeby — `www.shoeby.nl` |
| Tier | `browser_once` |
| Auth | password → SFCC `dwsid` cookie session |
| Evidence | no third-party repo; **direct verification against the live site, 2026-07-28** |
| Confidence | **CONFIRMED-from-source** (platform, site ID, order route). UNVERIFIED for payload shape — behind login. |
| Base URL | `https://www.shoeby.nl` |
| Order-history endpoint(s) | `GET /orders` — CONFIRMED as the canonical route: the SFRA controller URL `/on/demandware.store/Sites-NL-Site/nl_NL/Order-History` `301`s to it. SFCC site ID is **`NL`**. OCAPI form (needs client_id, see caveat): `GET /s/NL/dw/shop/v23_2/customers/{customer_id}/orders` |
| Required headers | storefront: `Cookie: dwsid=...` + browser UA. OCAPI: `x-dw-client-id: <client_id>`, `Authorization: Bearer <JWT>` |
| Money format | SFCC standard — JSON `decimal` number in `order_total` / `product_total` plus a separate `currency` string. **UNVERIFIED for these sites specifically**; this is the platform's documented shape. |
| Bot protection | Cloudflare `__cf_bm` only — the ordinary bot-management cookie, **no challenge issued**. Site returned `HTTP 200` to a plain scripted request. |
| Feasibility | **EASY** — predictable platform, clean route, no challenge on first contact. |

### Omoda

| field | |
| --- | --- |
| Retailer / domain | Omoda — `www.omoda.nl` |
| Tier | `browser_once` |
| Auth | password → `dwsid` cookie session |
| Evidence | direct verification against the live site, 2026-07-28 |
| Confidence | **CONFIRMED-from-source** (platform, site ID, order route) |
| Base URL | `https://www.omoda.nl` |
| Order-history endpoint(s) | `GET /account/orders/` — CONFIRMED, `/on/demandware.store/Sites-omoda-nl-Site/nl_NL/Order-History` `301`s to it. Site ID **`omoda-nl`**. OCAPI form: `GET /s/omoda-nl/dw/shop/v23_2/customers/{customer_id}/orders` |
| Required headers | `Cookie: dwsid=...` + browser UA |
| Money format | SFCC standard decimal + currency string — UNVERIFIED for this site |
| Bot protection | Cloudflare `__cf_bm`, no challenge. `HTTP 200` to a plain request; the full SFCC cookie set was issued to a scripted client without complaint. |
| Feasibility | **EASY** |

### Suitsupply

| field | |
| --- | --- |
| Retailer / domain | Suitsupply — `suitsupply.com/nl-nl` |
| Tier | `browser_once` |
| Auth | password → `dwsid` cookie session |
| Evidence | direct verification against the live site, 2026-07-28 |
| Confidence | **CONFIRMED-from-source** (platform, site ID). Order route UNVERIFIED — not probed. |
| Base URL | `https://suitsupply.com` |
| Order-history endpoint(s) | site ID **`INT`** confirmed via OCAPI and 16 `Sites-INT-Site` references in page HTML. Expect `/on/demandware.store/Sites-INT-Site/{locale}/Order-History` and a rewritten account route, by direct analogy with the two above. |
| Required headers | `Cookie: dwsid=...` + browser UA |
| Money format | SFCC standard decimal + currency string — UNVERIFIED |
| Bot protection | Cloudflare `__cf_bm` + a Vercel edge layer (`platform=vercel` cookie); **reCAPTCHA present in page source** — likely on the login form, so budget for it. |
| Feasibility | **MEDIUM** — same easy platform as the others, marked down only for the reCAPTCHA on the auth step. |

### SFCC playbook, and the one caveat that matters

Do this once and it covers all three retailers, plus any SFCC retailer added later.

The tempting path is OCAPI: `POST /s/{site}/dw/shop/v23_2/customers/auth` with `type=credentials`
and HTTP Basic credentials, which returns a JWT in the **`Authorization` response header**, then
`GET /s/{site}/dw/shop/v23_2/customers/{customer_id}/orders`. That is a genuinely documented API and
it would be Tier 1.

**Do not plan around it.** OCAPI requires an `x-dw-client-id` that the retailer provisions in
Business Manager and grants resource permissions to. I probed all three sites and every one returned
`MissingClientIdException` — **I did not find a usable client_id for any of them, and none is
exposed in the storefront JS**, because all three run server-rendered SFRA, which does not call
OCAPI from the browser at all. Assume you cannot get one. Two further cautions: Salesforce
deprecated OCAPI in April 2026 and it is maintenance-only (UNVERIFIED — from Salesforce docs
summaries, I did not confirm against a dated release note), and even with a client_id the
`customers` resource must be separately allow-listed by the retailer.

So the real design is `browser_once`: Playwright logs in once, you capture the `dwsid` cookie, and
from then on plain HTTP GETs against the account route work for the life of that session. The SFRA
order pages are server-rendered HTML, so parse HTML — do not expect JSON. `dwsid` is `HttpOnly`
and `Secure`; store it with the same care as a credential.

---

## Quick sweep — the remaining eleven

### Scotch & Soda

| field | |
| --- | --- |
| Retailer / domain | Scotch & Soda — `www.scotch-soda.com` → redirects to **`www.scotchandsoda.com`** |
| Tier | `browser_once` |
| Auth | password → Shopify customer session |
| Evidence | direct verification 2026-07-28: 140 `shopify` + 21 `cdn.shopify` references in page source |
| Confidence | **CONFIRMED-from-source** (platform + canonical domain + route) |
| Base URL | `https://www.scotchandsoda.com` |
| Order-history endpoint(s) | `GET /account/orders` — CONFIRMED, standard Shopify. Shopify also has a documented **Customer Account API** (GraphQL), but it is scoped to the merchant's own app and cannot be used by a third party, so it is not a Tier-1 route for us. |
| Required headers | Shopify customer session cookie + browser UA |
| Money format | Shopify — `decimal` string with a separate currency code |
| Bot protection | **hCaptcha and reCAPTCHA both present** in page source; Shopify's own edge protection |
| Feasibility | **MEDIUM** — uniform, well-understood platform; the captcha on login is the only real cost, and the domain move is a trap if you hardcode `scotch-soda.com`. |

### G-Star RAW

| field | |
| --- | --- |
| Retailer / domain | G-Star RAW — `www.g-star.com/nl_nl` |
| Tier | `browser_interactive` |
| Auth | password — UNVERIFIED |
| Evidence | none found |
| Confidence | CONFIRMED-from-source for the protection layer only |
| Base URL | `https://www.g-star.com` |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | **Akamai Bot Manager — CONFIRMED.** Sets an `akbot` cookie and returns `server-timing: ak_p`. Front end is Next.js (`__NEXT_DATA__`). |
| Feasibility | **HARD** — Akamai, no prior art, mid-tier value. |

### About You

| field | |
| --- | --- |
| Retailer / domain | About You — `www.aboutyou.nl` |
| Tier | `browser_interactive` |
| Auth | password — UNVERIFIED |
| Evidence | `scayle/storefront-api-ts-sdk` (last push 2026-04-10) and siblings — **merchant-side, wrong direction, see trap note** |
| Confidence | CONFIRMED-from-source for platform and protection; SPECULATIVE for anything account-side |
| Base URL | `https://www.aboutyou.nl` |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | SCAYLE APIs use integer **cents** — UNVERIFIED for the customer surface |
| Bot protection | Cloudflare (`__cf_bm`, `302` to a scripted client) + **hCaptcha referenced in page source** |
| Feasibility | **HARD** |

Trap note: About You built and spun out **SCAYLE**, and the SCAYLE SDKs are actively maintained and
look highly promising. They are for *operating a shop on SCAYLE* — they authenticate with a shop
access token the merchant holds. There is no path from those SDKs to reading your own aboutyou.nl
purchases. Same shape of mistake as the Zalando zDirect trap.

### Douglas

| field | |
| --- | --- |
| Retailer / domain | Douglas — `www.douglas.nl` |
| Tier | `browser_interactive` |
| Auth | password (Douglas Beauty Card) — UNVERIFIED |
| Evidence | none found |
| Confidence | CONFIRMED-from-source for protection only |
| Base URL | `https://www.douglas.nl` |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | **Akamai — CONFIRMED.** Sets `akavpau_VP-NL`; returned `HTTP 400` to a plain request and `HTTP 400` on the localised path. |
| Feasibility | **HARD** — genuinely high value (frequent, high-margin repeat purchases) but Akamai with zero prior art. |

### ICI PARIS XL

| field | |
| --- | --- |
| Retailer / domain | ICI PARIS XL — `www.iciparisxl.nl` |
| Tier | `browser_interactive` |
| Auth | password — UNVERIFIED |
| Evidence | none found |
| Confidence | CONFIRMED-from-source for protection only |
| Base URL | `https://www.iciparisxl.nl` |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | **Akamai — CONFIRMED.** `HTTP 403`, `server: AkamaiGHost`. |
| Feasibility | **HARD** — same Douglas Group stack as Douglas; if you ever crack one, try the other immediately. |

### de Bijenkorf

| field | |
| --- | --- |
| Retailer / domain | de Bijenkorf — `www.debijenkorf.nl` |
| Tier | `browser_interactive` |
| Auth | password — UNVERIFIED |
| Evidence | none found |
| Confidence | CONFIRMED-from-source for protection only |
| Base URL | `https://www.debijenkorf.nl` |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | **Cloudflare with an active managed challenge — CONFIRMED.** `HTTP 403` plus `server-timing: chlray` — the `chlray` (challenge ray) token means a challenge was actually *served*, which is materially more hostile than the passive `__cf_bm` seen on Shoeby/Omoda. |
| Feasibility | **HARD** — high basket values make this attractive, but it actively challenges. Cloudflare challenges do yield to a real browser more readily than Akamai sensor validation, so rank it above the Akamai group if you ever revisit. |

### Bonprix

| field | |
| --- | --- |
| Retailer / domain | Bonprix — `www.bonprix.nl` (Otto Group) |
| Tier | `browser_once` |
| Auth | password → `__Host-PSESSIONID` cookie session, with a CSRF token |
| Evidence | direct verification 2026-07-28 |
| Confidence | CONFIRMED-from-source for the session/CSRF mechanics; order route UNVERIFIED (`/mijn-account/` returned 404, so the path differs) |
| Base URL | `https://www.bonprix.nl` |
| Order-history endpoint(s) | not established — needs 10 minutes with a browser to find the real account path |
| Required headers | `Cookie: __Host-PSESSIONID=...`; a `csrf-token-ssr` cookie is issued and its value must be echoed on state-changing calls |
| Money format | unknown |
| Bot protection | **none seen** — `HTTP 200` to a plain scripted request, no CDN bot layer, no captcha in source. Technically the softest target of all 17. |
| Feasibility | **EASY technically / LOW value** — the `__Host-` cookie prefix and explicit CSRF token indicate a competently built, conventional session app. Nothing stands in your way. Almost nobody in the target demographic shops here. |

### Otto

| field | |
| --- | --- |
| Retailer / domain | Otto — `www.otto.nl` |
| Tier | `browser_once` |
| Auth | password → `SESSIONID` cookie |
| Evidence | direct verification 2026-07-28. `thoreg/m13` `src/otto/services/orders.py` is the **seller-side Otto Market API — trap, not usable.** |
| Confidence | CONFIRMED-from-source for session and protection; order route UNVERIFIED |
| Base URL | `https://www.otto.nl` |
| Order-history endpoint(s) | not established. `/mijn-otto` returned `403`, so the account area is guarded even though the homepage is not. |
| Required headers | `Cookie: SESSIONID=...` |
| Money format | unknown |
| Bot protection | light — homepage `200` on a plain request, `server: istio-envoy`; **reCAPTCHA present** in source and the account path 403s |
| Feasibility | **MEDIUM technically / LOW value** — before spending anything here, confirm Otto still operates a Dutch consumer storefront at meaningful scale. I did not verify its NL market position and would not assume it. |

### Van Haren

| field | |
| --- | --- |
| Retailer / domain | Van Haren — `www.vanharen.nl` (Deichmann group) |
| Tier | `browser_interactive` |
| Auth | unknown |
| Evidence | none found |
| Confidence | UNVERIFIED |
| Base URL | `https://www.vanharen.nl/nl-nl/` (root `301`s to the locale path) |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | Cloudflare `__cf_bm`, no challenge seen |
| Feasibility | **MEDIUM / LOW value** — client-rendered SPA with no platform fingerprint I could detect, so every route must be discovered by hand for a low-value retailer. |

### Zalando Lounge

| field | |
| --- | --- |
| Retailer / domain | Zalando Lounge — `www.lounge.nl` |
| Tier | `none-found` |
| Auth | password, members-only — UNVERIFIED |
| Evidence | none found |
| Confidence | UNVERIFIED |
| Base URL | `https://www.lounge.nl` |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | none seen at the edge (`server: openresty`), but the entire site is behind a members-only login so nothing is observable without an account |
| Feasibility | **BLOCKED tonight** — a wholly separate stack from Zalando main (openresty, not Akamai), which is mildly encouraging, but it is closed-door by design: **nothing can be established without logging in, which I will not do.** Do not confuse this with Zalando proper; the session is not shared. |

### CoolCat

| field | |
| --- | --- |
| Retailer / domain | CoolCat — `www.coolcat.nl` |
| Tier | `none-found` |
| Auth | unknown |
| Evidence | none found |
| Confidence | UNVERIFIED |
| Base URL | `https://www.coolcat.nl` |
| Order-history endpoint(s) | not established |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | none seen (`server: Apache`, `301`) |
| Feasibility | **EASY but pointless** — an unremarkable Apache stack, no protection, and no detectable platform. The blocker is value, not difficulty: CoolCat is a fraction of its former size following its 2020 collapse (business context, UNVERIFIED). Lowest value on the list. |

### Rituals

| field | |
| --- | --- |
| Retailer / domain | Rituals — `www.rituals.com/nl-nl` |
| Tier | `browser_interactive` |
| Auth | password — UNVERIFIED |
| Evidence | **`milanmeu/pyrituals` (last push 2025-09-01) is the Perfume Genie diffuser, NOT the shop — CONFIRMED by reading the source.** Also `florianleon/node-red-contrib-rituals` (2025-12-05), `appspark-nl/rituals-api-net` (2019), `martijnrenkema/Rituals-diffuser` (2026-07-06). All diffuser. |
| Confidence | CONFIRMED-from-source that all existing work is the wrong product; nothing established for the webshop |
| Base URL | `https://www.rituals.com` |
| Order-history endpoint(s) | not established. `/nl-nl/account/orders` returns `200` but only an SPA shell with a GTM bootstrap — routing is client-side, so the `200` proves nothing about the route. |
| Required headers | unknown |
| Money format | unknown |
| Bot protection | none seen at the edge — no bot cookies, no captcha in the shell |
| Feasibility | **MEDIUM** — genuinely good value (high repeat-purchase frequency, strong NL presence) and no visible edge defence. The cost is that it is a client-rendered SPA with no reusable platform fingerprint, so every endpoint must be discovered by hand with devtools. Worth a look *after* the SFCC cluster. |

---

## Recommended build order (value × feasibility)

1. **The SFCC connector — Shoeby + Omoda, then Suitsupply.** One implementation, three retailers,
   all three CONFIRMED down to the site ID and (for the first two) the exact order route. No
   challenge on first contact. This is the only item here with a near-certain outcome tonight, and
   it is reusable infrastructure rather than a one-off. Start here even though no single one of
   these three is a "valuable" retailer — three-for-one changes the arithmetic.
2. **Scotch & Soda (Shopify).** Second uniform-platform win, and the second connector shape worth
   owning since Shopify is everywhere. Budget for the login captcha and use `scotchandsoda.com`.
3. **Rituals.** Best value-per-effort of the unprotected singles: high purchase frequency, no edge
   defence, but all discovery is manual. A good second-session task.
4. **Bonprix.** Do it when you want a guaranteed, frictionless win — genuinely zero bot protection.
   Low value; treat it as a smoke test for the generic session-based connector rather than as a
   feature.
5. **de Bijenkorf.** High basket value, and Cloudflare challenges are far more tractable with a real
   browser than Akamai sensors. The best of the genuinely-defended targets. Attempt only with a full
   Playwright session and realistic expectations.
6. **Zalando.** The most valuable retailer on the list, and worth building properly *eventually* —
   but it needs a dedicated session with a real browser, and you should expect to discover the order
   endpoint yourself. Not a night's work.

## Do not attempt

- **H&M, Douglas, ICI PARIS XL, G-Star RAW** — Akamai Bot Manager with **zero public prior art on
  the account side**. This is the exact combination the brief calls a waste of a night, and it
  applies to four retailers here. Akamai's sensor validation is the hardest layer on the list; a
  2025 project that beats it does so by paying an external service to generate the payload. Revisit
  only with a full browser stack and a specific reason. For H&M, raise the GDPR data-export route
  with the owner first — it may satisfy the requirement without any of this.
- **Zalando tonight.** Same reasoning, minus the "not worth it" — it *is* worth it, later, with a
  browser. Building an HTTP client against it tonight would be building the wrong thing, which is
  precisely the failure this research exists to prevent.
- **About You** — Cloudflare plus hCaptcha, and its only promising-looking prior art (SCAYLE) points
  the wrong way. Poor odds.
- **Zalando Lounge** — nothing whatsoever can be established without an account, and I will not
  create one. Not a judgement that it is hard; it is simply unresearchable under the rules. Revisit
  only when the owner can log in.
- **CoolCat** — technically the easiest target on the list and still not worth an hour. Too small
  to matter.
- **Van Haren** — full manual SPA discovery for a low-value retailer. Poor value-per-hour.
- **Any repo named `<retailer>-api` without reading its source first.** Of the ~15 promising repos
  I opened, **every single one** was catalogue data, merchant/seller data, or a different product
  entirely. The hit rate on READMEs for this vertical is zero.

## Loose ends for whoever picks this up

- Suitsupply's exact account route is unverified — I confirmed platform and site ID `INT` but did
  not probe the order path. Ten minutes.
- Bonprix and Otto account paths are unverified (`/mijn-account/` 404s, `/mijn-otto` 403s).
- The SFCC money-format claim (decimal + separate currency string) is the documented platform shape,
  not something I observed on these three sites — it sits behind login. Verify on first capture.
- Whether Zalando/H&M enforce OTP or device verification on a fresh login is **unknown**, and it is
  the single biggest risk to the `browser_once` model for every retailer here. Nothing in this
  research could establish it without logging in. Assume `browser_interactive` until proven
  otherwise, for all 17.
