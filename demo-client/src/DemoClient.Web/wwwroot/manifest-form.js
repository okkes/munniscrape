/**
 * Manifest in, working form out.
 *
 * THE LOAD-BEARING PIECE. Everything a login form needs is declared in the
 * manifest - the order of the steps, each field's type, whether it is
 * secret, its validation pattern, its autofill hint, and the copy key for
 * its label - so this file never mentions a provider by name and never will.
 * The day the shop connector adds a provider with a `select` for country and
 * an `iban` field, this app renders it correctly without being rebuilt.
 *
 * That is the whole payoff of "the connector's API tells you what the
 * interface is". Special-casing one provider here would quietly delete it.
 */

import { h, sayKey, badge, note, today, daysAgo } from './render.js';
import * as copy from './copy.js';

/** `username` -> `Username`, only ever as a fallback for a missing label_key. */
function humanise(key) {
  return key.replaceAll('_', ' ').replace(/^./, (c) => c.toUpperCase());
}

function labelFor(spec, group) {
  if (spec.label_key) return sayKey(spec.label_key);
  const term = copy.term(group, spec.key);
  return term.missing ? humanise(spec.key) : term.text;
}

/**
 * Values this app already holds that a field would accept.
 *
 * The rule is the field's own declared pattern, never its key: an agent id we
 * know about is offered to any field whose pattern matches it, which is how a
 * BYO provider's "which agent" field gets a dropdown without this file
 * learning that such a field exists.
 */
function suggestionsFor(spec, pool) {
  if (!spec.pattern || spec.type !== 'text' || pool.length === 0) return [];

  let expression = null;
  try {
    expression = new RegExp(spec.pattern);
  } catch {
    return [];
  }

  return pool.filter((candidate) => expression.test(candidate));
}

/**
 * One control, by declared type. `iban` and `phone` are their own types
 * rather than "text with a hint" because the right keyboard on a phone and
 * the right autocapitalisation are part of getting the value at all.
 */
function control(spec, id, offered = []) {
  const shared = {
    id,
    name: spec.key,
    autocomplete: spec.autofill ?? 'off',
    required: spec.required !== false,
  };

  if (spec.type === 'select') {
    const select = h('select', { ...shared, class: 'control' },
      spec.required === false ? h('option', { value: '' }, '-') : null,
      (spec.options ?? []).map((option) => h('option', { value: option }, option)));
    return select;
  }

  const attributes = { ...shared, class: 'control' };
  if (spec.pattern) attributes.pattern = spec.pattern;
  if (offered.length > 0) attributes.list = `${id}-options`;

  switch (spec.type) {
    case 'password':
      attributes.type = 'password';
      break;
    case 'number':
      attributes.type = 'number';
      attributes.inputmode = 'numeric';
      break;
    case 'date':
      attributes.type = 'date';
      break;
    case 'iban':
      attributes.type = 'text';
      attributes.autocapitalize = 'characters';
      attributes.spellcheck = 'false';
      attributes.placeholder = 'NL91INGB0417164300';
      break;
    case 'phone':
      attributes.type = 'tel';
      attributes.inputmode = 'tel';
      attributes.placeholder = '+31612345678';
      break;
    default:
      attributes.type = 'text';
      break;
  }

  return h('input', attributes);
}

function row(spec, group, index, pool) {
  const id = `f-${group}-${index}-${spec.key}`;
  const offered = suggestionsFor(spec, pool);
  const input = control(spec, id, offered);
  const error = h('p', { class: 'form-error', id: `${id}-error` });

  const element = h('div', { class: 'form-row' },
    h('label', { for: id },
      labelFor(spec, 'field'),
      spec.required === false ? h('span', { class: 'muted small' }, ' optional') : null,
      // A secret is a fact from the manifest, not a guess from the type: it
      // is what the redactor keys off, so the UI says so out loud.
      spec.secret ? badge('secret', 'secret') : null),
    input,
    offered.length > 0
      ? h('datalist', { id: `${id}-options` }, offered.map((value) => h('option', { value })))
      : null,
    h('p', { class: 'form-hint' },
      h('code', {}, spec.key),
      h('code', { class: 'muted' }, spec.type),
      spec.pattern ? h('code', { class: 'muted' }, spec.pattern) : null),
    error);

  return { spec, element, input, error };
}

function check(entry) {
  const { spec, input, error } = entry;
  const value = input.value.trim();
  error.textContent = '';
  input.classList.remove('is-invalid');

  if (spec.required !== false && value === '') {
    error.textContent = 'Required.';
    input.classList.add('is-invalid');
    return false;
  }

  if (value !== '' && spec.pattern) {
    let expression = null;
    try {
      expression = new RegExp(spec.pattern);
    } catch {
      // A pattern we cannot compile is the manifest's problem, not the
      // user's. Let the value through rather than block a login on it.
      expression = null;
    }

    if (expression && !expression.test(value)) {
      error.textContent = 'That does not look right for this field.';
      input.classList.add('is-invalid');
      return false;
    }
  }

  return true;
}

/**
 * The login form: `auth.config` first, then every `auth.step` in order.
 *
 * Config comes first because it is chosen before anything else can be typed
 * - Lidl's country and language end up in the URLs the login itself uses -
 * and because it is not secret and is sealed into the bundle, so it is the
 * one part of the form the user never re-enters.
 */
export function authForm(manifest, { suggestions = [] } = {}) {
  const auth = manifest.auth ?? {};
  const configFields = auth.config ?? [];
  const steps = auth.steps ?? [];

  const configEntries = configFields.map((spec, i) => row(spec, 'config', i, suggestions));
  const stepEntries = [];

  const element = h('form', { class: 'manifest-form', novalidate: true, autocomplete: 'on' });

  if (configEntries.length > 0) {
    element.append(h('fieldset', { class: 'step step-config' },
      h('legend', {}, 'Settings'),
      note('Not credentials and not challenges: values this provider needs on every call. ',
        'They are sealed into the bundle, so they are asked once and never again.'),
      configEntries.map((entry) => entry.element)));
  }

  steps.forEach((step, stepIndex) => {
    const entries = (step.fields ?? []).map((spec, i) => row(spec, `s${stepIndex}`, i, suggestions));
    stepEntries.push(...entries);

    element.append(h('fieldset', { class: 'step' },
      h('legend', {}, step.label_key ? sayKey(step.label_key) : humanise(step.id ?? `Step ${stepIndex + 1}`)),
      entries.map((entry) => entry.element)));
  });

  if (configEntries.length === 0 && stepEntries.length === 0) {
    element.append(note('This provider asks for nothing at connect time.'));
  }

  const collect = (entries) => {
    const values = {};
    for (const { spec, input } of entries) {
      const value = input.value.trim();
      // An empty optional field is absent, not empty-string: the connector
      // validates against the manifest and "" is a value, not a silence.
      if (value !== '' || spec.required !== false) values[spec.key] = value;
    }
    return values;
  };

  return {
    element,

    validate() {
      // Every field is checked, not just the first failure: fixing one
      // problem at a time is how a login form wastes a provider's attempts.
      const results = [...configEntries, ...stepEntries].map(check);
      return results.every(Boolean);
    },

    values() {
      return { config: collect(configEntries), inputs: collect(stepEntries) };
    },

    fill({ config = {}, inputs = {} } = {}) {
      for (const entry of configEntries) {
        if (config[entry.spec.key] !== undefined) entry.input.value = String(config[entry.spec.key]);
      }
      for (const entry of stepEntries) {
        if (inputs[entry.spec.key] !== undefined) entry.input.value = String(inputs[entry.spec.key]);
      }
    },

    focusFirst() {
      const first = [...configEntries, ...stepEntries][0];
      first?.input.focus();
    },
  };
}

/**
 * The fetch form, generated the same way from a resource's ParamSpec.
 *
 * `internal: true` params are documented for operators and rejected from a
 * caller, so they are not rendered at all.
 */
export function paramForm(resource, limits) {
  const specs = (resource.params ?? []).filter((param) => !param.internal);
  const maxHistory = resource.max_history_days ?? limits?.max_history_days ?? 365;
  const earliest = daysAgo(maxHistory);

  const element = h('form', { class: 'manifest-form param-form', novalidate: true });
  const entries = [];

  specs.forEach((spec, index) => {
    const id = `p-${resource.id}-${index}-${spec.key}`;
    const term = copy.term('param', spec.key);
    const label = term.missing ? humanise(spec.key) : term.text;
    let input;
    let read;

    if (spec.type === 'enum' && spec.multi) {
      const boxes = (spec.values ?? []).map((value, i) => {
        const box = h('input', { type: 'checkbox', id: `${id}-${i}`, value });
        return { value, box };
      });
      input = h('div', { class: 'checks' },
        boxes.map(({ value, box }, i) => h('label', { class: 'check', for: `${id}-${i}` }, box, value)));
      read = () => boxes.filter(({ box }) => box.checked).map(({ value }) => value);
    } else if (spec.type === 'enum') {
      const select = h('select', { id, class: 'control' },
        spec.required ? null : h('option', { value: '' }, '-'),
        (spec.values ?? []).map((value) => h('option', { value }, value)));
      input = select;
      read = () => (select.value === '' ? undefined : select.value);
    } else if (spec.type === 'bool') {
      const box = h('input', { type: 'checkbox', id });
      input = h('label', { class: 'check', for: id }, box, 'yes');
      read = () => (box.checked ? true : undefined);
    } else if (spec.type === 'date') {
      // The window the manifest actually allows, expressed as the input's
      // own bounds. Asking for more than max_history_days is a 400 the user
      // should never be able to trigger.
      const box = h('input', { type: 'date', id, class: 'control', min: earliest, max: today() });
      if (spec.key === 'since') box.value = latest(earliest, daysAgo(30));
      input = box;
      read = () => (box.value === '' ? undefined : box.value);
    } else if (spec.type === 'number') {
      const box = h('input', { type: 'number', id, class: 'control', inputmode: 'numeric' });
      input = box;
      read = () => (box.value === '' ? undefined : Number(box.value));
    } else {
      const box = h('input', { type: 'text', id, class: 'control' });
      input = box;
      read = () => (box.value.trim() === '' ? undefined : box.value.trim());
    }

    const error = h('p', { class: 'form-error' });

    element.append(h('div', { class: 'form-row' },
      h('label', { for: id }, label, spec.required ? null : h('span', { class: 'muted small' }, ' optional')),
      input,
      h('p', { class: 'form-hint' },
        h('code', {}, spec.key),
        h('code', { class: 'muted' }, spec.multi ? `${spec.type}[]` : spec.type),
        spec.key === 'since' ? h('code', { class: 'muted' }, `max ${maxHistory}d`) : null),
      error));

    entries.push({ spec, read, error });
  });

  if (entries.length === 0) element.append(note('This resource takes no parameters.'));

  return {
    element,

    validate() {
      let ok = true;
      for (const entry of entries) {
        entry.error.textContent = '';
        const value = entry.read();
        const missing = value === undefined || value === '' || (Array.isArray(value) && value.length === 0);
        if (entry.spec.required && missing) {
          entry.error.textContent = 'Required.';
          ok = false;
        }
      }
      return ok;
    },

    values() {
      const params = {};
      for (const entry of entries) {
        const value = entry.read();
        if (value === undefined) continue;
        if (Array.isArray(value) && value.length === 0) continue;
        params[entry.spec.key] = value;
      }
      return params;
    },
  };
}

/** The later of two ISO dates. Lexical order is date order for YYYY-MM-DD. */
function latest(a, b) {
  if (a > b) return a;
  return b;
}
