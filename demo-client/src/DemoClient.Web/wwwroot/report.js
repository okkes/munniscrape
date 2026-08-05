/**
 * "Something went wrong here" - collected, redacted, and put on the clipboard.
 *
 * The problem this solves is real: a connector failure is visible in four
 * places at once - the page, the browser console, the relay's log and the
 * agent's - and a person looking at the first of those cannot usefully
 * describe the other three. "It says provider unavailable" is not enough to
 * act on; the job id, the error code, the last few API calls and what the
 * connector was doing at the time usually are.
 *
 * WHAT THIS MUST NEVER DO is the whole design. A diagnostic bundle is the most
 * natural place in any app for a secret to escape: it is assembled in a hurry,
 * it is pasted into chat windows and issue trackers, and nobody reads it first.
 * This app holds three things that must never travel - the sealed session
 * bundle, the sealed credential bundle, and whatever the user typed into a
 * provider's form - and it would be trivial to scoop all three up by dumping
 * sessionStorage and calling it context.
 *
 * So the rule here is ALLOWLIST, not blocklist. Nothing reaches the report
 * unless it is named below, field by field. There is deliberately no code path
 * that iterates storage, no "...rest" spread of a server response, and no
 * "include everything under a size limit". A field that turns out to be useful
 * gets added on purpose; a field nobody thought about never appears by
 * accident.
 *
 * The redactor at the bottom is the belt to that brace: even an allowlisted
 * string is scanned for the bundle prefixes and for anything long enough to be
 * a token, because the cheapest way to leak a credential is to put it somewhere
 * nobody expected it - a provider echoing an input back inside an error detail,
 * say.
 */

/** How many API calls to keep. Enough to see a flow, short enough to read. */
const HISTORY = 25;

/** How many console errors to keep. */
const CONSOLE_HISTORY = 15;

const calls = [];
const consoleErrors = [];

/**
 * Prefixes that identify a sealed blob. Anything starting with one of these
 * is a credential in transit and never appears in a report, whatever field it
 * was found in.
 */
const BUNDLE_PREFIXES = ['sb_v1.', 'cb_v1.', 'tkt_', 'rt_'];

/**
 * What the app was doing, kept here rather than threaded through ten call
 * sites. Merged rather than replaced, so a screen that knows only the job id
 * does not erase the provider somebody chose two screens ago.
 *
 * Allowlisted on the way OUT, in build(), not on the way in - so a careless
 * caller cannot widen a report by remembering more.
 */
let context = {};

export function remember(fields) {
  context = { ...context, ...fields };
}

export function forget() {
  context = {};
}

/** Recorded by api.js around every call. Never the body - only its shape. */
export function noteCall(entry) {
  calls.push({
    at: new Date().toISOString(),
    method: entry.method,
    // Path only. A query string is where an authorization code, a resume
    // ticket or somebody's e-mail address would be.
    path: String(entry.path ?? '').split('?')[0],
    status: entry.status ?? null,
    ms: entry.ms ?? null,
    code: entry.code ?? null,
  });

  if (calls.length > HISTORY) calls.shift();
}

/**
 * Console errors, captured from load. Hooked rather than read back, because
 * there is no API for reading the console after the fact - which is exactly
 * why a user cannot be asked to describe what is in it.
 */
export function watchConsole() {
  const original = globalThis.console?.error;
  if (typeof original !== 'function') return;

  globalThis.console.error = (...args) => {
    try {
      consoleErrors.push({
        at: new Date().toISOString(),
        text: args.map(one => (one instanceof Error ? one.message : String(one))).join(' ').slice(0, 400),
      });

      if (consoleErrors.length > CONSOLE_HISTORY) consoleErrors.shift();
    } catch {
      // A logger that can break the app is worse than no logger.
    }

    original.apply(globalThis.console, args);
  };

  globalThis.addEventListener?.('unhandledrejection', (event) => {
    const reason = event?.reason;
    consoleErrors.push({
      at: new Date().toISOString(),
      text: `unhandled: ${(reason instanceof Error ? reason.message : String(reason)).slice(0, 400)}`,
    });

    if (consoleErrors.length > CONSOLE_HISTORY) consoleErrors.shift();
  });
}

/**
 * The report.
 *
 * `context` is supplied by whoever is rendering the failure and is itself
 * allowlisted below - a caller cannot widen the report by passing more.
 */
export function build(extra = {}) {
  const what = { ...context, ...extra };

  const report = {
    _comment:
      'Diagnostics from the connector demo client. Contains no bundles, no tickets, '
      + 'no credentials and no provider payloads - see report.js for what is collected and why.',

    at: new Date().toISOString(),
    page: {
      // The app's own URL, not the provider's, and without a query string.
      url: String(globalThis.location?.origin ?? '') + String(globalThis.location?.pathname ?? ''),
      userAgent: String(globalThis.navigator?.userAgent ?? '').slice(0, 300),
      language: String(globalThis.navigator?.language ?? ''),
      viewport: `${globalThis.innerWidth ?? 0}x${globalThis.innerHeight ?? 0}`,
      timeZone: Intl?.DateTimeFormat?.().resolvedOptions?.().timeZone ?? null,
    },

    // Opaque ids. Deliberately included: they are what lets a connector log be
    // found, and they identify a row rather than a person.
    what: {
      service: what.service ?? null,
      provider: what.provider ?? null,
      manifestVersion: what.manifestVersion ?? null,
      resource: what.resource ?? null,
      sessionId: what.sessionId ?? null,
      jobId: what.jobId ?? null,
      jobState: what.jobState ?? null,
      step: what.step ?? null,
      stepsDone: what.stepsDone ?? null,
      challengeType: what.challengeType ?? null,
      liveOrigin: what.liveOrigin ?? null,
    },

    error: extra.error
      ? {
        code: extra.error.code ?? null,
        httpStatus: extra.error.status ?? null,
        retriable: extra.error.retriable ?? null,
        userAction: extra.error.userAction ?? extra.error.user_action ?? null,
        messageKey: extra.error.messageKey ?? extra.error.message_key ?? null,
        // The connector's own words about what broke. Redacted like
        // everything else: a provider that echoes an input back inside a
        // detail string would otherwise put it here.
        detail: clean(extra.error.detail ?? extra.error.message ?? null),
      }
      : null,

    api: calls.map(call => ({ ...call })),
    consoleErrors: consoleErrors.map(one => ({ ...one, text: clean(one.text) })),
  };

  return JSON.stringify(report, null, 2);
}

/**
 * Puts the report on the clipboard, falling back to a selectable textarea
 * where the clipboard API is unavailable or refused - which it is on an
 * insecure origin, and this app is often served over plain http locally.
 */
export async function copy(text) {
  try {
    await globalThis.navigator?.clipboard?.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/**
 * Removes anything that looks like a credential, wherever it turned up.
 *
 * Belt to the allowlist's brace. The allowlist decides which FIELDS travel;
 * this decides that a field's CONTENTS are not a secret somebody put in an
 * unexpected place.
 */
function clean(value) {
  if (typeof value !== 'string' || value.length === 0) return value ?? null;

  let cleaned = value;

  for (const prefix of BUNDLE_PREFIXES) {
    // The prefix plus everything up to the next whitespace or quote.
    cleaned = cleaned.replaceAll(
      new RegExp(`${prefix.replace('.', '\\.')}[^\\s"']*`, 'g'),
      `<${prefix}redacted>`);
  }

  // Any remaining run long enough to be a token. A sealed bundle is thousands
  // of characters; no legitimate word in an error message is forty.
  cleaned = cleaned.replaceAll(/[A-Za-z0-9_\-+/=.]{40,}/g, '<redacted>');

  return cleaned.slice(0, 2_000);
}
