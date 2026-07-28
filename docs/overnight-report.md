# Overnight report — 2026-07-28

Built while you slept. No live login was attempted, no credential was read,
no captcha was solved. Baseline was 889 tests; it is now **1,302, all green,
zero warnings**, and the shop API boots and serves **15 providers**.

---

## 1 · What you can look at first

| | |
| --- | --- |
| **Providers now served** | 15 (was 9) |
| **New retail adapters** | Picnic, bol.com, Coolblue, Amazon NL |
| **New platform adapters** | Magento-guest, WooCommerce-guest |
| **Corrected** | Jumbo — six confirmed defects, one of them financial |
| **Need no agent at all** | Picnic, Magento-guest, Woo-guest |
| **Research** | `docs/research/` — seven files, every claim marked CONFIRMED or UNVERIFIED |

Three providers needing **no browser and no agent** is new. After Albert Heijn
moved to T2 there were none, and an `http`-tier provider is cheaper, faster and
far less exposed than anything driving Chromium.

---

## 2 · The financial bug, first, because it is the important one

**Jumbo's money unit was inverted, and it was live.**

`JumboOptions` declared `Minor` (integer cents). Jumbo's order totals are
**decimal-string euros** — the reference client does
`parseFloat(order.totalToPayMoneyType.amount)` with no division. Its *catalogue*
prices are `/100`, which is where the confusion came from.

A €31.13 order would have been ingested as **€0.31**.

Fixed, with the catalogue unit declared alongside — unused by the adapter,
present purely so nobody re-inverts it — and a test that asserts both the right
reading and the old wrong one, so a regression is loud rather than quiet.

This is exactly the class of failure the "declare units, never sniff" rule
exists to prevent, and it still got in. Worth remembering when reviewing the new
adapters: **every one of them declares its unit, and not one of them is
verified against a real response.** See §6.

---

## 3 · Jumbo — the project's biggest unknown is now closed

`vghoost360/Jumbo-API` (pushed 2026-02-22) had the whole thing. Read from
source, not its README.

- **No persisted queries.** APQ was the blocker we were braced for; it does not
  exist here. Plain documents, nothing to capture.
- **The real operation** is `GetOnlineOrdersAndStoreReceipts`, with
  `ordersInput: {offset, limit, direction, sortBy, statusCategory}`.
- **Line items** come from `OrderPagesOrder` — note the name. `OrdersPageOrders`,
  which we had, returns zero GitHub hits and was invented.
- **Two independent paginations**: `offset`/`limit` nested for orders,
  `page`/`pageSize` top-level for receipts. One counter drove neither.
- **In-store receipts carry no structured items** — only a receipt-printer
  layout that must be text-parsed. Implemented behind a seam: it emits items
  when it parses, and an explicitly **unreconciled** receipt when it does not.
  It never invents a line.
- **The login URL 404s.** `jumbo.com/inloggen` is gone; the live chain runs
  through Auth0 at `auth.jumbo.com`.

---

## 4 · The best result is not a retailer

The platform sweep found that **Magento's `guestOrder` and the WooCommerce Store
API need no login, no captcha and no browser**. An order number and an e-mail
address is the entire credential, and both return full line items. Confirmed
from source.

One adapter each, and they serve **any shop on that platform** rather than one
retailer. That is a fundamentally better shape than a per-retailer scraper, and
it is the direction worth pushing.

The same sweep rates an **e-mail connector** — reading order confirmations from
the user's own mailbox — as the highest-value item in the whole document:
universal coverage, no bot protection, nothing to churn, and it is the natural
supplier of the order references the two guest adapters need. **I did not build
it.** It touches the user's mailbox, which is a bigger privacy decision than I
should make while you are asleep. It is the first thing worth discussing.

---

## 5 · Two traps avoided, and a pattern worth naming

**bol.com's documented API is seller-only.** `api.bol.com` authenticates with
`client_credentials` — no user context anywhere in it. An adapter built on it
would have read *a different person's orders*. The consumer route is an
ordinary session login, and bol turned out to be softer than expected: no
Akamai, no DataDome, no Cloudflare.

**`amazon-orders` would have corrupted every Dutch amount.** It strips commas
and assumes `.` is the decimal separator, so **€12,50 becomes 1250.0** — and it
does not throw. We parse Dutch money explicitly instead.

That is now **five** READMEs in two days that disagreed with their own source:
Albert Heijn's client id, Jumbo's non-existent refresh token, Lidl's
"phone number" login, bol's "consumer" API, and `amazon-orders`' locale support.
The rule that keeps paying: **read the source, and mark anything you only read
*about* as UNVERIFIED.**

---

## 6 · What is NOT proven — read this before trusting anything above

Every new adapter is **fixture-tested only**. Not one has seen a real response.
The manifests validate and the parsers work against payloads we wrote, which
proves the shape we *expect* — not the shape that arrives.

| Provider | Confidence | What a live run must settle |
| --- | --- | --- |
| **Picnic** | Highest — confirmed end to end from a maintained client | Whether the auth scheme still matches; cents assumption on a real total |
| **Jumbo** | High — operations confirmed from source | **The first real total.** The unit was wrong before. Also whether the Auth0 login page's selectors match |
| **Magento / Woo guest** | High — confirmed from platform source | Whether real NL shops expose these endpoints, and how a user gets an order reference |
| **bol.com** | Medium | **Whether the orders page is HTML or JSON-backed.** Both shapes are implemented behind an option; the default is HTML |
| **Amazon NL** | Medium | Selectors, and whether the WAF challenge is survivable attended |
| **Coolblue** | Auth high, fetch **unproven** | The order endpoint is genuinely unknown — see below |

**Coolblue is deliberately half-built.** Its OIDC + PKCE login is confirmed and
complete; its order endpoint could not be found — `/graphql` 404s,
`api.coolblue.nl` does not resolve, and there is no prior art. Rather than
guess, the fetch sits behind a seam that fails with a message naming exactly
what to capture in devtools. A half-adapter whose missing half is precisely
specified is worth more than a guessed whole one.

---

## 7 · Deliberately not attempted

| | Why |
| --- | --- |
| **MediaMarkt** | Cloudflare returned an *interactive* captcha to one benign query, and their SPA uses a persisted-query **manifest**, so arbitrary documents are rejected outright. Three walls, no prior art. |
| **Kruidvat** | Every path on `api.kruidvat.nl` returns Akamai `Access Denied` to a non-app client — including a static JS bundle. |
| **Zalando, Douglas, ICI Paris, Bijenkorf** | Akamai with zero prior art on the account side. High value, but not a night's work. |
| **Zalando Lounge** | Nothing establishable without logging in, which I would not do. |
| **Etos / Gall & Gall** | Genuinely promising — same Ahold platform as AH, so two retailers may fall out of one adapter. Gated behind one cheap experiment: pull the `clientId` from the Mijn Etos APK and see whether `posReceiptsPage` resolves. Worth doing awake. |

---

## 8 · Suggested order when you are back

1. **Jumbo live** — closest to working, and the one number to check first is a
   real total against the app's own screen.
2. **Picnic live** — rated easiest; needs no agent, so it is the cheapest
   possible end-to-end proof.
3. **Decide on the e-mail connector** (§4). Highest ceiling, and a privacy call
   that is yours.
4. **Coolblue devtools capture** — ten minutes of your time converts a
   half-adapter into a whole one.
5. **Lidl via BYO-attach** — last night established Lidl blocks automated
   browsers by score. A real browser profile is the only route, which is
   `execution-modes-design.md` §4.5.

Still open from before: Albert Heijn's statiegeld field (57 of 131 receipts do
not reconcile — every delta is a combination of 15c and 25c deposits), and the
execution-mode slices X1/X2.
