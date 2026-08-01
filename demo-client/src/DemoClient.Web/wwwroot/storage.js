/**
 * Where a browser is allowed to keep a sealed bundle: sessionStorage, and
 * nowhere else.
 *
 * THE WEB CUSTODY RULE, in code. A bundle is ciphertext only the connector
 * can open, but it is still a live credential for as long as it is valid, so
 * on web it lives in the tab's session and dies with the tab. Never
 * localStorage (survives a browser restart, shared across tabs, readable by
 * any script that ever gets injected), never IndexedDB (same, plus it
 * survives longer than anyone expects). The user re-authenticates on the
 * next visit, and the connector caps a web bundle's TTL at an hour anyway -
 * so a bundle that somehow escapes this tab has a small window.
 *
 * The exception worth knowing: a `secret_custody: agent` provider seals only
 * `{agent_id, profile_id}` into its bundle. That is not a credential, which
 * is why web plus a BYO agent is the strongest combination in the design.
 * This module still keeps it in sessionStorage, because a demo that hides
 * the strict rule behind an optimisation teaches the wrong thing.
 */

const KEY = 'munni.demo.connections.v1';

/**
 * Sealed credentials, keyed by provider rather than by connection.
 *
 * A connection is one session and dies with it; the whole point of this is the
 * NEXT one, so it outlives any single row. Same sessionStorage and therefore
 * the same web custody rule: it dies with the tab, which is the boundary this
 * app already commits to.
 *
 * It is ciphertext only the connector can open, bound to this subject and this
 * provider, so a blob lifted out of here is useless to anybody else. That is
 * the entire reason a password may be kept at all - what is stored is not the
 * password.
 */
const CREDENTIALS_KEY = 'munni.demo.credentials.v1';

const listeners = new Set();

function read() {
  try {
    const raw = globalThis.sessionStorage.getItem(KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    // A corrupt store is not worth crashing the app over; the connections
    // are re-creatable by definition.
    return [];
  }
}

function write(rows) {
  globalThis.sessionStorage.setItem(KEY, JSON.stringify(rows));
  for (const fn of listeners) fn(rows);
}

// ── sealed credentials ────────────────────────────────────────────────────

function readCredentials() {
  try {
    const raw = globalThis.sessionStorage.getItem(CREDENTIALS_KEY);
    const parsed = raw ? JSON.parse(raw) : null;
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

/** The sealed credential bundle for a provider, or null. */
export function credentials(service, provider) {
  return readCredentials()[`${service}:${provider}`] ?? null;
}

/**
 * Keeps what the connector sealed, so the next login needs no typing.
 *
 * Called with whatever the login response carried, which for most providers is
 * nothing at all - only a provider declaring `offers_credential_store` ever
 * mints one, and only when a password was actually collected.
 */
export function rememberCredentials(service, provider, bundle) {
  if (!bundle) return;

  const all = readCredentials();
  all[`${service}:${provider}`] = bundle;
  globalThis.sessionStorage.setItem(CREDENTIALS_KEY, JSON.stringify(all));
}

/**
 * Drops it. Called when the connector says the credential itself is wrong -
 * the one answer that must never be retried, because a stored password
 * re-submitted on a schedule is how an account gets locked.
 */
export function forgetCredentials(service, provider) {
  const all = readCredentials();
  if (!(`${service}:${provider}` in all)) return;

  delete all[`${service}:${provider}`];
  globalThis.sessionStorage.setItem(CREDENTIALS_KEY, JSON.stringify(all));
}

/** Stable per (service, provider, session) so a re-login replaces its row. */
export function idFor(service, provider, sessionId) {
  return `${service}:${provider}:${sessionId}`;
}

export function connections() {
  return read();
}

export function connection(id) {
  return read().find((c) => c.id === id) ?? null;
}

/** Insert or merge. Fields absent from `row` are kept. */
export function save(row) {
  const rows = read();
  const at = rows.findIndex((c) => c.id === row.id);
  if (at === -1) rows.push(row);
  else rows[at] = { ...rows[at], ...row };
  write(rows);
  return connection(row.id);
}

export function forget(id) {
  write(read().filter((c) => c.id !== id));
}

/**
 * Replace the stored bundle. Called from the same function that renders the
 * fetched data, never from a separate step: a crash between persisting rows
 * and persisting the rotated bundle costs the whole connection.
 */
export function rotateBundle(id, bundle) {
  if (!bundle) return connection(id);
  return save({ id, bundle, bundleIssuedAt: new Date().toISOString() });
}

export function subscribe(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/** True when this tab has never held a bundle for a connection it can see. */
export function needsSignin(row) {
  return !row.bundle;
}
