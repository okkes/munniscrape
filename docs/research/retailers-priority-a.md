# Retailer research — Priority A (marketplaces): bol.com, amazon.nl

Researcher slug: `priority-a`
Date: 2026-07-28
Scope: read-only research. No logins, no accounts, no credentials used. Only public pages
and public repositories were fetched. No code written, no files changed outside this one.

**Headline answer to the two questions asked:**

1. **bol.com's documented API (`api.bol.com/retailer`) does NOT serve a consumer's own order
   history. It is seller-only.** CONFIRMED. The grant type is `client_credentials` — there is
   no user-delegated flow at all, so a shopper's identity cannot even be expressed in the
   protocol. `GET /retailer/orders` returns orders placed *with* a marketplace seller.
   Building against it would produce an adapter that reads the wrong person's data.
2. **amazon.nl is browser-or-nothing.** CONFIRMED. Three independent, currently-maintained
   projects all scrape HTML; none of them found a JSON consumer API. Amazon additionally runs
   AWS WAF JS challenges and the ACIC visual-puzzle challenge on the auth path.

---

## bol.com

| field | |
| --- | --- |
| Retailer / domain | bol.com — `www.bol.com` (consumer), `login.bol.com` (auth), `api.bol.com` (seller API, **not usable**) |
| Tier | `browser_interactive` for first link → `browser_once` for refresh. **Not `http`.** |
| Auth | password (HTML form POST `j_username` / `j_password`) → `JSESSIONID` cookie session. reCAPTCHA site key present on the login page. **Not** oauth+pkce. |
| Evidence | Consumer side: no public project exists — **none found** (see "What I searched" below). Seller side (for contrast, CONFIRMED but wrong-purpose): [qualityangel/bol-api-v10 `client.py`](https://github.com/qualityangel/bol-api-v10) last commit 2024-05-19; [picqer/bol-retailer-php-client](https://github.com/picqer/bol-retailer-php-client); [BartWaardenburg/bol-mcp](https://github.com/BartWaardenburg/bol-mcp) 2026-03-05. Official docs: `https://api.bol.com/retailer/public/Retailer-API/authentication.html`. |
| Confidence | Seller-API-is-not-consumer: **CONFIRMED-from-source**. Login mechanics: **CONFIRMED** (direct fetch of the public login page). Post-login order-page shape: **UNVERIFIED** (cannot see it without authenticating). |
| Base URL | Consumer: `https://www.bol.com` · Auth: `https://login.bol.com` · Seller API (do not use): `https://api.bol.com/retailer` |
| Order-history endpoint(s) | Consumer order list page: `GET https://www.bol.com/nl/nl/rnwy/account/bestellingen` (`rnwy` = "runway", bol's account app). Unauthenticated it returns **302 → `/nl/account/login.html?redirectUrl=/nl/nl/rnwy/account/bestellingen` → `https://login.bol.com/wsp/login`** (observed directly). No JSON order endpoint has been identified. For contrast, the seller endpoint is `GET https://api.bol.com/retailer/orders` — **wrong data, do not build on it.** |
| Required headers | Login page + orders page: ordinary browser headers; nothing exotic observed. Session carried by cookies `JSESSIONID`, `XSC`, `shopping_session_id`, `BUI`, `bltgSessionId`; a CSRF cookie `XSRF-TOKEN` is issued (HttpOnly on the account path). Seller API (not usable): `Authorization: Bearer <jwt>`, `Accept: application/vnd.retailer.v10+json`, `Content-Type: application/vnd.retailer.v10+json`. |
| Money format | UNKNOWN for the consumer order page (not observed). Assume Dutch-rendered decimal string with **comma as decimal separator** (`€ 12,50`) and dot as thousands separator — plan the parser for that from day one. Seller API uses decimal `totalPrice` / `unitPrice` (irrelevant here). |
| Bot protection | **No Akamai Bot Manager, no DataDome, no Cloudflare.** Confirmed by response headers: no `_abck`, no `bm_sz`, no `datadome` cookie on either the homepage or the account path. Akamai appears only as **mPulse RUM** (`c.go-mpulse.net`, `ds-aksb-a.akamaihd.net`, `*.akstat.io` in CSP) — performance telemetry, not a bot defence. **reCAPTCHA is configured at login**: site key `6Le4qaQsAAAAAFHTGTckpy4WkoCXpw9JJ8NgBtpk` is embedded in the login page's Next.js config blob. No reCAPTCHA `<script>` is emitted on initial server render, which suggests risk-based / invisible invocation rather than always-on — **UNVERIFIED**. |
| Feasibility | **MEDIUM** — a soft, undefended site (Java/Spring session cookies, no enterprise bot vendor) but the login is a JS-driven Next.js app with a reCAPTCHA key on hand, so the sign-in must happen in a real browser; after that, cookie reuse is very likely to work. |

### bol.com — detail

**Why the documented API is the wrong answer (CONFIRMED).**
`https://api.bol.com/retailer` authenticates with `POST https://login.bol.com/token`,
`grant_type=client_credentials`. Read from source in `qualityangel/bol-api-v10`'s `client.py`:

```python
_DEFAULT_BASE_URL = "https://api.bol.com/retailer"
_DEMO_BASE_URL    = "https://api.bol.com/retailer-demo"
payload = {'client_id': ..., 'client_secret': ..., 'grant_type': 'client_credentials'}
self.token = self.session.post(url='https://login.bol.com/token', data=payload).json()['access_token']
self.session.headers.update({'Accept': 'application/vnd.retailer.v10+json', ...})
```

`client_credentials` carries **no user context**. There is no `authorization_code` flow, no
PKCE, no consumer consent screen. Credentials are issued only via the Seller Dashboard (SDD)
to registered business partners. So the API can only ever answer "which orders were placed
with *this seller*", never "what did *this shopper* buy".

**README-vs-code trap, matching this project's existing scar tissue.**
`qualityangel/bol-api-v10` is described as *"a smart api that gets the orders from your
bol.com account and let them see in a UI"* — which reads exactly like consumer order history.
The source is `client_credentials` against the Retailer API. It is a seller tool. Anyone
skim-reading GitHub descriptions will pick this repo up and build the wrong adapter.

**Consumer login mechanics (CONFIRMED by fetching the public page).**
`https://login.bol.com/wsp/login` — a Next.js app served under `/wsp/`
(`/wsp/_next/static/chunks/*.js`, turbopack build). The server-rendered form is:

- `method="POST"`, **no `action` attribute** → posts to `/wsp/login` itself
- `<input type="email" name="j_username">`, plus `j_password`
- Sets `JSESSIONID` and `XSC`, both `Secure; HttpOnly`

`j_username` / `j_password` are the Servlet/Spring-Security form-login field names — a Java
backend with a classic session cookie. That is the good news: once a `JSESSIONID` exists,
plain HTTP requests should carry the session. The bad news is the reCAPTCHA key sitting in
the page config and the fact that the form is hydrated by React, so hidden fields may be
injected client-side that a naive HTTP POST would omit.

**Recommended shape:** Playwright logs in once (visible/interactive so the owner can clear
reCAPTCHA or an e-mail/SMS verification code if bol asks), harvest the cookie jar, persist it,
then refresh headlessly. Do **not** promise a pure-HTTP adapter until someone has actually
watched a successful login.

**What I searched, and found nothing (so nobody repeats it).**
GitHub repository search for `bol.com api`, `bol.com scraper`, `bolcom`, `bol com orders`,
`bol.com app api`; Home Assistant `custom_components` angle; Dutch-language searches for
`bestellingen` / `orderoverzicht` endpoint captures. **Every** hit was one of: (a) a Retailer
API client for sellers, (b) a *product/price* scraper (catalogue — explicitly out of scope for
this platform), or (c) spam. There is **no public reverse-engineering of the bol.com consumer
or mobile app API.** I probed `aai.bol.com` and `rsproxy.bol.com` (both appear in bol's CSP
`connect-src`): both refused connection from here; `swa.bol.com` returned 404 text/plain. No
consumer API surface was identified.

**Open questions a first authenticated run must answer** (all currently UNVERIFIED):
1. Is `/rnwy/account/bestellingen` server-rendered HTML, or does it fetch JSON from a
   backing endpoint? If JSON, that endpoint is the real prize and the tier may drop toward `http`.
2. Is reCAPTCHA always enforced, or only on risk?
3. Does bol force an e-mail/SMS verification code on a new device? (Assume yes; design the
   interactive-link flow for it.)
4. Does order history paginate, and by what parameter?
5. Are line items on the list page or only on an order-detail page?

---

## amazon.nl

| field | |
| --- | --- |
| Retailer / domain | Amazon Netherlands — `www.amazon.nl` |
| Tier | `browser_interactive` (first link) → `browser_once`. A pure-`http` variant exists in the wild but is not survivable unattended — see Feasibility. |
| Auth | password + OTP (TOTP, auto-solvable from a shared secret) + captcha/WAF challenge, over Amazon's OpenID form chain. Session then held in cookies; `x-main` is the authenticated-session marker. |
| Evidence | [alexdlaird/amazon-orders](https://github.com/alexdlaird/amazon-orders) — Python, 165★, **last push 2026-07-20** (v4.4.6, changelog entries dated 2026-07-20). [philipmulcahy/azad](https://github.com/philipmulcahy/azad) — Chrome extension, 321★, **last push 2026-07-20**. [eshaffer321/amazon-monarch-sync](https://github.com/eshaffer321/amazon-monarch-sync) — Node/Playwright, **last push 2026-07-02**. All three read as source, not just READMEs. |
| Confidence | **CONFIRMED-from-source** for endpoints, auth chain, money parsing and bot protection. **CONFIRMED by direct observation** for the amazon.nl redirect behaviour. **UNVERIFIED** for how amazon.nl's Dutch DOM differs in detail (no NL fixture exists in any of the three projects). |
| Base URL | `https://www.amazon.nl` |
| Order-history endpoint(s) | All HTML pages, no JSON API:<br>· `GET /your-orders/orders?timeFilter=year-YYYY&startIndex=N` (also `timeFilter=last30`, `timeFilter=months-3`; optional `&orderFilter=<value>`)<br>· `GET /gp/your-account/order-details?orderID=<id>`<br>· `GET /gp/css/summary/print.html?orderID=<id>` (print-friendly invoice — cleanest totals)<br>· `GET /cpe/yourpayments/transactions` (card-level transactions)<br>· digital goods: `GET /gp/legacy/order-history?opt=ab&orderFilter=year-YYYY&startIndex=N&unifiedOrders=0&digitalOrders=1`, or `/your-orders/orders?...&orderFilter=digital`<br>· auth entry: `GET /ax/claim`, `GET /ap/signin?openid.*`, sign-out `/gp/flex/sign-out.html` |
| Required headers | Ordinary Chromium fingerprint; nothing secret. From `constants.py` `BASE_HEADERS`: `User-Agent: Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36`, `Accept: text/html,application/xhtml+xml,...`, `Accept-Language` (see locale note — **there is no `nl` entry**), `Sec-Ch-Ua: "Chromium";v="149", "Google Chrome";v="149", "Not.A/Brand";v="24"`, `Sec-Ch-Ua-Mobile: ?0`, `Sec-Ch-Ua-Platform: "macOS"`, `Sec-Fetch-Dest: document`, `Sec-Fetch-Mode: navigate`, `Sec-Fetch-Site: none`, `Sec-Fetch-User: ?1`, `Upgrade-Insecure-Requests: 1`, plus `Host`/`Origin`/`Referer` derived from the active domain. Auth state: cookie `x-main` (`COOKIES_SET_WHEN_AUTHENTICATED = ["x-main"]`), plus `aws-waf-token` once a WAF challenge is cleared. |
| Money format | **Decimal, parsed out of a rendered currency string** — there is no numeric field anywhere, because there is no API. Fields: `Order.grand_total` (from `div.yohtmlc-order-total span.value` / `#wfm-grand-total-amount`), and on the details page `subtotal`, `shipping_total`, `estimated_tax`, `total_before_tax`, `refund_total`, `promotion_applied`, `coupon_savings`, `gift_card`, `gift_wrap` — all `Optional[float]`. **See the landmine below: the parser is not Dutch-safe.** |
| Bot protection | **AWS WAF JS challenge** (detected via `window.gokuProps`, cleared by an `aws-waf-token` cookie); **ACIC challenge page** at `/ax/aaut/verify/ap/challenge` (`#aa-challenge-page-captcha-container`), which can embed a WAF token challenge, a **visual grid puzzle** ("Choose all the buckets"), or both; a JS bot-detection interstitial ("verify that you're not a robot / Enable JavaScript"); and a legacy OCR image captcha. No Akamai/DataDome — Amazon runs its own. |
| Feasibility | **HARD** — the mechanics are fully documented by live projects, but the reference library ships first-class integrations for **three paid captcha-solving services** (CapSolver, Anti-Captcha, 2Captcha) because Amazon challenges routinely, and the money/label parsing is English-locale-only and will silently produce wrong euro amounts on amazon.nl unless deliberately corrected. |

### amazon.nl — detail

**There is no consumer order API. This is settled, not a guess.**
Three actively-maintained projects, three different languages, three different runtimes, all
scraping HTML:

- `alexdlaird/amazon-orders` — `requests` + BeautifulSoup. A 14 KB `selectors.py` of CSS
  selectors. `orders.py` walks `util.select(page.parsed, ORDER_HISTORY_ENTITY_SELECTOR)` and
  follows a `NEXT_PAGE_LINK_SELECTOR` anchor for pagination.
- `philipmulcahy/azad` — runs *inside* the logged-in browser as a Chrome extension, fetches
  order pages via hidden iframes, and parses the **transactions** page with a generated
  **ANTLR4 grammar** over page text. Nobody writes an ANTLR parser for a JSON endpoint.
- `eshaffer321/amazon-monarch-sync` — `chromium.launchPersistentContext(...)` from Playwright,
  then `page.goto(nextUrl)` and DOM extraction.

If a JSON consumer endpoint existed, at least one of these three would be using it.

**amazon.nl auth entry point — CONFIRMED by direct observation.**
An unauthenticated `GET https://www.amazon.nl/your-orders/orders` follows redirects to
`https://www.amazon.nl/ax/claim?arb=<uuid>` and returns 200. That matches
`SIGN_IN_CLAIM_URL = f"{base_url}/ax/claim"` and the `ClaimForm` that leads
`amazon-orders`' auth chain, so the `.nl` site does use the same entry flow as `.com`.

The full default auth chain (from `AmazonSession.default_auth_forms`, in order):
`ClaimForm → IntentForm → SignInForm → MfaDeviceSelectForm → MfaForm → CaptchaForm →
CaptchaForm(#2) → MfaForm(captcha-OTP variant) → AcicAuthBlocker → JSAuthBlocker`.
OTP can be fully automated by storing the TOTP shared secret (`otp_secret_key` /
`AMAZON_OTP_SECRET_KEY`); the library's own troubleshooting doc says enabling 2FA
*reduces* captcha frequency. The two blockers at the end are the ones that need a browser
or a paid solver.

**LANDMINE #1 — the money parser silently corrupts Dutch amounts. CONFIRMED from source.**
`amazonorders/entity/parsable.py`, `to_currency()`:

```python
value = re.sub("[a-zA-Z$£€₹,]+", "", value)
currency = util.to_type(value)
```

It strips commas and assumes `.` is the decimal separator. Dutch rendering is the opposite:
`€ 1.234,56` → after the regex → `1.234.56`… and on a plainer `€ 12,50` → `1250`. An order of
**€12,50 becomes 1250**. The docstring cheerfully says it "Recognizes the `$`, `£`, `€`, and
`₹` symbols", which is true and completely beside the point. This will not throw; it will just
be wrong. Any adapter must either force English rendering (below) or replace this function.

**LANDMINE #2 — subtotal/tax/shipping matching is English-substring based. CONFIRMED.**
`Order._parse_currency(contains)` does `if contains in tag.text.lower()`, called with
`"subtotal"`, `"estimated tax"`, `"grand total"`, `"shipping"`, `"before tax"`,
`"refund total"`, `"gift card amount"`, `"gift wrap"`. Dutch labels do not contain these
substrings (`"subtotaal"` does not contain `"subtotal"`). `selectors.py` also uses literal
English text matches — `Selector(..., text_contains="Whole Foods Market")`,
`Selector("h4.a-alert-heading", text_contains="cancelled")` — and `_parse_item_count` uses the
regex `r"(\d+)\s+items?\s+in this purchase"`. Dates go through
`dateutil.parser.parse(value, fuzzy=True)`, which does not know `januari` / `maart` / `mei`,
so `order_placed_date` would come back `None`.

**The fix, and it comes from azad's source (CONFIRMED).**
`azad`'s `order_list_page.ts` keeps a per-site URL template map and appends a language
override for exactly this reason:

```ts
['www.amazon.de', [BASE_URL_TEMPLATE + '&language=en_GB']],
['www.amazon.es', [BASE_URL_TEMPLATE + '&language=en_GB']],
['www.amazon.fr', [BASE_URL_TEMPLATE + '&language=en_GB']],
['www.amazon.it', [BASE_URL_TEMPLATE + '&language=en_GB']],
['other',         [BASE_URL_TEMPLATE + '&language=en_US']],
```

`www.amazon.nl` is **not** in that map, so azad falls through to `'other'` → `&language=en_US`.
Appending `&language=en_GB` (or `en_US`) to `/your-orders/orders?timeFilter=...` forces
English labels and English number formatting, which fixes both landmines at once.
**UNVERIFIED**: nobody has confirmed the `language` override actually takes on `.nl`
specifically, and it does not change the currency *symbol* (still €) — only the formatting.
Verify on the first authenticated run before trusting any euro figure.

**LANDMINE #3 — `amazon-orders` does not officially support non-`.com`.** Its own
`Constants` docstring: *"Only the English, `.com` site is officially supported. Other domains
may work, but values like `openid.assoc_handle` are not adjusted automatically."*
`SIGN_IN_QUERY_PARAMS` hardcodes `openid.assoc_handle: "usflex"` — a US value that
`_apply_domain()` does **not** rewrite. `_REGION_LANGUAGES` covers `ca, co.uk, com.au, in, sg`
and `_REGION_CURRENCIES` covers `co.uk, in, sg`; **neither has `nl`**, so `AMAZON_CURRENCY_SYMBOL=€`
must be set by hand. Treat the library as a specification of Amazon's endpoints and auth chain
— which it is, an excellent one — not as a dependency you can point at `.nl` and trust.

**"Request My Data" (GDPR export).** Requested manually through Amazon Privacy Central;
delivered asynchronously as a ZIP containing `Retail.OrderHistory.1.csv` (retail, digital,
refunds and cancellations in separate files). Reported turnaround varies wildly — from ~27
minutes to Amazon's stated "should not take more than a month" — **UNVERIFIED**, and it is
human-initiated per request with an e-mail confirmation step. It is a fine one-off backfill
for a motivated user, and it is **not** a connector: you cannot schedule it, cannot poll it,
and cannot complete it without the account holder clicking a link in their inbox.

**Mobile app API.** No public reverse-engineering project surfaced for the Amazon shopping
app's order endpoints. Amazon's mobile app traffic is certificate-pinned and the account
surfaces are largely webviews of the same pages listed above. Treat "there is a nicer mobile
API" as **SPECULATIVE** and do not budget a night against it.

---

## Recommended build order (value × feasibility)

**1. bol.com — build this one.**
Highest-value NL marketplace *and* the softer target of the two. No enterprise bot vendor in
front of it (verified by response headers, not assumed), a plain Java session cookie once
you are in, and a login that is a real form rather than an obfuscated challenge pipeline.
The whole job is: drive the Next.js login in Playwright once, keep the cookie jar, then
learn the shape of `/rnwy/account/bestellingen`. Budget the first session for *discovery* —
open the account page with devtools and find out whether it is server-rendered HTML or
JSON-backed, because that single fact decides whether the steady-state adapter is
`browser_once` or drops all the way to `http`.

**2. amazon.nl — build second, and budget double.**
Equally high value, meaningfully harder. Every endpoint and the entire auth chain are already
mapped for you by `amazon-orders` and `azad`, so there is no discovery risk — the risk is all
in operations (WAF challenges arriving at 3am with nobody to click a bucket) and in locale
correctness (the euro-parsing landmine above, which fails *silently*). Do it with Playwright
and a persistent profile, do the first login interactively while the owner is awake, store the
TOTP secret so OTP auto-solves, and append `&language=en_GB` to every order URL. Prefer
`/gp/css/summary/print.html?orderID=` for totals — it is the most stable, least
dynamically-rendered page Amazon serves.

---

## Do not attempt

- **`api.bol.com/retailer` for consumer order history.** Not "hard" — *impossible*.
  `client_credentials` has no user context; the endpoint returns a seller's incoming orders.
  Credentials are issued only to registered businesses via the Seller Dashboard. Every
  bol-related library on GitHub is a client for this API, so a GitHub search will keep
  offering it to you. It is the single most likely way to waste a day on this platform.
- **`qualityangel/bol-api-v10` as a consumer reference.** Its description says it "gets the
  orders from your bol.com account". Its `client.py` is `client_credentials` against
  `https://api.bol.com/retailer`. Seller tool. Fourth entry in this project's collection of
  READMEs that lie.
- **A pure-HTTP bol.com login** (POST `j_username`/`j_password` with `requests` and no
  browser) as the *initial* plan. It may well work, but there is a reCAPTCHA site key on the
  page and a React-hydrated form that may inject fields server-side rendering does not show.
  Prove it with a browser first, then optimise down to HTTP if the evidence supports it.
- **Pointing `alexdlaird/amazon-orders` at `amazon.nl` and shipping the result.** It will
  authenticate (the `/ax/claim` entry is shared) and it will return numbers. The numbers will
  be wrong: `€12,50` parses to `1250.0`, Dutch subtotal/tax labels do not match, and
  `order_placed_date` returns `None` on Dutch month names. Use it as documentation of
  Amazon's endpoints — it is the best such documentation available — not as a runtime.
- **Amazon "Request My Data" as a scheduled sync.** Human-initiated, e-mail-confirmed,
  asynchronous with an unbounded SLA. Good one-off backfill, not a connector.
- **Waiting for a bol.com or Amazon mobile API to turn up.** I searched for both from several
  angles and found nothing public for either. Anything claiming otherwise tonight would be a
  guess, and the preference order does not have a tier for guesses.

---

## Method note / provenance

- Read as **source** (not README): `amazon-orders` `constants.py`, `session.py`, `orders.py`,
  `entity/order.py`, `entity/parsable.py`, `selectors.py`, `util.py`, plus `docs/waf.rst`,
  `docs/browser.rst`, `docs/troubleshooting.rst`, `CHANGELOG.md`; `azad` `url.ts`,
  `order_list_page.ts`, `signin.ts`, `iframe-worker.ts`, `transaction2.ts`;
  `amazon-monarch-sync` `browser/client.ts`, `scrapers/orders.ts`;
  `qualityangel/bol-api-v10` `client.py`.
- Public unauthenticated HTTP observations: `www.bol.com/nl/nl/` response headers;
  `www.bol.com/nl/nl/rnwy/account/bestellingen` 302 chain; `login.bol.com/wsp/login` HTML;
  `www.amazon.nl/your-orders/orders` redirect to `/ax/claim`.
- Not done, by rule: no login, no account creation, no use of stored credentials, no
  authenticated request to either retailer. Every "UNVERIFIED" above is a thing that requires
  an authenticated session to settle, and is flagged as such rather than guessed.
