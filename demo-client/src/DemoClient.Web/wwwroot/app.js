/**
 * The demo client: five screens over one relay.
 *
 * Read it as documentation. Every screen exists to make one property of the
 * platform visible rather than to look busy:
 *
 *   Providers    - the manifest is the contract, including its health
 *   Connect      - the login form is generated, never written per provider
 *   Live login   - typed progress and relayed challenges, no prose from us
 *   Connections  - web custody: the bundle dies with the tab
 *   Data         - normalised records, cursor, and ack as a real button
 *
 * The browser talks only to /api/*. It never learns a connector hostname,
 * never holds a connector credential, and never sees the resume ticket.
 */

import { api, toRelayError, watchLogin } from './api.js';
import * as store from './storage.js';
import * as copy from './copy.js';
import {
  h, mount, clear, badge, note, field, empty, spinner, errorBlock,
  countdown, records, dateTime, sayKey, sayTerm,
} from './render.js';
import { authForm, paramForm } from './manifest-form.js';
import { renderChallenge } from './challenges.js';

/** The typed progress vocabulary, in the order a run walks it. */
const STEP_ORDER = [
  'queued', 'agent_assigned', 'opening_provider', 'authenticating', 'awaiting_human',
  'selecting_accounts', 'downloading', 'parsing', 'normalizing', 'finalizing', 'logging_out',
];

const state = {
  services: [],
  catalogs: new Map(),
  agents: new Map(),
  selected: null,
  login: null,
  data: null,
  tab: 'providers',
};

/** Live sub-containers, so a poll can update progress without eating a half-typed answer. */
const live = {
  login: { progress: null, challenge: null, status: null, challengeId: null, challengeUi: null },
  data: { progress: null, challenge: null, challengeId: null, challengeUi: null },
  /** Countdown tickers owned by the connections screen, dropped on re-render. */
  clocks: [],
};

const sleep = (ms) => new Promise((resolve) => { setTimeout(resolve, ms); });
const el = (id) => document.getElementById(id);

// ── boot ──────────────────────────────────────────────────────────────────

async function boot() {
  for (const button of document.querySelectorAll('[data-tab]')) {
    button.addEventListener('click', () => switchTab(button.dataset.tab));
  }

  store.subscribe(() => {
    if (state.tab === 'connections') renderConnections();
    // The data screen deliberately does not re-render on every save: a
    // rotated bundle arrives in the middle of rendering its own results, and
    // a re-entrant repaint there would throw them away.
    if (state.tab === 'data' && state.data && !store.connection(state.data.connectionId)) renderData();
  });

  mount(el('providers-body'), spinner('Asking the relay which connectors are up...'));

  try {
    state.services = await api.services();
  } catch (error) {
    mount(el('providers-body'), errorBlock(error));
    return;
  }

  await Promise.all(state.services.filter((s) => s.reachable).map(loadService));
  renderProviders();
  renderConnections();
}

async function loadService(service) {
  // Catalogue and agents are independent; one failing must not blank the
  // other, so each records its own outcome.
  const [catalog, agents] = await Promise.allSettled([
    api.providers(service.key),
    api.agents(service.key),
  ]);

  if (catalog.status === 'fulfilled') state.catalogs.set(service.key, catalog.value);
  else state.catalogs.set(service.key, { error: catalog.reason });

  state.agents.set(service.key, agents.status === 'fulfilled' ? agents.value ?? [] : []);
}

function switchTab(name) {
  state.tab = name;

  for (const button of document.querySelectorAll('[data-tab]')) {
    button.classList.toggle('is-active', button.dataset.tab === name);
    button.setAttribute('aria-selected', String(button.dataset.tab === name));
  }
  for (const screen of document.querySelectorAll('.screen')) {
    screen.hidden = screen.dataset.screen !== name;
  }

  if (name === 'providers') renderProviders();
  if (name === 'connect') renderConnect();
  if (name === 'login') renderLogin();
  if (name === 'connections') renderConnections();
  if (name === 'data') renderData();
}

// ── shared lookups ────────────────────────────────────────────────────────

function catalogOf(serviceKey) {
  const catalog = state.catalogs.get(serviceKey);
  return catalog && !catalog.error ? catalog : null;
}

function manifestOf(serviceKey, providerId) {
  return catalogOf(serviceKey)?.providers?.find((p) => p.id === providerId) ?? null;
}

function serviceOf(key) {
  return state.services.find((s) => s.key === key) ?? { key, name: key, reachable: false };
}

function allProviders() {
  const out = [];
  for (const service of state.services) {
    for (const manifest of catalogOf(service.key)?.providers ?? []) out.push({ service, manifest });
  }
  return out;
}

/**
 * Why a provider cannot be connected right now, or null.
 *
 * Both reasons come straight off the manifest. `status` is the platform's
 * kill switch and the whole point of shipping it is that a consumer can say
 * something true instead of showing a spinner; `web_support: none` is a
 * provider declaring that repeating its login every visit is unreasonable.
 */
function refusal(manifest) {
  const status = manifest.status ?? {};
  if (status.state && status.state !== 'healthy') {
    return {
      title: `Connections are ${copy.term('provider_state', status.state).text.toLowerCase()}.`,
      reasonKey: status.reason_key ?? null,
      since: status.since ?? null,
    };
  }

  if (manifest.web_support === 'none') {
    return {
      title: 'Not available in a browser.',
      reasonKey: null,
      since: null,
      detail: 'This provider\'s login is too heavy to repeat on every visit, so it is native-only.',
    };
  }

  return null;
}

function toast(message, kind = 'info') {
  const node = h('div', { class: `toast toast-${kind}` }, message);
  el('toasts').append(node);
  setTimeout(() => node.remove(), 5000);
}

// ── screen 1: providers ───────────────────────────────────────────────────

function renderProviders() {
  const body = el('providers-body');
  const parts = [];

  for (const service of state.services) {
    if (!service.reachable) {
      parts.push(unreachableService(service));
      continue;
    }
    const catalog = state.catalogs.get(service.key);
    if (catalog?.error) parts.push(h('div', { class: 'panel' }, h('h3', {}, service.name), errorBlock(catalog.error)));
  }

  const cards = allProviders().map(({ service, manifest }) => providerCard(service, manifest));
  parts.push(cards.length > 0
    ? h('div', { class: 'grid' }, cards)
    : empty('No providers. Is a connector running?'));

  mount(body, parts);
}

function unreachableService(service) {
  // Honest degradation: naming the connector that is down beats a spinner
  // that never resolves, and it is exactly what a user would need to tell
  // support.
  return h('div', { class: 'panel panel-warn' },
    h('h3', {}, `${service.name ?? service.key} is not reachable`),
    note('Its connector is not answering, so nothing from it is offered here.'),
    service.error ? h('p', { class: 'alert-meta' }, h('code', {}, String(service.error))) : null);
}

function providerCard(service, manifest) {
  const agent = manifest.agent ?? {};
  const blocked = refusal(manifest);

  return h('article', { class: `card${blocked ? ' card-blocked' : ''}` },
    h('header', { class: 'card-head' },
      h('div', {},
        h('h3', {}, manifest.name),
        h('code', { class: 'muted' }, manifest.id)),
      statusPill(manifest.status ?? {})),

    h('div', { class: 'chips' },
      badge(copy.term('service_kind', manifest.kind).text || service.name, 'service'),
      badge(manifest.country ?? '', 'neutral'),
      badge(manifest.unattended ? 'unattended' : 'needs a human', manifest.unattended ? 'good' : 'warn'),
      agent.required
        ? badge(`agent: ${copy.term('agent_class', agent.class).text}`, 'info')
        : badge('no agent', 'neutral')),

    providerFacts(manifest),
    manifest.notes_key ? note(sayKey(manifest.notes_key)) : null,
    blocked ? blockedNotice(blocked) : connectAction(service, manifest));
}

function providerFacts(manifest) {
  const auth = manifest.auth ?? {};
  const agent = manifest.agent ?? {};

  return h('dl', { class: 'facts' },
    field('Runtime', sayTerm('runtime', manifest.runtime)),
    field('Login', sayTerm('flow', auth.flow)),
    field('Custody', sayTerm('custody', manifest.secret_custody)),
    field('Web', sayTerm('web_support', manifest.web_support ?? 'ephemeral')),
    field('Session', sessionSummary(auth.session)),
    field('May ask for', challengeBadges(auth.challenges)),
    field('Resources', (manifest.resources ?? []).map((r) => h('code', {}, r.id))),
    agent.egress ? field('Egress', `${agent.egress.country} ${agent.egress.kind}`) : null);
}

function sessionSummary(session) {
  if (!session) return 'n/a';
  const hours = Math.round((session.ttl_seconds ?? 0) / 3600);
  // rotates_on_use is the instruction "persist the bundle from every
  // response", which is why it belongs on the card and not in a footnote.
  return session.rotates_on_use ? `${hours}h, rotates on use` : `${hours}h`;
}

function challengeBadges(challenges) {
  const list = challenges ?? [];
  if (list.length === 0) return 'nothing';
  return list.map((type) => badge(type, 'challenge'));
}

function blockedNotice(blocked) {
  return h('div', { class: 'alert alert-notice' },
    h('div', { class: 'alert-head' }, badge('unavailable', 'warn'), h('strong', {}, blocked.title)),
    blocked.reasonKey ? h('p', { class: 'alert-hint' }, sayKey(blocked.reasonKey)) : null,
    blocked.detail ? h('p', { class: 'alert-hint' }, blocked.detail) : null,
    blocked.since ? h('p', { class: 'alert-meta' }, h('code', {}, `since ${dateTime(blocked.since)}`)) : null);
}

function connectAction(service, manifest) {
  return h('div', { class: 'card-actions' },
    h('button', {
      class: 'primary',
      onclick: () => {
        state.selected = { service: service.key, provider: manifest.id };
        switchTab('connect');
      },
    }, 'Connect'));
}

function statusPill(status) {
  const state_ = status.state ?? 'healthy';
  const kind = { healthy: 'good', degraded: 'warn', paused: 'bad', retired: 'bad' }[state_] ?? 'neutral';
  return badge(copy.term('provider_state', state_).text || state_, kind);
}

// ── screen 2: connect ─────────────────────────────────────────────────────

function renderConnect() {
  const body = el('connect-body');
  const options = allProviders().filter(({ manifest }) => !refusal(manifest));

  if (options.length === 0) {
    mount(body, empty('Nothing connectable right now. Check the Providers tab.'));
    return;
  }

  if (!state.selected || !manifestOf(state.selected.service, state.selected.provider)) {
    state.selected = { service: options[0].service.key, provider: options[0].manifest.id };
  }

  const picker = h('select', { class: 'control' },
    options.map(({ service, manifest }) => h('option', {
      value: `${service.key}/${manifest.id}`,
      selected: service.key === state.selected.service && manifest.id === state.selected.provider,
    }, `${manifest.name} - ${service.name ?? service.key}`)));

  picker.addEventListener('change', () => {
    const [service, provider] = picker.value.split('/');
    state.selected = { service, provider };
    renderConnect();
  });

  const service = serviceOf(state.selected.service);
  const manifest = manifestOf(state.selected.service, state.selected.provider);
  const agentIds = liveAgentIds(service.key);

  // Values we already hold are offered to any field whose declared pattern
  // accepts them. No field key is named here, so a provider that asks for an
  // agent by some other key gets the same help for free.
  const form = authForm(manifest, { suggestions: agentIds });
  const routing = agentRouting(manifest, agentIds);

  const label = h('input', { class: 'control', type: 'text', placeholder: 'Groceries', maxlength: 60 });
  const errors = h('div', { class: 'connect-errors' });
  const submit = h('button', { class: 'primary', type: 'submit' }, 'Connect');

  const prefill = h('button', { class: 'ghost', type: 'button' }, 'Prefill');
  prefill.addEventListener('click', async () => {
    prefill.disabled = true;
    try {
      const values = await api.prefill(service.key, manifest.id);
      if (!values) {
        toast('Prefill is a Development-only convenience; the relay does not serve it here.', 'warn');
      } else if (Object.keys(values.inputs ?? {}).length === 0 && Object.keys(values.config ?? {}).length === 0) {
        toast(`No saved credentials for ${manifest.id}.`, 'warn');
      } else {
        form.fill(values);
        toast('Filled from the local credentials file.');
      }
    } catch (error) {
      mount(errors, errorBlock(error));
    } finally {
      prefill.disabled = false;
    }
  });

  const shell = h('form', { class: 'connect-form', novalidate: true },
    h('div', { class: 'form-row' },
      h('label', {}, 'Your name for this connection', h('span', { class: 'muted small' }, ' optional')),
      label,
      h('p', { class: 'form-hint' }, h('code', {}, 'label'),
        h('span', { class: 'muted' }, 'echoed back to you, never sent to the provider'))),
    routing?.element ?? null,
    form.element,
    errors,
    h('div', { class: 'card-actions' }, submit, prefill));

  shell.addEventListener('submit', async (event) => {
    event.preventDefault();
    clear(errors);
    if (!form.validate()) {
      mount(errors, note('Fix the highlighted fields. The patterns come from the manifest, not from this app.'));
      return;
    }

    if (routing && !routing.value()) {
      mount(errors, note('This provider keeps its login on one machine, so the run has to be pointed at that agent.'));
      return;
    }

    submit.disabled = true;
    submit.textContent = 'Connecting...';
    try {
      await startLogin(service, manifest, {
        ...form.values(),
        label: label.value.trim() || undefined,
        prefer_agent: routing?.value() || undefined,
      });
    } catch (error) {
      mount(errors, errorBlock(error));
    } finally {
      submit.disabled = false;
      submit.textContent = 'Connect';
    }
  });

  mount(body,
    h('div', { class: 'panel' },
      h('div', { class: 'form-row' }, h('label', {}, 'Provider'), picker),
      h('div', { class: 'chips' },
        badge(copy.term('flow', manifest.auth?.flow).text, 'info'),
        badge(copy.term('runtime', manifest.runtime).text, 'neutral'),
        badge(copy.term('custody', manifest.secret_custody).text, 'neutral')),
      manifest.notes_key ? note(sayKey(manifest.notes_key)) : null),

    h('div', { class: 'panel' },
      h('h3', {}, 'Sign in'),
      note('Every field below was generated from this provider\'s manifest - its type, its pattern, ',
        'its label key and its autofill hint. Nothing in this app knows what "',
        h('code', {}, manifest.id), '" is.'),
      shell),

    agentsPanel(service, manifest));
}

/**
 * An agent is online if it has beaten recently - three missed beats, not one,
 * matching the connector's own rule. Revoked wins over both: a revoked agent
 * that is still running is not a usable one.
 */
function agentBadge(row) {
  if (row.revoked) return badge('revoked', 'bad');

  const beat = row.last_heartbeat_at ? Date.now() - new Date(row.last_heartbeat_at).getTime() : Infinity;
  const online = row.online ?? beat < 90_000;
  return badge(online ? 'online' : 'offline', online ? 'good' : 'warn');
}

function liveAgentIds(serviceKey) {
  return (state.agents.get(serviceKey) ?? [])
    .filter((row) => !row.revoked)
    .map((row) => row.agent_id ?? row.id)
    .filter(Boolean);
}

/**
 * Where the run has to happen, for a provider whose login lives on one
 * machine. `prefer_agent` is routing, not a credential and not an input: a
 * persistent profile exists on exactly one host, so the session is pinned to
 * it before any job exists. Driven by the runtime tier, never by a name.
 */
function agentRouting(manifest, agentIds) {
  if (manifest.runtime !== 'browser_persistent') return null;

  const select = h('select', { class: 'control' },
    h('option', { value: '' }, agentIds.length > 0 ? '- pick one -' : 'no agent has checked in'),
    agentIds.map((id) => h('option', { value: id }, id)));

  const element = h('div', { class: 'form-row' },
    h('label', {}, 'Run it on'),
    select,
    h('p', { class: 'form-hint' },
      h('code', {}, 'prefer_agent'),
      h('span', { class: 'muted' }, 'routing only - the bundle will hold a pointer to this machine, not a secret')));

  return { element, value: () => select.value };
}

/**
 * BYO agents. Shown when the manifest says the provider needs one, because
 * "your sync is stale because your VM has been off for three days" is a real
 * message and the only way to say it is to show the agent's health.
 */
function agentsPanel(service, manifest) {
  const agent = manifest.agent ?? {};
  if (!agent.required || agent.class !== 'byo') return null;

  const agents = state.agents.get(service.key) ?? [];

  return h('div', { class: 'panel' },
    h('h3', {}, 'Your agents'),
    note('This provider runs on hardware you own. The sealed bundle will hold a pointer to it, not a password.'),
    agents.length === 0
      ? h('div', { class: 'alert alert-notice' },
        h('div', { class: 'alert-head' }, badge('Start your agent', 'info'),
          h('strong', {}, 'No agent has checked in for you yet.')),
        h('p', { class: 'alert-hint' }, 'Enroll and start an agent, then reload this page.'))
      : h('ul', { class: 'records' }, agents.map((row) => {
        const id = row.agent_id ?? row.id;

        return h('li', { class: 'record' },
          h('div', { class: 'record-head' },
            h('div', {},
              h('strong', {}, row.name ?? id),
              h('div', { class: 'muted small' }, `${copy.term('agent_class', row.class).text} - last beat ${dateTime(row.last_heartbeat_at)}`)),
            agentBadge(row)),
          h('div', { class: 'record-meta' },
            h('code', {}, id),
            h('button', {
              class: 'ghost small-button',
              type: 'button',
              onclick: () => {
                navigator.clipboard?.writeText(id);
                toast('Agent id copied.');
              },
            }, 'Copy id')),
          (row.profiles ?? []).length > 0
            ? h('div', { class: 'record-meta' }, row.profiles.map((p) => h('code', {},
              `${p.provider}: ${p.healthy ? 'healthy' : 'unhealthy'}`)))
            : null);
      })));
}

// ── screen 3: live login ──────────────────────────────────────────────────

async function startLogin(service, manifest, body) {
  // A second login must not leave the first run's stream open: it would keep
  // reporting into a screen that has moved on.
  state.login?.stop?.();

  const view = await api.login(service.key, manifest.id, body);

  state.login = {
    service, manifest, sessionId: view.session_id, view, error: null, stop: null, finished: false,
  };

  switchTab('login');

  if (isSettled(view.state)) {
    await settle(view);
    return;
  }

  // The stream carries progress and challenges but never the bundle: a
  // stream cannot acknowledge receipt, so the bundle is collected with one
  // GET the moment the session goes active.
  state.login.stop = watchLogin(service.key, manifest.id, view.session_id, {
    onState: (next) => {
      if (!state.login || state.login.sessionId !== next.session_id) return;
      state.login.view = next;
      updateLogin();
      if (isSettled(next.state)) settle(next);
    },
    onError: () => {
      if (state.login && !state.login.finished) updateLogin('The event stream dropped; reconnecting.');
    },
  });
}

function isSettled(sessionState) {
  return ['active', 'failed', 'expired', 'blocked'].includes(sessionState);
}

async function settle(view) {
  const run = state.login;
  if (!run || run.finished) return;
  run.finished = true;
  run.stop?.();
  run.stop = null;

  if (view.state !== 'active') {
    updateLogin();
    return;
  }

  try {
    // A bundle is handed over exactly once, and either the POST or this GET
    // carried it - never both. Hence the fallback rather than an assumption
    // about which call won.
    const settled = view.bundle ? view : await api.loginStatus(run.service.key, run.manifest.id, run.sessionId);
    run.view = { ...settled, bundle: settled.bundle ?? view.bundle };
    run.connectionId = persist(run.service.key, run.manifest, run.view);
  } catch (error) {
    run.error = error;
  }

  updateLogin();
  renderConnections();
}

/**
 * Write the connection row. Called with whatever response carried the
 * bundle, and never with a stale one: a bundle is handed over exactly once.
 */
function persist(serviceKey, manifest, view) {
  const id = store.idFor(serviceKey, manifest.id, view.session_id);

  const row = {
    id,
    service: serviceKey,
    provider: manifest.id,
    providerName: manifest.name,
    sessionId: view.session_id,
    state: view.state,
    label: view.label ?? null,
    custody: view.custody ?? 'ephemeral',
    secretCustody: manifest.secret_custody,
    expiresAt: view.expires_at ?? null,
    config: view.config ?? {},
    account: view.provider_account ?? null,
    connectedAt: new Date().toISOString(),
  };

  if (view.bundle) {
    row.bundle = view.bundle;
    row.bundleIssuedAt = new Date().toISOString();
  }

  store.save(row);
  return id;
}

function renderLogin() {
  const body = el('login-body');
  const run = state.login;

  live.login.challengeUi?.dispose();
  live.login = { progress: null, challenge: null, status: null, challengeId: null, challengeUi: null };

  if (!run) {
    mount(body, empty('No login in progress. Start one from the Connect tab.'));
    return;
  }

  live.login.progress = h('div', { class: 'progress-box' });
  live.login.challenge = h('div', { class: 'challenge-box' });
  live.login.status = h('div', { class: 'status-box' });

  mount(body,
    h('div', { class: 'panel' },
      h('header', { class: 'card-head' },
        h('div', {},
          h('h3', {}, run.manifest.name),
          h('code', { class: 'muted' }, run.sessionId)),
        h('div', { id: 'login-state' })),
      note('Progress below is a closed enum sent by the connector - ',
        h('code', {}, 'step'), ' plus ', h('code', {}, 'steps_done'),
        '. The words are this app\'s; the connector never sends prose.'),
      live.login.progress),
    live.login.challenge,
    live.login.status);

  updateLogin();
}

function updateLogin(streamNote) {
  const run = state.login;
  if (!run || !live.login.progress) return;

  const view = run.view;
  const stateNode = el('login-state');
  if (stateNode) mount(stateNode, badge(copy.term('session_state', view.state).text, stateKind(view.state)));

  mount(live.login.progress, progressTrail(view.progress));

  // The challenge element is rebuilt only when the challenge itself changes,
  // so a progress frame arriving mid-typing does not wipe what was typed.
  const challenge = view.challenge ?? null;
  if ((challenge?.id ?? null) !== live.login.challengeId) {
    live.login.challengeId = challenge?.id ?? null;
    live.login.challengeUi?.dispose();
    live.login.challengeUi = null;
    clear(live.login.challenge);

    if (challenge) {
      live.login.challengeUi = renderChallenge(challenge, {
        onAnswer: (value) => api.answerLogin(run.service.key, run.manifest.id, run.sessionId, challenge.id, value),
        loadImage: () => api.challengeImage(run.service.key, run.manifest.id, run.sessionId, challenge.id),
        // Derived from ids this tab already holds, exactly as the image URL is.
        // A live view adds no hostname and no path to any payload.
        live: {
          frame: (after, signal) =>
            api.liveFrame(run.service.key, run.manifest.id, run.sessionId, challenge.id, after, signal),
          input: (events) =>
            api.liveInput(run.service.key, run.manifest.id, run.sessionId, challenge.id, events),
        },
      });
      live.login.challenge.append(live.login.challengeUi.element);
    }
  }

  const parts = [];
  if (run.error) parts.push(errorBlock(run.error));

  if (view.state === 'active') {
    parts.push(h('div', { class: 'panel panel-good' },
      h('h3', {}, 'Connected'),
      view.provider_account?.display_name ? note(view.provider_account.display_name) : null,
      h('div', { class: 'chips' },
        badge(`custody: ${view.custody ?? 'ephemeral'}`, 'custody'),
        view.expires_at ? badge(`expires ${dateTime(view.expires_at)}`, 'neutral') : null),
      note('The sealed bundle is in this tab\'s sessionStorage and nowhere else.'),
      h('div', { class: 'card-actions' },
        h('button', { class: 'primary', onclick: () => switchTab('data') }, 'Fetch data'),
        h('button', { class: 'ghost', onclick: () => switchTab('connections') }, 'See connections'))));
  }

  if (['failed', 'expired', 'blocked'].includes(view.state)) {
    parts.push(h('div', { class: 'panel panel-warn' },
      h('h3', {}, `This login ${copy.term('session_state', view.state).text.toLowerCase()}`),
      note('Start again from the Connect tab.')));
  }

  if (streamNote) parts.push(note(streamNote));
  mount(live.login.status, parts);
}

/** One colour map for session and job states - they share a vocabulary. */
function stateKind(runState) {
  return {
    active: 'good', succeeded: 'good',
    running: 'info', queued: 'info', leased: 'info',
    awaiting_input: 'warn', needs_reauth: 'warn',
    failed: 'bad', expired: 'bad', blocked: 'bad',
    disabled: 'neutral',
  }[runState] ?? 'neutral';
}

/**
 * The trail. Rendering the whole enum, not just what has happened, is what
 * makes it a trail rather than a status line - and it is only possible
 * because the vocabulary is closed.
 */
function progressTrail(progress) {
  if (!progress) return note('No progress reported yet.');

  const done = new Set(progress.steps_done ?? []);
  const current = progress.step;
  const known = new Set(STEP_ORDER);
  const extra = [...done, current].filter((s) => s && !known.has(s));

  return h('ol', { class: 'trail' },
    [...STEP_ORDER, ...extra].map((step) => {
      const isCurrent = step === current;

      return h('li', { class: `trail-step ${trailKind(step, current, done)}` },
        h('span', { class: 'trail-dot', 'aria-hidden': 'true' }),
        sayTerm('step', step),
        isCurrent ? h('span', { class: 'trail-spin', 'aria-hidden': 'true' }) : null);
    }));
}

function trailKind(step, current, done) {
  if (step === current) return 'is-current';
  return done.has(step) ? 'is-done' : 'is-pending';
}

// ── screen 4: connections ─────────────────────────────────────────────────

function renderConnections() {
  const body = el('connections-body');
  const rows = store.connections();

  // Countdowns register with a page-wide ticker, so a re-render has to drop
  // the old ones or the page accumulates timers for cards nobody can see.
  for (const dispose of live.clocks) dispose();
  live.clocks = [];

  if (rows.length === 0) {
    mount(body, empty('No connections in this tab. Connect one, and it will live here until the tab closes.'));
    return;
  }

  mount(body, h('div', { class: 'grid' }, rows.map(connectionCard)));
}

function connectionCard(row) {
  const manifest = manifestOf(row.service, row.provider);
  const expiry = row.expiresAt ? countdown(row.expiresAt, { prefix: 'in ' }) : null;
  if (expiry) live.clocks.push(expiry.dispose);
  const pointerOnly = row.secretCustody === 'agent';

  const disconnect = h('button', { class: 'ghost' }, 'Disconnect');
  const actions = h('div', { class: 'card-actions' },
    h('button', {
      class: 'primary',
      onclick: () => {
        state.data = { connectionId: row.id, resourceId: null, result: null };
        switchTab('data');
      },
    }, 'Fetch data'),
    disconnect);

  const errorSlot = h('div', {});

  disconnect.addEventListener('click', async () => {
    disconnect.disabled = true;
    disconnect.textContent = 'Disconnecting...';
    try {
      await api.disconnect(row.service, row.provider, row.sessionId, row.bundle ?? '');
      // Purge locally only after the connector confirms, so a failed
      // upstream logout cannot orphan a session that is still live there.
      store.forget(row.id);
      toast('Disconnected, and the connector logged out upstream.');
    } catch (error) {
      mount(errorSlot, errorBlock(error),
        h('div', { class: 'card-actions' },
          h('button', {
            class: 'ghost small-button',
            onclick: () => {
              store.forget(row.id);
              toast('Forgotten here. The provider may still hold the session.', 'warn');
            },
          }, 'Forget it here anyway')));
      disconnect.disabled = false;
      disconnect.textContent = 'Disconnect';
    }
  });

  return h('article', { class: 'card' },
    h('header', { class: 'card-head' },
      h('div', {},
        h('h3', {}, row.label || row.providerName || row.provider),
        h('code', { class: 'muted' }, `${row.service}/${row.provider}`)),
      badge(copy.term('session_state', row.state).text, stateKind(row.state))),

    h('div', { class: 'chips' },
      // The custody badge, spelled out. A user who does not know why they
      // are signing in again every visit will assume the app is broken.
      badge('ephemeral - this bundle dies with the tab', 'custody'),
      pointerOnly ? badge('bundle holds an agent pointer, not a secret', 'good') : null),

    h('dl', { class: 'facts' },
      field('Session', h('code', {}, row.sessionId)),
      field('Bundle', row.bundle
        ? h('span', {}, h('code', {}, `${row.bundle.slice(0, 11)}...`),
          h('span', { class: 'muted small' }, ` sealed, ${row.bundle.length} chars, opaque to this app`))
        : h('span', { class: 'muted' }, 'gone - sign in again to sync')),
      field('Expires', expiry ? expiry.element : 'n/a'),
      field('Connected', dateTime(row.connectedAt)),
      row.account?.display_name ? field('Account', row.account.display_name) : null,
      Object.keys(row.config ?? {}).length > 0
        ? field('Config', Object.entries(row.config).map(([k, v]) => h('code', {}, `${k}=${v}`)))
        : null,
      manifest ? null : field('Manifest', h('span', { class: 'muted' }, 'its connector is not reachable right now'))),

    errorSlot,
    actions);
}

// ── screen 5: data ────────────────────────────────────────────────────────

function renderData() {
  const body = el('data-body');
  const rows = store.connections();

  live.data.challengeUi?.dispose();
  live.data = { progress: null, challenge: null, challengeId: null, challengeUi: null };

  if (rows.length === 0) {
    mount(body, empty('Connect something first - a fetch needs a bundle.'));
    return;
  }

  if (!state.data || !rows.some((r) => r.id === state.data.connectionId)) {
    state.data = { connectionId: rows[0].id, resourceId: null, result: null };
  }

  const row = store.connection(state.data.connectionId);
  const manifest = manifestOf(row.service, row.provider);

  const picker = h('select', { class: 'control' },
    rows.map((candidate) => h('option', {
      value: candidate.id, selected: candidate.id === state.data.connectionId,
    }, `${candidate.label || candidate.providerName || candidate.provider}`)));

  picker.addEventListener('change', () => {
    state.data = { connectionId: picker.value, resourceId: null, result: null };
    renderData();
  });

  const head = h('div', { class: 'panel' },
    h('div', { class: 'form-row' }, h('label', {}, 'Connection'), picker));

  if (!manifest) {
    mount(body, head, h('div', { class: 'panel panel-warn' },
      h('h3', {}, 'That connector is not reachable'),
      note('Without its manifest there is no resource list and no parameter spec, so there is nothing honest to offer.')));
    return;
  }

  if (store.needsSignin(row)) {
    mount(body, head, h('div', { class: 'panel panel-warn' },
      h('h3', {}, 'Sign in again to sync'),
      note('This tab holds no bundle for the connection. That is the normal state of a web connection ',
        'between visits, not a failure - the row is real, the credential simply did not outlive the tab.'),
      h('div', { class: 'card-actions' },
        h('button', { class: 'primary', onclick: () => { state.selected = { service: row.service, provider: row.provider }; switchTab('connect'); } },
          'Sign in again'))));
    return;
  }

  const resources = manifest.resources ?? [];
  if (!state.data.resourceId || !resources.some((r) => r.id === state.data.resourceId)) {
    state.data.resourceId = resources[0]?.id ?? null;
  }

  const resource = resources.find((r) => r.id === state.data.resourceId);
  if (!resource) {
    mount(body, head, empty('This provider offers no resources.'));
    return;
  }

  const tabs = h('div', { class: 'pill-tabs' }, resources.map((r) => h('button', {
    class: `pill${r.id === resource.id ? ' is-active' : ''}`,
    onclick: () => {
      state.data = { ...state.data, resourceId: r.id, result: null };
      renderData();
    },
  }, r.id)));

  const params = paramForm(resource, manifest.limits);
  const runButton = h('button', { class: 'primary', type: 'submit' }, 'Fetch');
  live.data.progress = h('div', { class: 'progress-box' });
  live.data.challenge = h('div', { class: 'challenge-box' });
  const results = h('div', { class: 'results' });

  const form = h('form', { class: 'connect-form', novalidate: true },
    params.element,
    h('div', { class: 'card-actions' }, runButton));

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (!params.validate()) return;

    runButton.disabled = true;
    runButton.textContent = 'Fetching...';
    clear(results);
    clear(live.data.progress);

    try {
      await runFetch(row, resource, params.values(), results);
    } catch (error) {
      mount(results, errorBlock(error));
    } finally {
      runButton.disabled = false;
      runButton.textContent = 'Fetch';
    }
  });

  mount(body,
    head,
    h('div', { class: 'panel' },
      h('h3', {}, 'Resource'),
      tabs,
      h('div', { class: 'chips' },
        badge(`returns ${resource.returns}`, 'neutral'),
        badge(`~${resource.typical_duration_seconds ?? 30}s`, 'neutral'),
        resource.max_history_days ? badge(`${resource.max_history_days}d history`, 'neutral') : null),
      note('These inputs are the resource\'s ', h('code', {}, 'params'), ' from the manifest. ',
        'The relay resumes the session, holds the ticket, fetches, and hands the ticket to nobody.'),
      form),
    live.data.progress,
    live.data.challenge,
    results);

  if (state.data.result) deliverInto(results, row, resource, state.data.result);
}

async function runFetch(row, resource, params, results) {
  const response = await api.fetchResource(row.service, row.provider, resource.id, {
    bundle: row.bundle,
    params,
  });

  if (response.job_id) {
    await followJob(row, resource, response, results);
    return;
  }

  deliver(row, resource, response, results);
}

/**
 * The 202 path. A fetch is a job: it can run for a minute and it can stop to
 * ask a question, because a T3 provider re-authenticates on every run.
 */
async function followJob(row, resource, accepted, results) {
  let view = accepted;
  // The box we are writing into. If the screen is rebuilt under us - another
  // connection picked, another resource, a disconnect - this poll is writing
  // into a detached node and should stop rather than run on invisibly.
  const box = live.data.progress;
  const deadline = Date.now() + 10 * 60_000;

  mount(box, jobPanel(view));
  paintJobChallenge(row, view);

  for (;;) {
    if (['succeeded', 'failed', 'expired'].includes(view.state)) break;
    if (live.data.progress !== box) return;
    if (Date.now() > deadline) throw toRelayError({
      code: 'mfa_timeout', retriable: true, user_action: 'retry', message_key: 'connect.error.mfa_timeout',
    }, 408);

    await sleep(1200);

    view = await api.job(row.service, row.provider, accepted.job_id);
    if (live.data.progress !== box) return;
    mount(box, jobPanel(view));
    paintJobChallenge(row, view);
  }

  if (view.state !== 'succeeded') {
    throw toRelayError(view.error ?? { code: 'internal', retriable: true, user_action: 'retry', message_key: 'connect.error.internal' }, 502);
  }

  deliver(row, resource, view, results);
}

function jobPanel(view) {
  return h('div', { class: 'panel' },
    h('header', { class: 'card-head' },
      h('div', {}, h('h3', {}, 'Fetch in flight'), h('code', { class: 'muted' }, view.job_id)),
      badge(copy.term('job_state', view.state).text, stateKind(view.state))),
    progressTrail(view.progress));
}

function paintJobChallenge(row, view) {
  const challenge = view.challenge ?? null;
  if ((challenge?.id ?? null) === live.data.challengeId) return;

  live.data.challengeId = challenge?.id ?? null;
  live.data.challengeUi?.dispose();
  live.data.challengeUi = null;
  clear(live.data.challenge);

  if (!challenge) return;

  // The 202 handle carries no session id - only the job view does - so the
  // stored one is the fallback. It is the same session either way: the bundle
  // seals the session id, and resuming it is what produced this job.
  const sessionId = view.session_id ?? row.sessionId;

  live.data.challengeUi = renderChallenge(challenge, {
    onAnswer: (value) => api.answerJob(row.service, row.provider, view.job_id, challenge.id, value),
    // A mid-fetch challenge's image lives under its session, exactly as a
    // login challenge does - one route, both cases.
    loadImage: () => api.challengeImage(row.service, row.provider, sessionId, challenge.id),
    // And so does its live view. A re-login in the middle of a fetch is the
    // case this exists for.
    live: {
      frame: (after, signal) =>
        api.liveFrame(row.service, row.provider, sessionId, challenge.id, after, signal),
      input: (events) => api.liveInput(row.service, row.provider, sessionId, challenge.id, events),
    },
  });
  live.data.challenge.append(live.data.challengeUi.element);
}

/**
 * Render the page AND persist the rotated bundle, in one step.
 *
 * These belong together. A crash between them costs at most one sync if the
 * bundle was written first, and the whole connection if it was not - so the
 * write happens here, next to the data it arrived with, rather than in a
 * tidier place further away.
 */
function deliver(row, resource, payload, results) {
  if (payload.session?.bundle) store.rotateBundle(row.id, payload.session.bundle);

  state.data.result = {
    resourceId: resource.id,
    shape: resource.returns,
    data: payload.data ?? [],
    cursor: payload.cursor ?? null,
    complete: payload.complete !== false,
    rotated: Boolean(payload.session?.rotated),
    at: new Date().toISOString(),
  };

  deliverInto(results, row, resource, state.data.result);
}

function deliverInto(results, row, resource, result) {
  const ackBox = h('div', {});

  const ack = h('button', { class: 'ghost' }, 'Ack (purge staged data)');
  ack.addEventListener('click', async () => {
    ack.disabled = true;
    try {
      // The bundle may have rotated on the fetch that produced this cursor,
      // so read it back rather than closing over the one we started with.
      const current = store.connection(row.id);
      const response = await api.ack(row.service, row.provider, resource.id, {
        bundle: current?.bundle ?? row.bundle,
        cursor: result.cursor,
      });
      mount(ackBox, note(`Purged ${response?.purged ?? 0} staged row${response?.purged === 1 ? '' : 's'}. `,
        'The connector has forgotten what it just handed over.'));
    } catch (error) {
      mount(ackBox, errorBlock(error));
      ack.disabled = false;
    }
  });

  mount(results,
    h('div', { class: 'panel' },
      h('header', { class: 'card-head' },
        h('div', {}, h('h3', {}, `${result.data.length} ${resource.id}`),
          h('code', { class: 'muted' }, `fetched ${dateTime(result.at)}`)),
        h('div', { class: 'chips' },
          badge(result.complete ? 'complete' : 'more to come', result.complete ? 'good' : 'warn'),
          result.rotated ? badge('bundle rotated', 'custody') : null)),

      result.cursor
        ? h('div', { class: 'ack-row' },
          field('Cursor', h('code', {}, result.cursor)),
          ack)
        : note('No cursor: nothing was staged, so there is nothing to ack.'),

      note('Ack is what keeps the connector a pipe. Acknowledged rows are deleted immediately; ',
        'anything unacknowledged dies after seven days regardless.'),
      ackBox),

    records(result.shape, result.data));
}

await boot();
