/**
 * Every user-facing string in this app, in one file.
 *
 * This is the half of the contract the connector deliberately does not own.
 * A connector emits `message_key`, `label_key`, `prompt_key`, `reason_key`
 * and typed enums - never English - because prose cannot be translated by a
 * caller, cannot be restyled, and becomes a de-facto API the moment someone
 * string-matches it. So the consumer owns it, and this file IS that
 * ownership: swapping it for a Dutch one localises the whole app.
 *
 * An unmapped key is rendered visibly (monospace, marked) rather than
 * silently swallowed - a missing translation must look like a bug, because
 * it is one.
 */

/** Copy keys: everything a connector names but does not phrase. */
const KEYS = {
  // ── the error taxonomy, connector-api-spec.md section 5 ──────────────
  'connect.error.invalid_credentials': 'Those sign-in details were not accepted.',
  'connect.error.session_expired': 'This connection has expired.',
  'connect.error.mfa_failed': 'That code was not accepted.',
  'connect.error.mfa_timeout': 'Nobody answered in time, so the login was stopped.',
  'connect.error.challenge_expired': 'That question timed out before it was answered.',
  'connect.error.blocked_by_provider': 'The provider is refusing connections for this account.',
  'connect.error.provider_changed': 'The provider changed its site and we cannot read it right now. We have been told.',
  'connect.error.provider_unavailable': 'The provider is not answering.',
  'connect.error.rate_limited': 'Too many requests for now.',
  // Not phrased as a failure on purpose - see tone() below.
  'connect.error.agent_unavailable': 'Your own agent is not running.',
  'connect.error.unsupported_resource': 'This provider does not offer that.',
  'connect.error.invalid_request': 'That request was not something we could send.',
  'connect.error.consent_expired': 'We need your permission again before syncing.',
  'connect.error.reconciliation_failed': 'The numbers did not add up, so we refused the data rather than pass on something we cannot vouch for.',
  'connect.error.internal': 'Something broke on our side.',

  // ── field, step and note keys the two connectors actually emit ───────
  'connect.field.username': 'Username',
  'connect.field.password': 'Password',
  'connect.field.email': 'Email address',
  'connect.step.credentials': 'Your sign-in details',
  'connect.step.redirect': 'Sign in on the provider\'s own site',

  'connect.challenge.captcha': 'Type the characters shown in the picture.',
  // The grid. Mapped ahead of the adapter that will send it, because this is
  // the reference consumer and a missing string in the flagship path reads as
  // a bug in the feature rather than in the copy. An unmapped key still
  // degrades visibly, which is why mapping one early costs nothing.
  'connect.challenge.captcha_tiles':
    'Tap what the provider is asking for, then press "Send taps". This widget lives on the page - '
    + 'its own buttons are inside this picture, so tap those here too.',
  // Not something we can relay: an hCaptcha wants drags and clicks and hands
  // its answer straight to the provider, so the only person who can pass one
  // is whoever is sitting at the browser the agent opened.
  'connect.challenge.captcha_in_browser':
    'Solve the captcha in the browser window we opened for you. There is nothing to type here - once the provider lets us through, this page moves on by itself.',
  'connect.challenge.sms_code': 'Enter the code we just texted you.',

  'connect.ah.notes': 'We sign you in to Albert Heijn with these details, then keep the connection alive on our own. After this you will not need to sign in again.',
  // Kept for older bundles and manifests: AH used to ask the human to paste
  // the appie:// address its sign-in ended on. It does not any more, and an
  // unmapped key must degrade to something honest rather than to a raw
  // identifier - so the mapping stays until the key is gone everywhere.
  'connect.ah.paste_redirect': 'Paste the address the sign-in ended on',
  'connect.jumbo.notes': 'Jumbo sessions last about a day, so expect to sign in again tomorrow. That is Jumbo, not us.',
  'connect.lidl.country': 'Country',
  'connect.lidl.language': 'Language',
  'connect.lidl.phone': 'Mobile number',
  'connect.lidl.notes': 'Lidl Plus texts you a code every time you connect. After that, syncing needs nothing from you.',
  'connect.mock.notes': 'A fixture provider. Nothing leaves this machine.',

  'connect.mock_bank.notes': 'A fixture bank. Nothing leaves this machine.',
  'connect.mock_bank.persistent_notes': 'This one runs on an agent you own. The sealed bundle holds a pointer to your machine, not a password - which is why this connection survives closing the tab and the others do not.',
  'connect.mock_bank.step.credentials': 'Your sign-in details',
  'connect.mock_bank.step.pick_agent': 'Which of your agents holds this login',
  'connect.mock_bank.field.agent_id': 'Agent id',
  'connect.mock_bank.config.run_seconds': 'How long the fixture should take (seconds)',
  'connect.mock_bank.challenge.code_display': 'Key this number into your card reader and type back what it shows.',
  'connect.mock_bank.challenge.app_approval': 'Approve the sign-in in your banking app.',

  'connect.provider.changed': 'The provider\'s site changed and an engineer is on it.',
};

/**
 * Typed vocabulary. Not copy keys - closed enums the connector uses so a
 * consumer can render a progress bar or a badge instead of echoing a string.
 */
const VOCAB = {
  step: {
    queued: 'Queued',
    agent_assigned: 'Agent assigned',
    opening_provider: 'Opening the provider',
    authenticating: 'Signing in',
    awaiting_human: 'Waiting for you',
    selecting_accounts: 'Choosing accounts',
    downloading: 'Downloading',
    parsing: 'Reading',
    normalizing: 'Tidying up',
    finalizing: 'Finishing',
    logging_out: 'Signing out',
  },
  session_state: {
    queued: 'Queued',
    running: 'Running',
    awaiting_input: 'Waiting for you',
    active: 'Connected',
    needs_reauth: 'Needs signing in again',
    blocked: 'Blocked by the provider',
    disabled: 'Turned off',
    failed: 'Failed',
    expired: 'Expired',
  },
  job_state: {
    queued: 'Queued',
    leased: 'Assigned',
    running: 'Running',
    awaiting_input: 'Waiting for you',
    succeeded: 'Done',
    failed: 'Failed',
    expired: 'Timed out',
  },
  flow: {
    password: 'Username and password',
    password_sms: 'Password, then a texted code',
    password_totp: 'Password, then an authenticator code',
    two_step: 'Username first, then a second step',
    mobile_approval: 'Password, then approve in the app',
    challenge_response: 'Card reader challenge and response',
    qr_scan: 'Scan a QR with the provider app',
    oauth_redirect: 'Sign in on the provider\'s site',
    device_persistent: 'Already signed in on your own agent',
  },
  runtime: {
    http: 'T1 - no browser, ever',
    browser_once: 'T2 - browser for the first login only',
    browser_interactive: 'T3 - browser whenever the session goes stale',
    browser_persistent: 'T4 - a profile that stays signed in',
  },
  custody: {
    client: 'Your device holds the sealed bundle',
    server: 'The service vault holds the secret',
    agent: 'Your own agent holds it - we never have it',
  },
  web_support: {
    ephemeral: 'Web allowed, bundle dies with the tab',
    none: 'Not available to web clients',
  },
  agent_class: {
    inline: 'runs in the control plane',
    pooled: 'operator fleet',
    byo: 'your own machine',
  },
  provider_state: {
    healthy: 'Healthy',
    degraded: 'Degraded',
    paused: 'Paused',
    retired: 'Retired',
  },
  user_action: {
    none: 'Nothing to do',
    retry: 'Try again',
    reauth: 'Sign in again',
    reconnect: 'Reconnect',
    wait: 'Wait',
    start_your_agent: 'Start your agent',
  },
  /**
   * What the answer must BE, which the challenge type does not imply: an
   * image is a picture with a box beside it at one provider and a grid of
   * tiles at the next.
   */
  answer_kind: {
    text: 'Type an answer',
    taps: 'Tap the picture',
  },
  challenge: {
    image: 'Read the picture',
    qr_display: 'Scan this code',
    code_display: 'Type this into your device',
    mfa_code: 'Enter the code you were sent',
    app_approval: 'Approve it in the app',
    select_option: 'Pick one',
    redirect: 'Sign in on the provider\'s site',
  },
  service_kind: { bank: 'Banking', store: 'Shopping' },
  account_type: {
    current: 'Current account',
    savings: 'Savings',
    credit_card: 'Credit card',
    loan: 'Loan',
    unknown: 'Account',
  },
  transaction_kind: {
    card_payment: 'Card payment',
    transfer: 'Transfer',
    direct_debit: 'Direct debit',
    interest: 'Interest',
    fee: 'Fee',
    other: 'Other',
  },
  param: {
    since: 'From',
    until: 'Until',
    include: 'Include',
    accounts: 'Accounts',
  },
  field: {
    text: 'text', password: 'password', number: 'number', date: 'date',
    select: 'choice', iban: 'IBAN', phone: 'phone number',
  },
};

/** What the human should actually do, phrased as an instruction. */
const ACTION_HINTS = {
  none: 'Nothing you can do about this one.',
  retry: 'Try again in a moment.',
  reauth: 'Sign in again to bring this connection back.',
  reconnect: 'Set this connection up again from scratch.',
  wait: 'Give it time - the problem is at the provider\'s end.',
  start_your_agent: 'Start your agent, then try again.',
};

/**
 * agent_unavailable is not a failure. The user's own machine is switched
 * off; the only honest UI is an instruction, not a red banner. The whole
 * point of shipping `user_action` alongside `code` is that the consumer can
 * make exactly this distinction.
 */
const NOTICES = new Set(['agent_unavailable']);

/** Resolve a copy key. `missing: true` means nobody has written this string yet. */
export function key(name) {
  if (!name) return { text: '', missing: false, key: name };
  const text = KEYS[name];
  return text ? { text, missing: false, key: name } : { text: name, missing: true, key: name };
}

/** Resolve one value of a closed enum. Unknown values are surfaced, not hidden. */
export function term(group, value) {
  if (value === null || value === undefined || value === '') {
    return { text: '', missing: false, key: value };
  }
  const table = VOCAB[group] ?? {};
  const text = table[value];
  return text ? { text, missing: false, key: value } : { text: String(value), missing: true, key: value };
}

export function actionHint(action) {
  return ACTION_HINTS[action] ?? null;
}

export function tone(code) {
  return NOTICES.has(code) ? 'notice' : 'error';
}

/** Every key this app can render, for the "unmapped copy" self-check. */
export function knownKeys() {
  return Object.keys(KEYS);
}
