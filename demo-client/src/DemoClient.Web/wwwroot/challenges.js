/**
 * The eight ways a provider can stop and ask the human something.
 *
 * A challenge is relayed, never solved. That line - we hand the question to
 * the person who owns the account and hand their answer back - is what
 * separates acting as someone's agent from abusing a provider, and it is why
 * every one of these renderers is a piece of UI rather than a piece of
 * cleverness.
 *
 * `answer_kind` decides which renderer runs, not `type`. An image is a
 * picture with a box beside it at one provider and a grid of tiles at the
 * next, and the type cannot tell those apart - which is exactly why the field
 * exists. A tile grid cannot be answered in text, so it is answered in taps:
 * the picture comes out, the human touches it in their own app, and the
 * normalised points go back. Nothing here solves anything.
 *
 * The eighth is `live_view` and it is the odd one: not a question with an
 * answer but the provider's own page, streamed, with the human's pointer and
 * keyboard on it. It rides this machinery anyway because everything the
 * machinery provides - an expiry, a renderer chosen by the consumer, a visible
 * degradation when the consumer has never heard of the type - is exactly what
 * it needs too. `live-view.js` holds it.
 *
 * Every challenge carries `expires_at`, and every renderer here counts down
 * to it and disables itself on the way past. A challenge holds a live
 * browser open on an agent; a form still accepting input for a question that
 * timed out two minutes ago is lying to the user.
 */

import { h, badge, note, spinner, countdown, sayKey, sayTerm, errorBlock, mount } from './render.js';
import { liveViewChallenge } from './live-view.js';

/**
 * @param challenge  the ChallengeView off the wire
 * @param onAnswer   (value) => Promise, posts to .../answer
 * @param loadImage  () => Promise<objectUrl>, the authenticated PNG
 * @param live       { frame(after, signal), input(events) } for a live_view
 */
export function renderChallenge(challenge, { onAnswer, loadImage, live } = {}) {
  const controls = [];
  const status = h('div', { class: 'challenge-status' });
  let objectUrl = null;

  // A renderer that owns something the caller cannot see - a poll loop, a
  // listener on the visual viewport - hands back the way to stop it. Kept as a
  // list rather than a single slot so this stays true of the next renderer
  // that needs it.
  const disposers = [];

  const clock = countdown(challenge.expires_at, {
    prefix: 'expires in ',
    onExpire: () => {
      for (const node of controls) node.disabled = true;
      mount(status, note('This question expired. The run has released its browser; start the login again.'));
    },
  });

  const submit = async (value) => {
    for (const node of controls) node.disabled = true;
    mount(status, spinner('Sending your answer...'));
    try {
      await onAnswer?.(value);
      mount(status, note('Answer sent.'));
    } catch (error) {
      for (const node of controls) node.disabled = false;
      mount(status, errorBlock(error));
    }
  };

  const body = build(challenge, {
    controls,
    submit,
    loadImage,
    live,
    onUrl: (url) => { objectUrl = url; },
    onDispose: (stop) => disposers.push(stop),
    // Read rather than tracked separately: the countdown already owns whether
    // this question is still live, and a second flag would be the one that
    // goes stale.
    expired: () => clock.isExpired(),
  });

  const taps = isTapKind(challenge);

  // `answer_kind` overrides the prompt key's own wording, because a connector
  // deliberately sends ONE captcha key for both shapes - "this is not a
  // different question, it is the same question answered with a finger" - and
  // leaves the control to answer_kind. Rendering the key verbatim therefore
  // printed "Type the characters shown in the picture" above a grid with no
  // text box in it.
  const promptKey = taps ? tapPromptKey(challenge) : challenge.prompt_key;

  const element = h('section', { class: `challenge challenge-${challenge.type}` },
    h('header', { class: 'challenge-head' },
      h('div', {},
        badge(challenge.type, 'challenge'),
        // Shown, not implied: the whole point of `answer_kind` is that a
        // consumer can see it is being honoured.
        taps ? badge('taps', 'info') : null,
        h('h3', {}, taps ? sayTerm('answer_kind', 'taps') : sayTerm('challenge', challenge.type))),
      clock.element),
    promptKey ? h('p', { class: 'challenge-prompt' }, sayKey(promptKey)) : null,
    body,
    status,
    h('p', { class: 'alert-meta' }, h('code', {}, challenge.id)));

  return {
    element,
    dispose() {
      clock.dispose();
      // Before the object URL, and unconditionally: a live view's frame loop
      // creates one of its own every 100ms, and a loop still running after
      // this element left the page would go on doing it into a detached node.
      for (const stop of disposers) stop();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    },
  };
}

/**
 * True when the answer must be taps.
 *
 * A payload without the field, or one carrying a kind this build has never
 * heard of, is false - so it falls through to the type's own renderer, the
 * human types something, and the adapter reports a challenge nobody answered.
 * That is the intended degradation: guessing taps out of a string would click
 * a live page at coordinates no human chose.
 */
/**
 * The tap wording for a challenge whose key was written for a text box.
 *
 * A provider that sends its own tap-specific key keeps it - this only rewrites
 * the generic captcha key, which is the one a connector reuses for both
 * shapes. Anything unrecognised is left alone and degrades visibly, as every
 * unknown key does.
 */
function tapPromptKey(challenge) {
  return !challenge.prompt_key || challenge.prompt_key === 'connect.challenge.captcha'
    ? 'connect.challenge.captcha_tiles'
    : challenge.prompt_key;
}

function isTapKind(challenge) {
  return challenge.answer_kind === 'taps';
}

function build(challenge, ctx) {
  // Before the type switch, deliberately. `answer_kind` is the field that says
  // what the answer must CONTAIN, and it is the only one that can: a provider
  // that grew a tile grid did not change its challenge type.
  if (isTapKind(challenge)) return tapChallenge(challenge, ctx);

  switch (challenge.type) {
    case 'image': return imageChallenge(challenge, ctx);
    case 'mfa_code': return mfaChallenge(challenge, ctx);
    case 'code_display': return codeDisplayChallenge(challenge, ctx);
    case 'qr_display': return qrChallenge(challenge, ctx);
    case 'app_approval': return approvalChallenge(challenge, ctx);
    case 'select_option': return selectChallenge(challenge, ctx);
    case 'redirect': return redirectChallenge(challenge, ctx);
    // Reached from the type and not from `answer_kind`, because a live view's
    // answer is the empty one every passive challenge sends. If a `live`
    // answer kind is ever appended it falls through `isTapKind` unchanged and
    // arrives here anyway.
    case 'live_view': return liveViewChallenge(challenge, ctx);
    default: return unknownChallenge(challenge, ctx);
  }
}

/** A text answer plus a submit button, shared by most of the types. */
function answerRow(ctx, { label, placeholder, inputmode, maxlength, action = 'Submit' }) {
  const input = h('input', {
    type: 'text', class: 'control', placeholder: placeholder ?? '',
    inputmode: inputmode ?? 'text', autocomplete: 'one-time-code', maxlength: maxlength ?? null,
  });
  const button = h('button', { class: 'primary', type: 'submit' }, action);

  ctx.controls.push(input, button);

  const form = h('form', { class: 'answer-form' },
    h('label', { class: 'answer-label' }, label),
    h('div', { class: 'answer-row' }, input, button));

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    if (input.value.trim() === '') {
      input.focus();
      return;
    }
    ctx.submit(input.value.trim());
  });

  queueMicrotask(() => input.focus());
  return form;
}

function imageChallenge(challenge, ctx) {
  const slot = h('div', { class: 'challenge-image' }, spinner('Loading the picture...'));

  ctx.loadImage?.()
    .then((url) => {
      ctx.onUrl(url);
      mount(slot, h('img', { src: url, alt: 'The challenge shown by the provider' }));
    })
    .catch((error) => mount(slot, errorBlock(error)));

  return h('div', {},
    slot,
    note('The image is fetched from the relay, one shot, and redacted before it ever left the agent.'),
    answerRow(ctx, { label: 'What does it say?', placeholder: 'the characters you can read' }));
}

// ── taps ──────────────────────────────────────────────────────────────────

/** The wire's precision, and the type's: `Tap` rounds to four decimals too. */
const TAP_DIGITS = 4;

/** How far one arrow key moves the keyboard crosshair, and with Shift held. */
const CURSOR_STEP = 0.05;
const CURSOR_FINE = 0.01;

/** Arrow keys as unit vectors, for that crosshair. */
const CURSOR_KEYS = {
  ArrowLeft: [-1, 0],
  ArrowRight: [1, 0],
  ArrowUp: [0, -1],
  ArrowDown: [0, 1],
};

const clamp = (value) => Math.min(1, Math.max(0, value));

/**
 * A normalised coordinate as a CSS length.
 *
 * Per cent, never pixels: a mark has to stay on the tile it was put on when
 * the window is resized or the phone is turned, and the picture is the only
 * thing either of those keeps constant.
 */
const percent = (value) => `${(value * 100).toFixed(3)}%`;

/**
 * One coordinate, formatted the way `Tap.Format` formats it.
 *
 * Rounded to the wire's four decimals here rather than trusted to survive the
 * trip: a client that sent seventeen digits would get four back, and the two
 * ends would be "agreeing" on numbers that are not the same. `String` always
 * writes an invariant decimal point, whatever the phone's locale is set to -
 * half of Europe writes 0,5, and 0,5 parses as something else entirely.
 */
const coordinate = (value) => String(Math.round(value * 10 ** TAP_DIGITS) / 10 ** TAP_DIGITS);

/**
 * `tap.v1:x,y;x,y[;submit]` - `TapAnswer.Format`, character for character.
 *
 * The marker used to be appended unconditionally, on the reasoning that one
 * round trip is cheapest because a captcha token ages while we wait. That is
 * true of a grid that answers in one shot, and wrong about every widget that
 * draws its own verify button INSIDE the picture we relayed - hCaptcha, which
 * is the whole reason taps exist. There, "done" is not ours to declare: the
 * marker ends the relay, so sending it after the first two tiles replayed
 * those two clicks and walked away while the widget sat waiting to be
 * verified. The human saw one picture, tapped it, and never learned whether
 * anything registered.
 *
 * So `done` is now the human's choice, and the default is to come back with a
 * fresh photograph of what their taps did. That extra round is not overhead;
 * for this widget it is the only feedback that exists.
 *
 * With no taps and done=true this produces `tap.v1:submit`, which is not an
 * empty answer - it is "verify with nothing selected", the legal way to answer
 * a grid where nothing matches. An empty string would mean the human never
 * answered, and those are different sentences.
 */
function formatTaps(taps, done) {
  const parts = taps.map((tap) => `${coordinate(tap.x)},${coordinate(tap.y)}`);
  if (done) parts.push('submit');
  return `tap.v1:${parts.join(';')}`;
}

/**
 * A picture answered with taps: a tile grid, and the one challenge a text box
 * cannot carry.
 *
 * The single thing that must be right here is the normalisation. Nobody agrees
 * on the size of this picture - the agent captured it at the browser's device
 * scale factor, the relay passed the bytes through untouched, and this page
 * draws it at whatever the screen allows - so a tap is stored as a FRACTION of
 * the displayed image and never as a pixel. Get that wrong and nothing looks
 * broken here: the marks land where the finger did, the answer posts, and the
 * agent clicks the wrong square on a phone while being perfectly right on the
 * desktop it was tested on.
 *
 * Which is why every tap is measured against the `<img>` element's own client
 * rectangle and nothing else. An image carries no border and no padding, so
 * that rectangle IS the drawn picture; measuring against a wrapper with a 1px
 * border would put every tap out by that border, and by far more once the
 * image is letterboxed inside a container that does not share its aspect
 * ratio. `.tap-stage` shrink-wraps the image for the same reason, so the
 * numbered marks overlay exactly the box the coordinates came from.
 */
function tapChallenge(challenge, ctx) {
  // Ordered, because some grids ask for tiles in sequence and the order is
  // information this end already has and cannot be recovered once dropped.
  const taps = [];

  // Where the keyboard crosshair sits. Normalised exactly like a tap, so the
  // two input paths cannot drift apart.
  const cursor = { x: 0.5, y: 0.5 };
  let ready = false;

  const picture = h('img', { class: 'tap-image', alt: 'The challenge the provider is showing' });
  const marks = h('span', { class: 'tap-marks', 'aria-hidden': 'true' });
  const crosshair = h('span', { class: 'tap-crosshair', 'aria-hidden': 'true' });

  // A button, not a div: it is focusable, it is announced as something you
  // press, and the countdown's expiry sweep disables it along with everything
  // else by setting one property it already understands.
  const stage = h('button', {
    type: 'button',
    class: 'tap-stage',
    disabled: true,
    'aria-label': 'The challenge picture. Click or tap what the provider asked for. '
      + 'With a keyboard, move the crosshair with the arrow keys and press Enter to place a point.',
  }, picture, marks, crosshair);

  const slot = h('div', { class: 'challenge-taps' }, spinner('Loading the picture...'));
  const count = h('p', { class: 'tap-count', role: 'status', 'aria-live': 'polite' });

  const undo = h('button', { class: 'ghost small-button', type: 'button' }, 'Undo');
  const wipe = h('button', { class: 'ghost small-button', type: 'button' }, 'Clear');
  const send = h('button', { class: 'primary', type: 'submit' }, 'Send taps');

  // Separate from "Send taps" because they mean different things to the
  // provider: this one ends the relay, and on a widget that draws its own
  // verify button inside the picture, ending it early is how a half-answered
  // captcha gets abandoned looking like a coordinate bug.
  const done = h('button', {
    class: 'ghost', type: 'button',
    onclick: () => ctx.submit(formatTaps(taps, true)),
  }, 'Done');

  ctx.controls.push(stage, undo, wipe, send, done);

  const refresh = () => {
    mount(marks, taps.map((tap, index) => h('span', {
      class: 'tap-mark',
      style: `left:${percent(tap.x)};top:${percent(tap.y)}`,
    }, String(index + 1))));

    const plural = taps.length === 1 ? '' : 's';
    count.textContent = taps.length === 0
      ? 'Nothing selected. "Done" now answers "none of these" - which is a real answer, not an empty one.'
      : `${taps.length} point${plural} selected, in the order you tapped them.`;

    const dead = ctx.expired();
    stage.disabled = dead || !ready;
    undo.disabled = dead || taps.length === 0;
    wipe.disabled = dead || taps.length === 0;
    send.disabled = dead || !ready;
    done.disabled = dead || !ready;
  };

  const place = (x, y) => {
    // A tap on a question that has already timed out has nowhere to land: the
    // agent released its browser when the challenge expired.
    if (ctx.expired() || !Number.isFinite(x) || !Number.isFinite(y)) return;

    // Clamped rather than rejected. A press on the last row of pixels can
    // round to a hair over 1, and the human plainly meant the edge of the
    // picture - but a coordinate outside 0..1 is refused by the parser at the
    // other end, so the whole answer would be thrown away for it.
    taps.push({ x: clamp(x), y: clamp(y) });
    refresh();
  };

  stage.addEventListener('click', (event) => {
    // A button activated from the keyboard still fires a click, and it carries
    // no pointer position at all - Chrome reports (0, 0), which would silently
    // drop a point in the top-left corner of the image. `detail === 0` is how
    // that activation is told apart from a real press.
    if (event.detail === 0) {
      place(cursor.x, cursor.y);
      return;
    }

    // Measured against the IMAGE, not the button around it and not the panel
    // around that. `<img>` carries no border and no padding, so its client
    // rectangle is definitionally the picture the human is looking at - which
    // is the only rectangle the fractions below mean anything against. The
    // stage shrink-wraps it, so the numbered marks land on the same box.
    const box = picture.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) return;

    place((event.clientX - box.left) / box.width, (event.clientY - box.top) / box.height);
  });

  stage.addEventListener('keydown', (event) => {
    const move = CURSOR_KEYS[event.key];
    if (!move) return;

    // Otherwise the arrow keys scroll the page out from under the picture.
    event.preventDefault();

    const step = event.shiftKey ? CURSOR_FINE : CURSOR_STEP;
    cursor.x = clamp(cursor.x + (move[0] * step));
    cursor.y = clamp(cursor.y + (move[1] * step));
    crosshair.style.left = percent(cursor.x);
    crosshair.style.top = percent(cursor.y);
  });

  undo.addEventListener('click', () => {
    if (taps.length === 0) return;
    taps.pop();
    refresh();
  });

  wipe.addEventListener('click', () => {
    taps.length = 0;
    refresh();
  });

  // Nothing is tappable until the picture is on screen. Before it decodes the
  // stage has no height, so a click would divide by zero - and a submit button
  // over an image nobody can see would post coordinates into a grid the human
  // never looked at.
  picture.addEventListener('load', () => {
    ready = true;
    refresh();
  }, { once: true });

  picture.addEventListener('error', () => {
    mount(slot, note('The picture came through but the browser could not draw it, so there is nothing to tap.'));
    refresh();
  }, { once: true });

  ctx.loadImage?.()
    .then((url) => {
      ctx.onUrl(url);
      picture.src = url;
      mount(slot, stage);
    })
    .catch((error) => {
      mount(slot, errorBlock(error));
      refresh();
    });

  const form = h('form', { class: 'answer-form' },
    slot,
    note('Tap what the provider is asking for. Every tap is marked and numbered, and the order ',
      'is sent as you made it - some grids ask for tiles in sequence.'),
    count,
    h('div', { class: 'answer-row' }, send, done, undo, wipe),
    note('"Send taps" replays them on the page and shows you a fresh picture of what happened - ',
      'including this widget\'s own buttons, which you press by tapping them here. Use "Done" only ',
      'when the provider has accepted the answer and there is nothing left to look at.'),
    note('Your taps travel back as fractions of this picture, so they land in the same square ',
      'whatever the size of your screen. Nobody here solved anything - you did.'));

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    ctx.submit(formatTaps(taps, false));
  });

  refresh();
  return form;
}

function mfaChallenge(challenge, ctx) {
  const delivery = challenge.delivery ? `sent by ${challenge.delivery}` : 'sent to you';
  const length = challenge.length ? `${challenge.length} digits` : 'the code';

  return h('div', {},
    h('div', { class: 'challenge-meta' },
      badge(delivery, 'info'),
      challenge.length ? badge(`${challenge.length} digits`, 'neutral') : null),
    answerRow(ctx, {
      label: `Enter ${length}`,
      placeholder: challenge.length ? '0'.repeat(challenge.length) : '123456',
      inputmode: 'numeric',
      maxlength: challenge.length ?? null,
    }));
}

function codeDisplayChallenge(challenge, ctx) {
  // The one direction that surprises people: this code travels outward, from
  // us to the human, who keys it into a reader and types back what it shows.
  return h('div', {},
    h('div', { class: 'code-display' },
      h('span', { class: 'code-display-label' }, 'Type this into your device or app'),
      h('output', { class: 'code-display-value' }, challenge.code ?? 'n/a')),
    answerRow(ctx, {
      label: 'Now type back what your device shows',
      placeholder: '123456',
      inputmode: 'numeric',
      action: 'Send response',
    }));
}

function qrChallenge(challenge, ctx) {
  const slot = h('div', { class: 'challenge-qr' }, spinner('Loading the code...'));

  ctx.loadImage?.()
    .then((url) => {
      ctx.onUrl(url);
      mount(slot, h('img', { src: url, alt: 'QR code to scan with the provider app' }));
    })
    .catch((error) => mount(slot, errorBlock(error)));

  const button = h('button', { class: 'primary', type: 'button', onclick: () => ctx.submit('') }, 'I scanned it');
  ctx.controls.push(button);

  return h('div', {},
    slot,
    note('Scan this with the provider\'s own app. Nothing is typed - the app tells the provider, and the provider tells us.'),
    h('div', { class: 'answer-row' }, button));
}

function approvalChallenge(challenge, ctx) {
  // Passive by definition: there is nothing to type, and the human acts
  // somewhere we cannot observe. In production the connector would learn of
  // the approval from the provider; here the only witness is the person at
  // the keyboard, so the empty answer is theirs to send.
  const button = h('button', { class: 'ghost', type: 'button', onclick: () => ctx.submit('') },
    'I approved it in the app');
  ctx.controls.push(button);

  return h('div', {},
    h('div', { class: 'waiting' },
      spinner('Waiting for you to approve it in the provider\'s app'),
      note('There is nothing to type here. Open the app, approve the sign-in, and this page moves on.')),
    h('div', { class: 'answer-row' }, button));
}

function selectChallenge(challenge, ctx) {
  const name = `opt-${challenge.id}`;
  const options = challenge.options ?? [];

  const radios = options.map((option, index) => {
    const radio = h('input', { type: 'radio', name, id: `${name}-${index}`, value: option.value });
    ctx.controls.push(radio);
    return h('label', { class: 'radio', for: `${name}-${index}` }, radio, h('span', {}, option.label ?? option.value));
  });

  const button = h('button', { class: 'primary', type: 'submit' }, 'Choose');
  ctx.controls.push(button);

  const form = h('form', { class: 'answer-form' },
    h('div', { class: 'radios' }, radios.length > 0 ? radios : note('The provider sent no options.')),
    h('div', { class: 'answer-row' }, button));

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    const picked = form.querySelector(`input[name="${name}"]:checked`);
    if (picked) ctx.submit(picked.value);
  });

  return form;
}

function redirectChallenge(challenge, ctx) {
  const open = h('a', {
    class: 'primary button-link', href: challenge.url ?? '#', target: '_blank', rel: 'noopener noreferrer',
  }, 'Open the provider\'s sign-in');

  return h('div', {},
    h('div', { class: 'answer-row' }, open),
    challenge.return_pattern
      ? h('div', { class: 'redirect-explainer' },
        note('When you finish, the provider sends your browser to an address it cannot open:'),
        h('code', { class: 'block' }, challenge.return_pattern),
        note('That is not a failure - the address belongs to an app, not a website. ',
          'Copy it out of the address bar (or from the error page) and paste it below.'))
      : note('Finish signing in, then paste the address you ended on below.'),
    answerRow(ctx, {
      label: 'Paste the address the sign-in ended on',
      placeholder: 'appie://login-exit?code=...',
      action: 'Hand it back',
    }));
}

function unknownChallenge(challenge, ctx) {
  // A challenge type this build has never heard of still gets a text box:
  // refusing to render it would strand a login that the connector can
  // otherwise finish.
  return h('div', {},
    note('This build does not know this challenge type yet, so here is a plain box for the answer.'),
    h('pre', { class: 'raw' }, JSON.stringify(challenge, null, 2)),
    answerRow(ctx, { label: 'Answer' }));
}
