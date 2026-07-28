/**
 * The provider's own sign-in page, live, with the human's fingers on it.
 *
 * Every other renderer in `challenges.js` is one question and one answer. This
 * one is a conversation: JPEGs come out of the agent's Chromium several times a
 * second and pointer and key events go back, until the provider lets the login
 * through. The reason it exists is custody - a provider served this way
 * declares no credential fields, so nothing is typed into a form of ours,
 * nothing is written to a job's inputs, and there is no password at rest
 * anywhere in the platform. The password stops entering the platform. It is not
 * encrypted better; it is not there.
 *
 * Three properties are load-bearing here and everything else is decoration.
 *
 * COORDINATES ARE FRACTIONS OF THE PICTURE AS DRAWN, measured against the
 * `<img>`'s own client rectangle and nothing else. That is the tap relay's rule
 * (`challenges.js`, `tapChallenge`) reused verbatim, for the same reason:
 * nobody agrees on the size of that picture - the agent captured it at 390x844,
 * this page draws it at whatever the screen allows - so a pixel measured here
 * lands somewhere else once the agent maps it back. The failure is silent and
 * it is silent on exactly the devices that matter.
 *
 * THE SHADOW FIELD IS EMPTIED ON EVERY KEYSTROKE, WITH ONE EXCEPTION THAT IS
 * WORTH STATING HONESTLY. It is read as a delta from `beforeinput` and emptied
 * on every `input`, so ordinarily there is no password sitting in this document
 * for a screenshot, an extension, a crash dump or a `document.querySelector` to
 * find. That is the whole defence against this end accumulating what the other
 * end refuses to store.
 *
 * The exception is an IME's composition buffer, and it is not a small one.
 * Clearing the value mid-composition is how you ABORT a composition, and
 * composition is what Android gesture typing and iOS autocorrect are made of -
 * so during one, the field holds what has been typed so far. This was first
 * written down as "a word, not a login", which is wrong: a password with no
 * space in it is a single composition, so the bound is the whole credential,
 * resident until the composition ends.
 *
 * What is true is that every way a composition can END empties it, and there
 * are more of those than there look to be. `compositionend` is the ordinary
 * one. `blur` is NOT sufficient on its own - the stage's pointerdown calls
 * preventDefault precisely so focus does not move, so tapping the picture
 * mid-word fires no blur at all - which is why the pointerdown handler, the
 * visibility change and the disposer all flush it too. Each of those sends
 * what was in the buffer rather than dropping it: the human typed it and meant
 * it to arrive, and it leaves the document either way.
 *
 * OBJECT URLS ARE REVOKED EVERY FRAME. A blob URL keeps its bytes alive for the
 * life of the document, and the garbage collector may not touch them. At a
 * frame every 100ms an unrevoked stream leaks a megabyte a second.
 */

import { h, note, spinner, errorBlock, mount, badge, sayKey } from './render.js';

// ── the wire, spelled out ─────────────────────────────────────────────────
//
// `LiveInputKind` and `LiveKey` as the connector's JSON writes them: snake
// case, because `ConnectorWireJson` registers a snake-case enum converter in
// its `Converters` collection and a converter in the collection beats the
// type-level attribute. Written out as literals rather than derived, so a
// rename at the other end fails visibly here instead of sending a verb nobody
// understands.

const MOVE = 'move';
const DOWN = 'down';
const UP = 'up';
const SCROLL = 'scroll';
const TEXT = 'text';
const KEY = 'key';

/** `LiveKey`, keyed by the `KeyboardEvent.key` that means it. */
const KEY_TOKENS = {
  Backspace: 'backspace',
  Delete: 'delete',
  Enter: 'enter',
  Tab: 'tab',
  Escape: 'escape',
  ArrowUp: 'arrow_up',
  ArrowDown: 'arrow_down',
  ArrowLeft: 'arrow_left',
  ArrowRight: 'arrow_right',
  Home: 'home',
  End: 'end',
};

/** `LiveInput.MaxTextLength`. */
const MAX_TEXT = 256;

/** `LiveInputBatch.MaxEvents`. */
const MAX_EVENTS = 64;

/**
 * `char.IsControl` as a regular expression: UnicodeCategory.Control is exactly
 * C0 and C1, and nothing else. Written to match the server's predicate
 * character for character, because the two ends disagreeing about what a
 * control character is means a batch refused whole and a human who has to type
 * their password again.
 */
const CONTROL = /[\u0000-\u001F\u007F-\u009F]+/;

// ── cadences ──────────────────────────────────────────────────────────────

/** How long a gesture waits for its neighbours before it is posted. */
const FLUSH_MS = 60;

/** At most one Move this often. A finger produces 120 a second; a page needs 20. */
const MOVE_MS = 50;

/** After a stream error, before trying again. */
const RETRY_MS = 1500;

/**
 * A floor under the empty half of the frame loop.
 *
 * A 204 means the server's own long-poll window closed with nothing new, so
 * going straight round again is right. A 204 that comes back instantly is a
 * server that is not long-polling, and an unthrottled loop against one is a
 * denial of service we wrote ourselves.
 */
const IDLE_FLOOR_MS = 250;

/**
 * A floor under the half of the loop that gets a picture, which is the half
 * that spins in the HEALTHY case rather than the broken one.
 *
 * The connector answers a frame poll the instant it holds something newer than
 * `after`, so during a live stream most polls return immediately - and a loop
 * with a floor only under its 204 path goes straight round again at whatever
 * rate the network allows. Measured against a server with a frame always ready:
 * 22 polls in 300ms, against 3 for a stream running at the agent's real shutter
 * rate. That is a request storm aimed at the one process holding the human's
 * login open, produced by the stream working rather than by it failing.
 *
 * Set below the agent's measured cadence - ~9-10 shutters a second, so
 * ~100-110ms apart - so a stream that is working is never throttled by it. What
 * is slept is the REMAINDER of the floor, so a poll that already parked 100ms
 * waiting for the next photograph owes nothing at all.
 */
const FRAME_FLOOR_MS = 80;

/** How many dots the local echo draws before it stops counting them out. */
const ECHO_CAP = 24;

/** One press of a scroll button, in fractions of the view. */
const SCROLL_STEP = 0.6;

const sleep = (ms) => new Promise((resolve) => { setTimeout(resolve, ms); });

/**
 * Sleeps out whatever is left of `floor` since `started`, and nothing when the
 * work already took that long.
 *
 * The remainder rather than the whole floor, because the two are only the same
 * thing when the request was instant. A poll that parked 90ms of a 100ms floor
 * owes 10ms; sleeping the full floor instead would halve the frame rate of a
 * stream that is working perfectly.
 */
const floorSince = async (started, floor) => {
  const rest = floor - (Date.now() - started);
  if (rest > 0) await sleep(rest);
};

/**
 * An abort is this end hanging up on purpose, and it is never a failure.
 *
 * `api.js` rethrows it as itself rather than as a relay error precisely so this
 * distinction can be made here - the two halves are one mechanism and only work
 * as a pair.
 */
const isAbort = (error) => error?.name === 'AbortError';

const clamp = (value) => Math.min(1, Math.max(0, value));

/**
 * The consumer's event counter, shared by every live view this document ever
 * renders instead of restarting at 1 inside each one.
 *
 * The connector hands the agent its input with
 * `RemoveAll(e => e.Sequence <= after)`, where `after` is the highest sequence
 * the AGENT has already been given - a high-water mark over THIS counter, held
 * per job and not per renderer. A second live view on the same job that started
 * counting from 1 again would therefore have every event it ever queued deleted
 * unread by the agent's next poll: the human types, the queue is emptied at the
 * connector, and nothing on the page moves. A second view on one job is not
 * exotic - a captcha followed by a streamed sign-in is two renderers on one job,
 * and so is a re-login raised in the middle of a fetch.
 *
 * So the counter sits at module scope and never goes backwards. It is seeded
 * from the wall clock so that a page RELOAD in the middle of a job continues
 * the series rather than restarting it, and takes `previous + 1` whenever the
 * clock has not moved or has moved backwards - several events inside one
 * millisecond, or an NTP correction. `Date.now()` is ~1.8e12, which is a long
 * on the wire and a safe integer here, with room for centuries of both.
 *
 * What this cannot fix, from here or from anywhere in a client: two DEVICES on
 * one job. Their clocks agree to within seconds, so their series interleave and
 * each one's poll deletes the other's queued events. The wire carries no field
 * saying who is driving; the design says the live channel is single-driver, and
 * that is a rule the connector has to enforce at the capability rather than one
 * a client can keep on its own.
 */
let lastSequence = 0;

export function nextSequence() {
  const now = Date.now();
  lastSequence = lastSequence < now ? now : lastSequence + 1;
  return lastSequence;
}

/**
 * The page this is a picture of, as far as anything here can tell.
 *
 * A rectangle showing a login form with no domain beside it is, structurally,
 * what a phishing page is - the domain is the single thing a person uses to
 * decide whether to type a password. `ChallengeView.Url` is already a provider
 * URL on the wire and `redirectChallenge` already renders one, so the "no URLs
 * in the payload" rule was never about provider identity; it is about connector
 * paths. Both spellings are read because the field carrying it is the one part
 * of this feature still moving, and a missing origin must read as missing
 * rather than as absent.
 */
function originOf(challenge) {
  const raw = challenge.origin ?? challenge.live?.origin ?? challenge.url ?? null;
  if (!raw) return null;
  try {
    return new URL(raw).origin;
  } catch {
    // Not parseable as a URL: show it anyway rather than hide it. The human
    // reading something odd is better than the human reading nothing.
    return String(raw);
  }
}

/**
 * Text the wire will accept, cut out of whatever the human actually typed or
 * pasted.
 *
 * Split on control characters rather than stripped of them: the server refuses
 * a whole batch containing one (`LiveInput.IsWellFormed`), and a refused batch
 * loses everything else the human typed with it. A newline in a paste is not
 * turned into Enter either - Enter submits a form, and deciding to submit on
 * somebody's behalf because their password manager put a trailing newline on
 * the clipboard is not ours to decide.
 *
 * Then chunked at the wire's 256-character bound, and never through the middle
 * of a surrogate pair: half of an emoji is a lone surrogate, which is not a
 * character any page can receive. Iterating the string yields code points,
 * `.length` counts UTF-16 units - which is what `string.Length` counts at the
 * other end - so the bound is measured in the units the server measures it in.
 */
export function textSegments(text) {
  const out = [];
  if (typeof text !== 'string' || text.length === 0) return out;

  for (const piece of text.split(CONTROL)) {
    if (piece.length === 0) continue;

    let chunk = '';
    for (const point of piece) {
      if (chunk.length + point.length > MAX_TEXT) {
        out.push(chunk);
        chunk = '';
      }
      chunk += point;
    }
    if (chunk.length > 0) out.push(chunk);
  }

  return out;
}

/**
 * @param challenge  the ChallengeView off the wire
 * @param ctx        the shared challenge context from `renderChallenge`
 */
export function liveViewChallenge(challenge, ctx) {
  const stream = ctx.live;

  // A consumer wired for challenges but not for this one degrades the way an
  // unknown type does: visibly, with the payload on screen, rather than as a
  // grey box that never fills in.
  if (!stream) {
    return h('div', {},
      note('This build can render a live sign-in but this screen was not wired to the frame and ',
        'input routes, so there is nothing to show. That is a bug in this app, not in the connector.'),
      h('pre', { class: 'raw' }, JSON.stringify(challenge, null, 2)));
  }

  const origin = originOf(challenge);

  // ── the picture ─────────────────────────────────────────────────────────

  const picture = h('img', {
    class: 'live-image',
    // Honest rather than useful. A photograph of a login form cannot be read
    // by a screen reader at all, which is a real gap in this feature and is
    // said out loud below rather than papered over with a better alt string.
    alt: 'A live picture of the provider\'s sign-in page',
    draggable: 'false',
  });

  /**
   * A real, focusable, invisible input, because it is the only way to raise a
   * phone's keyboard.
   *
   * There is no API that asks for the soft keyboard; the OS raises it in
   * response to an editable element taking focus. So the element has to exist,
   * has to be focusable - which rules out `hidden`, `display:none` and
   * `visibility:hidden`, none of which can hold focus - and has to not eat the
   * taps meant for the picture, which is what `pointer-events: none` in the
   * stylesheet is for.
   *
   * Every autofill and correction affordance is off. This field exists to be
   * emptied after every keystroke, not to hold a value, and an autocorrect
   * suggestion or a spellcheck underline both imply a field with contents. It
   * also means a password manager cannot fill it - a real loss, and an
   * unavoidable one: associating it with the provider's domain would require
   * this app to know the provider's domain, which is exactly the provider
   * knowledge the architecture keeps out of a client.
   */
  const shadow = h('input', {
    type: 'text',
    class: 'live-shadow',
    autocomplete: 'off',
    autocorrect: 'off',
    autocapitalize: 'off',
    spellcheck: 'false',
    enterkeyhint: 'go',
    'aria-label': 'Type into the provider\'s sign-in page',
  });

  const veil = h('div', { class: 'live-veil' }, spinner('Waiting for the first picture...'));

  const stage = h('div', { class: 'live-stage' }, picture, shadow, veil);

  const sizeChip = h('code', { class: 'muted' }, 'no frame yet');
  const problem = h('div', { class: 'live-problem' });
  const echo = h('p', { class: 'live-echo', role: 'status', 'aria-live': 'polite' });

  // ── frames ──────────────────────────────────────────────────────────────

  let shown = null;
  // Null, not zero: null is "I have seen nothing", which the connector reads as
  // below every sequence rather than as the number 0.
  let after = null;
  let running = true;
  let drawn = false;
  let paused = false;
  let abort = new AbortController();

  const draw = (frame) => {
    const url = URL.createObjectURL(frame.blob);
    const stale = shown;
    shown = url;
    picture.src = url;

    // Revoked only after `src` has moved on, so what is released is a handle
    // nothing is reading any more - including the case where the previous
    // frame was still decoding, because assigning a new `src` abandons that
    // load. Skipping this is the difference between a stream and a leak.
    if (stale) URL.revokeObjectURL(stale);

    // Shown, never used in the coordinate maths. Dividing by the frame's own
    // pixel size instead of the size on screen is the classic way to make
    // every tap land in the wrong place on a phone while being perfectly right
    // on the desktop it was tested on.
    sizeChip.textContent = frame.width > 0 ? `${frame.width} x ${frame.height}` : 'size not reported';
    sizeChip.classList.toggle('muted', frame.width <= 0);

    if (!drawn) {
      drawn = true;
      stage.classList.add('is-live');
    }
  };

  const stopped = (message) => {
    running = false;
    abort.abort();
    stage.classList.add('is-stopped');
    mount(veil, h('p', { class: 'live-veil-text' }, message));
    for (const node of ctx.controls) node.disabled = true;
  };

  const pump = async () => {
    while (running) {
      if (ctx.expired()) {
        stopped('This sign-in ran out of time. The agent has released its browser; start the login again.');
        return;
      }

      // The client half of viewer liveness. A backgrounded tab that keeps
      // polling leaves Chromium photographing an authenticated browser with
      // nobody watching, which is the state the whole design says must not
      // exist. Polling is what tells the other end somebody is here.
      if (paused || document.visibilityState === 'hidden') {
        await sleep(IDLE_FLOOR_MS);
        continue;
      }

      const started = Date.now();
      let frame;

      try {
        frame = await stream.frame(after, abort.signal);
      } catch (error) {
        if (!running) return;

        // An abort is not a failure and must not be drawn as one. It is this
        // end closing the poll on purpose - the tab went to the background,
        // which happens every time the human switches to their password
        // manager or their SMS - and rendering it as an error put an empty red
        // alert on the screen (the shape `errorBlock` reads is a RelayError's,
        // and an AbortError carries none of it) and then burned the 1500ms
        // retry delay on a pause that had already ended. Coming back to the
        // app cost a second and a half of frozen picture and a scare.
        if (isAbort(error)) {
          await floorSince(started, FRAME_FLOOR_MS);
          continue;
        }

        // Recoverable, and worth recovering from: a relay restart or a phone
        // changing network should not end a login. Saying so matters more
        // than retrying does - a frozen picture with no message looks live.
        mount(problem, errorBlock(error));
        await sleep(RETRY_MS);
        continue;
      }

      if (!running) return;
      mount(problem);

      // Anything typed before this request went out has now had its chance to
      // appear. Cleared on a 204 as well as on a frame, and that is the whole
      // reason it sits above the `!frame` check: a keystroke that changed
      // nothing visible - a password box that was already full of dots - would
      // otherwise leave the strip stuck, saying "on the way" about something
      // that arrived. See the local echo note below: this acknowledges that
      // the keystrokes were carried, and claims nothing about what the page
      // did with them.
      if (deliveredAt > 0 && started >= deliveredAt) {
        deliveredAt = 0;
        inFlight = 0;
        paintEcho();
      }

      if (!frame) {
        await floorSince(started, IDLE_FLOOR_MS);
        continue;
      }

      // A sequence this end cannot count with can never become the cursor.
      // `api.js` refuses an absent or unparseable `X-Live-Sequence` before it
      // reaches here, which is where the fix belongs; this is the same refusal
      // for any other frame source wired into `ctx.live`. Latching a NaN would
      // send `?after=NaN` on every later poll, which the connector answers with
      // a 400 for ever, because a cursor that is not a number cannot advance.
      if (!Number.isSafeInteger(frame.sequence)) {
        mount(problem, note('That picture arrived with no usable frame number, so the stream cannot ',
          'move past it. That is a bug in whatever is serving the frames, not in the provider.'));
        await sleep(RETRY_MS);
        continue;
      }

      // A server that forgot the sequence header would otherwise have us
      // asking for the same frame for ever, at whatever rate the network
      // allows.
      after = after === null || frame.sequence > after ? frame.sequence : after + 1;
      draw(frame);

      // The floor under the path that actually got a picture. See
      // FRAME_FLOOR_MS: this is the one that was missing, and the case it
      // bounds is a stream that is working rather than one that is broken.
      await floorSince(started, FRAME_FLOOR_MS);
    }
  };

  // ── local echo ──────────────────────────────────────────────────────────
  //
  // There is none, in the sense of drawing the character where it will appear,
  // and that is a decision rather than an omission.
  //
  // Drawing a character into the picture needs two things this client does not
  // have: the caret's rectangle, which no field on this wire carries, and the
  // provider's own font and masking. Guessing either means painting a
  // character somewhere the human's password is not going, which is a worse
  // lie than a delay - they would correct text that was never there.
  //
  // So what is echoed is custody of the keystroke, not its appearance: the
  // moment a key is pressed it becomes a dot here, and the dots clear once a
  // frame taken after the delivery arrives. The human learns instantly that
  // the press was taken and, a couple of hundred milliseconds later, sees the
  // real thing in the real page. At 5-12 frames a second a password is four or
  // five frames of lag, which is survivable; typing into a box where nothing at
  // all acknowledges the press is not.

  let inFlight = 0;
  let deliveredAt = 0;

  const paintEcho = () => {
    if (inFlight === 0) {
      echo.textContent = '';
      return;
    }
    const dots = '•'.repeat(Math.min(inFlight, ECHO_CAP));
    const plural = inFlight === 1 ? '' : 's';
    echo.textContent = `${dots}  ${inFlight} keystroke${plural} on the way to the page`;
  };

  // ── outgoing events ─────────────────────────────────────────────────────

  const queue = [];
  let sending = false;
  let flushTimer = null;

  const flush = async () => {
    if (flushTimer !== null) {
      clearTimeout(flushTimer);
      flushTimer = null;
    }

    // One request at a time. Two in flight arrive in whichever order the
    // network feels like, and these events are a sequence a human made: a Down
    // that overtakes its Move is a click somewhere they were not pointing.
    if (sending || queue.length === 0 || !running) return;

    const events = queue.splice(0, MAX_EVENTS);
    sending = true;

    try {
      await stream.input(events);
      deliveredAt = Date.now();
    } catch (error) {
      // Not retried, deliberately. Every other channel in this platform
      // retries, which is exactly why this one has to say why it does not: a
      // re-delivered Enter is a second sign-in attempt against a provider that
      // counts them, and a locked account costs more than a lost gesture.
      mount(problem, errorBlock(error));
      inFlight = 0;
      paintEcho();
    } finally {
      sending = false;
      if (queue.length > 0) flush();
    }
  };

  const push = (event) => {
    if (!running || ctx.expired()) return;

    // From the document-wide series, never from a counter of this renderer's
    // own. See `nextSequence`: a second live view on one job that restarted at
    // 1 would have every event it queued deleted, unread, at the connector.
    queue.push({ ...event, sequence: nextSequence() });

    if (event.kind === TEXT || event.kind === KEY) {
      inFlight += event.kind === TEXT ? event.text.length : 1;
      paintEcho();
    }

    // Batched rather than one request per event, because a drag is thirty
    // moves and thirty requests spend more time in HTTP headers than in the
    // browser. Sent at once when the batch is as large as the wire allows.
    if (queue.length >= MAX_EVENTS) flush();
    else if (flushTimer === null) flushTimer = setTimeout(flush, FLUSH_MS);
  };

  const pushText = (text) => {
    for (const segment of textSegments(text)) push({ kind: TEXT, text: segment });
  };

  // ── pointer ─────────────────────────────────────────────────────────────

  /**
   * Where a press landed, as a fraction of the picture as drawn.
   *
   * Measured against the IMAGE, not the stage around it and not the panel
   * around that. An `<img>` carries no border and no padding, so its client
   * rectangle is definitionally the picture the human is looking at, which is
   * the only rectangle these fractions mean anything against.
   *
   * Clamped rather than refused. A coordinate outside 0..1 makes the server
   * refuse the whole batch, so the tail of a drag that wandered off the picture
   * would cost the human every event it travelled with. The only events that
   * can land outside are the tail of a gesture that started inside.
   */
  const fraction = (event) => {
    const box = picture.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) return null;
    return {
      x: clamp((event.clientX - box.left) / box.width),
      y: clamp((event.clientY - box.top) / box.height),
    };
  };

  let dragging = false;
  let lastMove = 0;
  let lastAt = null;

  stage.addEventListener('pointerdown', (event) => {
    if (!running || ctx.expired()) return;

    const at = fraction(event);
    if (!at) return;

    // Both halves matter and they fight each other, so the order is the point.
    // `preventDefault` stops the browser's own focus handling from moving
    // focus to the body - which on a phone closes the keyboard the human just
    // raised - and `focus()` must happen inside a user gesture or iOS ignores
    // it entirely. This is the same trick a canvas editor uses, and it is the
    // whole mechanism by which a keyboard appears at all.
    event.preventDefault();

    // Before focus, because preventDefault above means no blur will fire and
    // this is the commonest way a composition is abandoned: the human is
    // half-way through a word and reaches for the page instead.
    flushComposition();

    shadow.focus({ preventScroll: true });

    dragging = true;
    lastAt = at;

    // The press is queued BEFORE the capture is taken, because
    // `setPointerCapture` throws when the pointer is not active any more - a
    // stylus lifted between the browser dispatching this event and this line
    // running - and a throw here would lose the press the human actually made
    // while leaving the page believing nothing happened.
    push({ kind: DOWN, x: at.x, y: at.y });

    // So a drag that leaves the picture still delivers its moves and, more
    // importantly, still delivers its release. A drag that ends outside with no
    // capture leaves the provider's page holding the button down, and the next
    // tap is then a drag.
    try {
      stage.setPointerCapture(event.pointerId);
    } catch {
      // Not fatal: without capture the gesture is confined to the picture,
      // which is a smaller drag rather than a broken one.
    }
  });

  stage.addEventListener('pointermove', (event) => {
    // Only while the button is down. A stream of hover moves is twenty
    // requests a second for a page nobody is touching, and every control a
    // login form has responds to a press rather than to a hover. A provider
    // that needs hover gets it back by making this unconditional; nothing else
    // changes.
    if (!dragging || !running) return;

    const now = Date.now();
    if (now - lastMove < MOVE_MS) return;

    const at = fraction(event);
    if (!at) return;

    lastMove = now;
    lastAt = at;
    push({ kind: MOVE, x: at.x, y: at.y });
  });

  const release = (event) => {
    if (!dragging) return;
    dragging = false;

    // Asked first, because `releasePointerCapture` throws for a pointer that is
    // no longer active - which is exactly the case a `pointercancel` is, and
    // the throw would take the Up event below down with it.
    if (stage.hasPointerCapture?.(event.pointerId)) stage.releasePointerCapture(event.pointerId);

    const at = fraction(event) ?? lastAt;
    if (at) push({ kind: UP, x: at.x, y: at.y });

    // Flushed now rather than in 60ms: a click the human has finished making
    // is the one event worth a request of its own.
    flush();
  };

  stage.addEventListener('pointerup', release);

  // A cancelled pointer - the OS took the gesture for a system swipe, the
  // finger left the screen edge - still has to release the button on the
  // provider's page. Leaving it down means the next tap is a drag.
  stage.addEventListener('pointercancel', release);

  stage.addEventListener('wheel', (event) => {
    if (!running || ctx.expired()) return;

    const at = fraction(event);
    if (!at) return;

    event.preventDefault();

    const box = picture.getBoundingClientRect();
    // deltaMode 1 is lines and 2 is pages; a browser that reports either would
    // otherwise scroll by a fraction of a pixel.
    const lines = event.deltaMode === 1 ? 16 : 1;
    const pixels = event.deltaMode === 2 ? event.deltaY * box.height : event.deltaY * lines;

    push({ kind: SCROLL, x: at.x, y: at.y, delta_y: Math.max(-1, Math.min(1, pixels / box.height)) });
  }, { passive: false });

  // ── keyboard ────────────────────────────────────────────────────────────

  let composing = false;

  shadow.addEventListener('keydown', (event) => {
    if (!running || ctx.expired()) return;

    // A physical keyboard reports the key here; a soft one reports
    // 'Unidentified' or nothing at all and speaks through `beforeinput`
    // instead. Reading both, and letting whichever fires first win, is why
    // Backspace works on a laptop and on a phone.
    // Not while an IME is composing, and this is the one that matters most.
    //
    // A composition holds the typed text in the shadow field until
    // `compositionend` commits it. Relaying Enter from inside one sends the
    // key WITHOUT the text it was meant to submit: the provider's page gets a
    // sign-in with an empty password box while the password is still sitting
    // in a buffer on this side. Backspace and the arrows are the same shape -
    // the human is editing a word that has not been sent, and the key would
    // land on whatever the page had before it.
    //
    // `event.isComposing` is read as well as the flag because a soft keyboard
    // can deliver a keydown between `compositionend` and our handler running,
    // and the two disagree for exactly that one event.
    if (composing || event.isComposing) return;

    const token = KEY_TOKENS[event.key];
    if (!token) return;

    // There is no way to say Control, Alt or Meta on this wire, and that
    // absence is load-bearing: Playwright reads "Control+o" as a chord and a
    // latched modifier turns the next click into "open in a new tab". So a
    // chord is refused whole rather than relayed as its base key, which would
    // be a different keystroke than the one the human made.
    if (event.ctrlKey || event.metaKey || event.altKey) return;

    // Shift+Tab means "the field before this one" and comes out the other end
    // as plain Tab, which moves the human forwards when they asked to go back.
    // Refusing it is honest; guessing is not.
    if (event.shiftKey && event.key === 'Tab') return;

    // So the character does not also arrive through `beforeinput`, and so Tab
    // does not walk the browser's focus out of the picture.
    event.preventDefault();
    push({ kind: KEY, key: token });
  });

  shadow.addEventListener('compositionstart', () => { composing = true; });

  shadow.addEventListener('compositionend', (event) => {
    composing = false;
    // The committed string, once. Android gesture typing and every IME emit a
    // run of partial `insertCompositionText` events on the way here; sending
    // those would type the word several times over.
    if (event.data) pushText(event.data);
    shadow.value = '';
  });

  shadow.addEventListener('beforeinput', (event) => {
    if (!running || ctx.expired() || composing) return;

    const type = event.inputType ?? '';
    if (type === 'insertCompositionText') return;

    if (type.startsWith('delete')) {
      push({ kind: KEY, key: type === 'deleteContentForward' ? 'delete' : 'backspace' });
      return;
    }

    if (type === 'insertLineBreak' || type === 'insertParagraph') {
      push({ kind: KEY, key: 'enter' });
      return;
    }

    // `data` is the delta itself. The field's own value is never read, and
    // that is the whole reason nothing accumulates here: there is nothing to
    // accumulate into.
    if (typeof event.data === 'string' && event.data.length > 0) pushText(event.data);
  });

  shadow.addEventListener('paste', (event) => {
    // Handled here rather than through `beforeinput`, whose `data` is null for
    // a paste in more than one browser, and prevented so the text never lands
    // in the field at all. This is the password-manager path: the manager
    // fills the client's own clipboard and the paste rides as text events.
    const text = event.clipboardData?.getData('text') ?? '';
    event.preventDefault();
    pushText(text);
  });

  // Emptied after every event, including for an inputType this build did not
  // recognise - the field being empty matters more than the keystroke being
  // delivered. `beforeinput` fires before the character lands, so the clearing
  // has to happen after it does.
  //
  // With ONE exception, and it is the difference between this working on a
  // phone and not: never while an IME is composing. Assigning to `value`
  // mid-composition is the documented way to ABORT a composition, and
  // composition is the machinery Android gesture typing and iOS autocorrect
  // both run on - so clearing here cancelled the gesture the human was in the
  // middle of, every time, and the symptom on a real phone is a keyboard that
  // deletes what you swipe. `beforeinput` already refuses to send partial
  // composition text, so nothing was being relayed during that window either:
  // the field was cleared and the keystrokes went nowhere.
  //
  // What sits in the field during composition is the composition buffer, which
  // is a word rather than a password: `compositionend` above empties it the
  // instant the word is committed, and `blur` below empties it if the human
  // walks away mid-word. That window is the price of an IME working at all,
  // and it is bounded by one composition rather than by the login.
  shadow.addEventListener('input', () => {
    if (composing) return;
    shadow.value = '';
  });

  // The backstop on that exception, and `blur` is NOT sufficient to be it.
  //
  // The obvious abandonment - the human taps the picture mid-composition - does
  // not blur anything: the stage's own pointerdown calls preventDefault()
  // precisely so focus stays in the shadow field, so no blur fires and the
  // buffer survives. For a password with no space in it the whole credential is
  // one composition, so what survives is the whole credential, sitting in the
  // document for the rest of the login.
  //
  // So every path that abandons a composition goes through here, and it COMMITS
  // rather than discards. Dropping it would be safer by a hair and much worse
  // to use: the human types their password, taps the picture, and what they
  // typed silently never happened. Sending it is what they asked for, and it
  // empties the field either way - which is the part that matters.
  const flushComposition = () => {
    const pending = shadow.value;
    composing = false;
    shadow.value = '';
    if (pending) pushText(pending);
  };

  shadow.addEventListener('blur', flushComposition);

  // Belt and braces for the moment the whole feature turns on: a soft keyboard
  // that did not appear on pointerdown is one press away here, and a press on
  // a real button is the one gesture every mobile browser accepts a focus()
  // from.
  const keyboard = h('button', {
    class: 'ghost small-button', type: 'button',
    onclick: () => shadow.focus({ preventScroll: true }),
  }, 'Show keyboard');

  // A login page taller than a phone is unreachable below the fold without
  // these, and there is no wheel on a phone. A drag stays a drag - sliders and
  // captcha handles need it - so scrolling is asked for explicitly instead of
  // guessed from an ambiguous swipe.
  const up = h('button', {
    class: 'ghost small-button', type: 'button',
    onclick: () => { push({ kind: SCROLL, x: 0.5, y: 0.5, delta_y: -SCROLL_STEP }); flush(); },
  }, 'Scroll up');

  const down = h('button', {
    class: 'ghost small-button', type: 'button',
    onclick: () => { push({ kind: SCROLL, x: 0.5, y: 0.5, delta_y: SCROLL_STEP }); flush(); },
  }, 'Scroll down');

  // Passive in the same sense as an app approval: there is no string to type
  // back, and what ends this normally is the provider letting the login
  // through. This is the escape hatch for when it does not - without it the
  // only way out of a stream that will not work is to wait out the expiry.
  const giveUp = h('button', {
    class: 'ghost', type: 'button',
    onclick: () => {
      stopped('You stopped this sign-in.');
      ctx.submit('');
    },
  }, 'Stop - I could not sign in');

  ctx.controls.push(shadow, keyboard, up, down, giveUp);

  // ── keyboard avoidance, such as it is ───────────────────────────────────
  //
  // In an ordinary form the browser scrolls the focused input into view when
  // the keyboard rises. Here it cannot: focus is on a 1px field and the real
  // password box is a region of a bitmap. Scrolling the whole stage into the
  // visual viewport is the mitigation this client can actually implement;
  // panning the picture so the caret clears the keyboard needs the caret's
  // rectangle, which nothing on this wire carries yet.
  const viewport = globalThis.visualViewport ?? null;
  const reveal = () => {
    if (!running || document.activeElement !== shadow) return;
    stage.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  };
  viewport?.addEventListener('resize', reveal);

  const onVisibility = () => {
    paused = document.visibilityState === 'hidden';

    // Switching away is the other abandonment that fires no blur on some
    // mobile browsers. Nothing should be left composing in a backgrounded tab,
    // least of all a password.
    if (paused) flushComposition();
    // A poll that is already parked in the server's long-poll window would sit
    // there for its full duration; dropping it is what makes the pause take
    // effect now rather than in several seconds.
    if (paused) {
      abort.abort();
      abort = new AbortController();
    }
  };
  document.addEventListener('visibilitychange', onVisibility);

  ctx.onDispose(() => {
    // First, so a composition open when this element leaves the page does not
    // go with it - both because the human meant to send it and because it is
    // the last moment anything can empty that field.
    flushComposition();

    running = false;
    abort.abort();
    if (flushTimer !== null) clearTimeout(flushTimer);
    viewport?.removeEventListener('resize', reveal);
    document.removeEventListener('visibilitychange', onVisibility);
    if (shown) URL.revokeObjectURL(shown);
    shown = null;
  });

  paintEcho();
  pump();

  return h('div', { class: 'live-view' },
    challenge.prompt_key ? null : h('p', { class: 'challenge-prompt' }, sayKey('connect.challenge.live_login')),

    h('div', { class: 'live-origin' },
      badge('live', 'info'),
      h('span', { class: 'live-origin-label' }, 'You are typing into'),
      origin
        ? h('code', { class: 'live-origin-value' }, origin)
        : h('code', { class: 'live-origin-value is-unknown' }, 'nobody told us whose page this is'),
      sizeChip),

    stage,
    echo,
    problem,

    h('div', { class: 'answer-row' }, keyboard, up, down),

    h('p', { class: 'note' }, sayKey('connect.live.custody')),
    h('p', { class: 'note note-warn' }, sayKey('connect.live.no_attestation')),
    h('p', { class: 'note' }, sayKey('connect.live.keyboard')),
    note('Your taps travel back as fractions of this picture, so they land on the same button ',
      'whatever the size of your screen. Nothing is typed into a form of ours: there is no form.'),
    note('A live picture cannot be read by a screen reader, so a provider that signs in this way ',
      'cannot yet be connected without sight. That is a real gap, said out loud rather than hidden.'),

    h('div', { class: 'answer-row' }, giveUp));
}
