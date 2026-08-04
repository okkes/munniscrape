/**
 * DOM helpers, formatting, and the renderers for normalised records.
 *
 * Nothing here uses innerHTML. Every value on this page came off a provider's
 * website by way of a scraper, so it is treated as text and only ever as
 * text.
 *
 * The record renderers are shape-driven, not provider-driven: a receipt is a
 * receipt whether it came from Jumbo, Lidl or a fixture, which is the entire
 * value of the connector normalising its output.
 */

import * as copy from './copy.js';

// ── DOM ───────────────────────────────────────────────────────────────────

export function h(tag, props, ...children) {
  const node = document.createElement(tag);

  for (const [name, value] of Object.entries(props ?? {})) {
    if (value === null || value === undefined || value === false) continue;

    if (name === 'class') node.className = value;
    else if (name === 'text') node.textContent = String(value);
    else if (name === 'dataset') Object.assign(node.dataset, value);
    else if (name.startsWith('on') && typeof value === 'function') {
      node.addEventListener(name.slice(2).toLowerCase(), value);
    } else if (value === true) node.setAttribute(name, '');
    else node.setAttribute(name, String(value));
  }

  append(node, children);
  return node;
}

export function append(parent, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    parent.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return parent;
}

export function clear(node) {
  while (node.firstChild) node.firstChild.remove();
  return node;
}

export function mount(node, ...children) {
  clear(node);
  return append(node, children);
}

// ── copy ──────────────────────────────────────────────────────────────────

/**
 * Renders a resolved copy key. A key nobody has translated shows as the raw
 * key in monospace: a missing string must be obvious at a glance instead of
 * quietly reading as intentional.
 */
export function say(resolved, extra) {
  if (!resolved || !resolved.text) return null;
  return resolved.missing
    ? h('code', { class: `copy-missing ${extra ?? ''}`.trim(), title: 'no copy for this key yet' }, resolved.text)
    : h('span', { class: extra ?? null }, resolved.text);
}

export const sayKey = (name, extra) => say(copy.key(name), extra);
export const sayTerm = (group, value, extra) => say(copy.term(group, value), extra);

// ── formatting ────────────────────────────────────────────────────────────

/**
 * Money arrives as minor units plus a currency, always. The number of minor
 * units in a major one is a property of the currency, not a constant - JPY
 * has none - so the exponent comes from Intl rather than a hardcoded 100.
 */
export function money(value, { signed = false } = {}) {
  const amount = normaliseMoney(value);
  if (!amount) return 'n/a';

  const format = new Intl.NumberFormat(undefined, { style: 'currency', currency: amount.currency });
  const digits = format.resolvedOptions().maximumFractionDigits ?? 2;
  const major = amount.value / 10 ** digits;
  const text = format.format(major);

  return signed && amount.value > 0 ? `+${text}` : text;
}

function normaliseMoney(value) {
  if (!value || typeof value.value !== 'number') return null;
  return { value: value.value, currency: value.currency ?? 'EUR' };
}

export function moneySign(value) {
  const amount = normaliseMoney(value);
  if (!amount) return 'zero';
  if (amount.value < 0) return 'negative';
  return amount.value > 0 ? 'positive' : 'zero';
}

export function dateTime(iso) {
  if (!iso) return 'n/a';
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return String(iso);
  return at.toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}

export function date(value) {
  if (!value) return 'n/a';
  // Dates are plain YYYY-MM-DD on the wire; parsing them as UTC and printing
  // them locally is how a purchase drifts to the day before.
  const [y, m, d] = String(value).slice(0, 10).split('-').map(Number);
  if (!y || !m || !d) return String(value);
  return new Date(y, m - 1, d).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: '2-digit' });
}

export function today() {
  return new Date().toISOString().slice(0, 10);
}

export function daysAgo(days) {
  const at = new Date();
  at.setDate(at.getDate() - days);
  return at.toISOString().slice(0, 10);
}

// ── countdowns ────────────────────────────────────────────────────────────

// One ticker for the whole page. Every expiry on screen - challenges,
// bundles, sessions - reads from it, so they cannot drift apart.
const tickers = new Set();
setInterval(() => {
  for (const tick of tickers) tick();
}, 1000);

/**
 * A live countdown to an instant. `onExpire` fires once, which is what
 * disables an expired challenge rather than leaving a dead form on screen.
 */
export function countdown(iso, { onExpire, prefix = '' } = {}) {
  const element = h('span', { class: 'countdown' });
  let expired = false;

  const tick = () => {
    const remaining = new Date(iso).getTime() - Date.now();
    if (Number.isNaN(remaining)) {
      element.textContent = '';
      return;
    }

    if (remaining <= 0) {
      element.textContent = 'expired';
      element.classList.add('is-expired');
      if (!expired) {
        expired = true;
        onExpire?.();
      }
      return;
    }

    element.classList.toggle('is-urgent', remaining < 30_000);
    element.textContent = prefix + humanise(remaining);
  };

  tickers.add(tick);
  tick();

  return { element, dispose: () => tickers.delete(tick), isExpired: () => expired };
}

function humanise(ms) {
  const total = Math.floor(ms / 1000);
  const days = Math.floor(total / 86_400);
  const hours = Math.floor((total % 86_400) / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;

  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`;
  if (minutes > 0) return `${minutes}m ${String(seconds).padStart(2, '0')}s`;
  return `${seconds}s`;
}

// ── small components ──────────────────────────────────────────────────────

export function badge(text, kind) {
  return h('span', { class: `badge badge-${kind ?? 'neutral'}` }, text);
}

export function field(label, ...value) {
  return h('div', { class: 'field' },
    h('span', { class: 'field-label' }, label),
    h('span', { class: 'field-value' }, ...value));
}

export function note(...children) {
  return h('p', { class: 'note' }, ...children);
}

export function spinner(label) {
  return h('div', { class: 'spinner-row' },
    h('span', { class: 'spinner', 'aria-hidden': 'true' }),
    label ? h('span', {}, label) : null);
}

export function empty(message) {
  return h('p', { class: 'empty' }, message);
}

/**
 * The error block. `user_action` is rendered as loudly as the message
 * because it is the half a user can act on, and agent_unavailable is styled
 * as a prompt rather than a failure - the user's machine being off is not an
 * error, it is an instruction.
 */
export function errorBlock(error) {
  const kind = copy.tone(error.code);
  const hint = copy.actionHint(error.userAction);

  return h('div', { class: `alert alert-${kind}`, role: kind === 'error' ? 'alert' : 'status' },
    h('div', { class: 'alert-head' },
      badge(copy.term('user_action', error.userAction).text || error.userAction, kind === 'notice' ? 'info' : 'bad'),
      h('strong', {}, say(copy.key(error.messageKey)))),
    hint ? h('p', { class: 'alert-hint' }, hint) : null,
    error.retryAfterSeconds
      ? h('p', { class: 'alert-hint' }, `Try again in ${error.retryAfterSeconds}s.`)
      : null,
    h('p', { class: 'alert-meta' },
      h('code', {}, error.code),
      error.status ? h('code', {}, `HTTP ${error.status}`) : null,
      h('code', {}, error.retriable ? 'retriable' : 'not retriable'),
      error.detailId ? h('code', {}, error.detailId) : null,
      error.detail ? h('code', { class: 'muted' }, error.detail) : null));
}

// ── normalised records ────────────────────────────────────────────────────

/**
 * One renderer per shape, chosen from the manifest's `returns`. A caller
 * never learns which provider a record came from, so neither does this.
 */
export function records(shape, rows) {
  if (!rows || rows.length === 0) return empty('No records in this window.');

  switch (shape) {
    case 'receipt': return h('ul', { class: 'records' }, rows.map(receipt));
    case 'transaction': return transactions(rows);
    case 'account': return h('ul', { class: 'records' }, rows.map(account));
    case 'credit_registration': return h('ul', { class: 'records' }, rows.map(creditRegistration));
    default: return h('ul', { class: 'records' }, rows.map(unknownRecord));
  }
}

/**
 * A derived total is checked first, because a receipt carrying one is always
 * unreconciled: there was no stated total to check ours against. Reading the
 * reconciliation flag first would report every bol receipt as a mismatch.
 */
function totalBadge(reconciled, derived) {
  if (derived) return badge('total derived', 'warn');
  return reconciled ? badge('reconciled', 'good') : badge('NOT RECONCILED', 'bad');
}

function totalNote(reconciled, derived) {
  if (derived) {
    return note('This provider states no order total, so the one shown is the connector\'s own sum ',
      'of the line items. Nothing independent was checked against it, which is why it is not ',
      'marked reconciled - the flag means "the provider\'s total was verified", and here there was none.');
  }

  if (reconciled) return null;

  return note('The line items and discounts do not add up to the stated total. ',
    'The connector handed it over anyway, flagged, instead of dropping it or guessing.');
}

/**
 * One credit, as a register states it.
 *
 * Not a transaction, and it does not pretend to be one: there is no moment it
 * happened at and no money that moved. What it has is a standing amount, a
 * lender, and whether it is still running.
 */
function creditRegistration(row) {
  const amount = row.amount ?? {};
  const ended = row.status === 'ended';

  return h('li', { class: `record record-credit ${ended ? 'is-ended' : ''}`.trim() },
    h('div', { class: 'record-head' },
      h('div', {},
        h('strong', {}, row.creditor ?? 'Unknown creditor'),
        // The register's own words, not our enum. Somebody checking this
        // against a letter from BKR is looking for "Aflopend krediet", and a
        // label we mapped to `instalment` would not match what they hold.
        row.kind_label ? h('span', { class: 'muted' }, ` ${row.kind_label}`) : null,
        h('div', { class: 'muted small' }, creditDates(row))),
      h('div', { class: 'record-amount' },
        h('span', { class: 'amount' }, money(amount)),
        badge(ended ? 'ended' : 'running', ended ? 'neutral' : 'info'),
        // Never softened, never summarised, never hidden behind a tooltip.
        // An arrears code is the single most consequential thing a credit
        // register says about somebody, and what it means for their mortgage
        // application is not this app's judgement to make.
        row.arrears_code ? badge(row.arrears_code, 'bad') : null)),
    row.monthly_amount
      ? h('div', { class: 'muted small' }, `${money(row.monthly_amount)} per month`)
      : null);
}

function creditDates(row) {
  const from = row.started_on;
  const to = row.ends_on;

  if (from && to) return `${from} to ${to}`;
  if (from) return `since ${from}`;

  // Revolving credit states neither, and saying so beats an empty line that
  // reads as a field we failed to fetch.
  return 'no dates stated';
}

function receipt(row) {
  const items = row.items ?? [];
  const merchant = row.merchant ?? {};
  const payment = row.payment ?? {};
  const reconciled = row.reconciled !== false;

  // Two very different reasons a receipt can arrive unreconciled, and only one
  // of them is a problem. A derived total is one the provider never stated, so
  // the connector summed the lines itself - there was nothing to check it
  // against. Showing that as "NOT RECONCILED" in the same red as a receipt
  // whose numbers genuinely disagree teaches the user to ignore the badge.
  const derived = row.total_is_derived === true;

  return h('li', { class: `record record-receipt ${reconciled || derived ? '' : 'is-unreconciled'}`.trim() },
    h('div', { class: 'record-head' },
      h('div', {},
        h('strong', {}, merchant.name ?? 'Unknown merchant'),
        merchant.store_name ? h('span', { class: 'muted' }, ` ${merchant.store_name}`) : null,
        h('div', { class: 'muted small' }, dateTime(row.purchased_at))),
      h('div', { class: 'record-amount' },
        h('span', { class: 'amount' }, money(row.total)),
        // The platform emits an unreconciled receipt rather than dropping
        // it, so the consumer decides. Hiding the badge would throw away
        // the one signal that says "these line items do not add up".
        totalBadge(reconciled, derived))),

    h('div', { class: 'record-meta' },
      payment.method ? h('code', {}, payment.method) : null,
      payment.card_last4 ? h('code', {}, `card ****${payment.card_last4}`) : null,
      payment.iban_tail ? h('code', {}, `iban ...${payment.iban_tail}`) : null,
      // Explicit null, not an omission: without a tail, matching a receipt
      // to a bank transaction is weaker and the user should know.
      !payment.card_last4 && !payment.iban_tail ? h('code', { class: 'muted' }, 'no payment tail') : null,
      h('code', { class: 'muted' }, row.id ?? '')),

    totalNote(reconciled, derived),

    items.length === 0
      ? null
      : h('details', { class: 'items' },
        h('summary', {}, `${items.length} line item${items.length === 1 ? '' : 's'}`),
        h('table', { class: 'items-table' },
          h('thead', {}, h('tr', {},
            h('th', {}, 'Item'), h('th', {}, 'Qty'), h('th', {}, 'Unit'), h('th', {}, 'Total'))),
          h('tbody', {}, items.map(receiptItem)))),

    rawBlock(row));
}

/**
 * The provider's own document, where `include=raw` asked for one.
 *
 * Collapsed, and absent entirely when nothing was asked for or the adapter had
 * no single document to hand back. It is the thing to read when a normalised
 * field goes missing: the record above says only that a value is gone, and this
 * says what actually arrived.
 *
 * Rendered last and behind a disclosure on purpose - it is the more sensitive
 * of the two, because normalisation drops the fields nobody asked for and this
 * puts them back.
 */
function rawBlock(row) {
  if (!row.raw) return null;

  return h('details', { class: 'items raw' },
    h('summary', {}, 'provider payload'),
    h('pre', { class: 'raw-json' }, JSON.stringify(row.raw, null, 2)));
}

function receiptItem(item) {
  const discount = item.discount;

  return h('tr', {},
    h('td', {},
      item.name ?? '',
      discount
        ? h('div', { class: 'discount' },
          badge(money(discount.amount), 'discount'),
          discount.label ? h('span', { class: 'muted small' }, ` ${discount.label}`) : null)
        : null),
    h('td', { class: 'num' }, item.quantity ?? ''),
    h('td', { class: 'num' }, item.unit_price ? money(item.unit_price) : ''),
    h('td', { class: 'num' }, money(item.total)));
}

function transactions(rows) {
  return h('div', { class: 'table-scroll' },
    h('table', { class: 'data-table' },
      h('thead', {}, h('tr', {},
        h('th', {}, 'Booked'),
        h('th', {}, 'Counterparty'),
        h('th', {}, 'Description'),
        h('th', {}, 'Kind'),
        h('th', { class: 'num' }, 'Amount'))),
      h('tbody', {}, rows.map((row) => h('tr', {},
        h('td', {}, date(row.booked_at)),
        h('td', {}, row.counterparty?.name ?? 'n/a',
          row.counterparty?.iban ? h('div', { class: 'muted small' }, row.counterparty.iban) : null),
        h('td', { class: 'wrap' }, row.description ?? ''),
        h('td', {}, sayTerm('transaction_kind', row.kind)),
        h('td', { class: `num amount-${moneySign(row.amount)}` }, money(row.amount, { signed: true })))))));
}

function account(row) {
  const balance = row.balance;
  const amount = balance?.amount ?? balance;
  const asOf = balance?.as_of;

  return h('li', { class: 'record record-account' },
    h('div', { class: 'record-head' },
      h('div', {},
        h('strong', {}, row.display_name ?? 'Account'),
        h('div', { class: 'muted small' }, sayTerm('account_type', row.type))),
      h('div', { class: 'record-amount' },
        h('span', { class: `amount amount-${moneySign(amount)}` }, money(amount)),
        asOf ? h('span', { class: 'muted small' }, dateTime(asOf)) : null)),
    h('div', { class: 'record-meta' },
      row.iban ? h('code', {}, row.iban) : null,
      row.masked_number ? h('code', {}, row.masked_number) : null,
      h('code', { class: 'muted' }, row.currency ?? ''),
      h('code', { class: 'muted' }, row.id ?? '')));
}

function unknownRecord(row) {
  // A shape this build does not know about is shown raw rather than dropped:
  // silently losing records is the worse failure.
  return h('li', { class: 'record' }, h('pre', { class: 'raw' }, JSON.stringify(row, null, 2)));
}
