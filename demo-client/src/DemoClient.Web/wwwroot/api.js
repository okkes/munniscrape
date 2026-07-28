/**
 * The only place this app talks to the network, and it only ever talks to
 * its own origin.
 *
 * The browser never sees a connector hostname, never holds a connector
 * credential, never gets a CORS grant. Everything goes through the relay at
 * /api/*, which is the piece munni's server would implement: it mints the
 * pseudonymous subject, holds the mTLS cert and the M2M token, and does
 * resume -> ticket -> fetch internally so the ticket never reaches a tab.
 *
 * Errors come back as the connector's own envelope, unchanged:
 *   { "error": { code, retriable, user_action, message_key, detail_id? } }
 * There is no prose in it. This module turns that into a RelayError; copy.js
 * turns the keys into English.
 */

const BASE = '/api';

export class RelayError extends Error {
  constructor(status, error, detail) {
    super(error?.message_key ?? `http_${status}`);
    this.name = 'RelayError';
    this.status = status;
    this.code = error?.code ?? 'internal';
    this.retriable = error?.retriable ?? false;
    this.userAction = error?.user_action ?? 'retry';
    this.messageKey = error?.message_key ?? 'connect.error.internal';
    this.detailId = error?.detail_id ?? null;
    this.retryAfterSeconds = error?.retry_after_seconds ?? null;
    /** Operator-facing, never shown as the primary message. */
    this.detail = detail ?? null;
  }
}

/** A connector error object (jobs carry one inline) promoted to a throwable. */
export function toRelayError(error, status = 200) {
  return new RelayError(status, error, null);
}

async function request(method, path, body) {
  let response;
  try {
    response = await fetch(BASE + path, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (cause) {
    // The relay itself is unreachable. Not a connector error, but the user
    // deserves the same shaped message rather than a stack trace.
    throw new RelayError(0, { code: 'provider_unavailable', retriable: true, user_action: 'retry',
      message_key: 'connect.error.provider_unavailable' }, String(cause));
  }

  if (response.status === 204) return null;

  const text = await response.text();
  let payload = null;
  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = null;
    }
  }

  if (!response.ok) {
    // A well-behaved relay always passes the envelope through. Anything else
    // (a 404 from a typo, a proxy's HTML error page) still has to arrive as
    // one, or the UI would have two error paths and only test one.
    throw new RelayError(response.status, payload?.error, payload?.error ? null : text.slice(0, 400));
  }

  return payload;
}

export const api = {
  /** [{ key, name, kind, reachable, error? }] - an unreachable connector is a fact, not a hang. */
  services: () => request('GET', '/services'),

  /** The connector's catalogue verbatim: { providers: [manifest...], service: {...} } */
  providers: (service) => request('GET', `/${service}/providers`),

  agents: (service) => request('GET', `/${service}/agents`),

  /**
   * Development only. Returns null outside Development, where the relay
   * refuses to serve a credentials file at all.
   */
  prefill: async (service, provider) => {
    try {
      return await request('GET', `/prefill/${service}/${provider}`);
    } catch (error) {
      if (error instanceof RelayError && error.status === 404) return null;
      throw error;
    }
  },

  login: (service, provider, body) => request('POST', `/${service}/${provider}/login`, body),

  loginStatus: (service, provider, sessionId) =>
    request('GET', `/${service}/${provider}/login/${sessionId}`),

  answerLogin: (service, provider, sessionId, challengeId, value) =>
    request('POST', `/${service}/${provider}/login/${sessionId}/answer`, { challenge_id: challengeId, value }),

  /**
   * The challenge image, fetched rather than dropped into an <img src>: the
   * URL is authenticated and one-shot, so a failure has to be a value this
   * app can render, not a broken-image glyph.
   */
  challengeImage: async (service, provider, sessionId, challengeId) => {
    const url = `${BASE}/${service}/${provider}/login/${sessionId}/challenges/${challengeId}/image`;
    const response = await fetch(url);
    if (!response.ok) {
      let payload = null;
      try {
        payload = JSON.parse(await response.text());
      } catch {
        payload = null;
      }
      throw new RelayError(response.status, payload?.error, 'challenge image');
    }
    return URL.createObjectURL(await response.blob());
  },

  /**
   * One picture of a live view, or null when the server's own long-poll window
   * closed with nothing newer than `after`.
   *
   * Raw `fetch` rather than `request`, for three reasons that all matter: the
   * body is JPEG bytes and not JSON, the sequence to ask for next lives in a
   * response HEADER, and this call parks for seconds at a time so it needs an
   * abort signal the caller owns. The headers are readable because this is the
   * app's own origin - nothing here is cross-origin, so nothing depends on a
   * CORS expose list.
   */
  liveFrame: async (service, provider, sessionId, challengeId, after, signal) => {
    // Omitted, not sent as 0, when nothing has been seen yet. The connector
    // reads an absent `after` as "below every sequence a caller can hold" and a
    // present one literally, so a client that opened with `after=0` would never
    // be given a frame numbered 0 - and that failure looks like a dead stream
    // rather than like a disagreement about counting.
    const query = after === null || after === undefined ? '' : `?after=${encodeURIComponent(after)}`;
    const url = `${BASE}/${service}/${provider}/login/${sessionId}/challenges/${challengeId}`
      + `/live/frame${query}`;

    let response;
    try {
      // no-store, because a cached frame is a picture of a page as it was, and
      // this one is only worth anything as it is now.
      response = await fetch(url, { signal, cache: 'no-store' });
    } catch (cause) {
      // An abort is the caller closing the stream on purpose, not a failure,
      // so it travels as itself rather than as a relay error the UI would draw
      // a red box for.
      if (cause?.name === 'AbortError') throw cause;
      throw new RelayError(0, { code: 'provider_unavailable', retriable: true, user_action: 'retry',
        message_key: 'connect.error.provider_unavailable' }, String(cause));
    }

    if (response.status === 204) return null;

    if (!response.ok) {
      let payload = null;
      try {
        payload = JSON.parse(await response.text());
      } catch {
        payload = null;
      }
      throw new RelayError(response.status, payload?.error, 'live frame');
    }

    const [width, height] = String(response.headers.get('X-Live-Size') ?? '').split('x').map(Number);

    return {
      sequence: Number(response.headers.get('X-Live-Sequence') ?? 0),
      // Reported for the human and for a size change nobody expected. It must
      // never reach the coordinate maths - those are fractions of the picture
      // as DRAWN, and dividing by the frame's own pixel size instead is how
      // every tap lands somewhere else on a phone.
      width: Number.isFinite(width) ? width : 0,
      height: Number.isFinite(height) ? height : 0,
      blob: await response.blob(),
    };
  },

  /**
   * What the human just did, in fractions of the picture they did it to.
   *
   * `{ events: [...] }` is `LiveInputBatch` verbatim - the relay passes it
   * through unread, exactly as it passes a tap answer's string through unread,
   * because the grammar belongs to the adapter and not to the relay.
   */
  liveInput: (service, provider, sessionId, challengeId, events) =>
    request('POST', `/${service}/${provider}/login/${sessionId}/challenges/${challengeId}/live/input`,
      { events }),

  /** Data, a 202 job handle, or a challenge - all three from one call. */
  fetchResource: (service, provider, resource, body) =>
    request('POST', `/${service}/${provider}/${resource}/fetch`, body),

  job: (service, provider, jobId) => request('GET', `/${service}/${provider}/jobs/${jobId}`),

  answerJob: (service, provider, jobId, challengeId, value) =>
    request('POST', `/${service}/${provider}/jobs/${jobId}/answer`, { challenge_id: challengeId, value }),

  /** "Delivered, purge it." The connector stays a pipe only if callers ack. */
  ack: (service, provider, resource, body) =>
    request('POST', `/${service}/${provider}/${resource}/ack`, body),

  disconnect: (service, provider, sessionId, bundle) =>
    request('DELETE', `/${service}/${provider}/sessions/${sessionId}`, { bundle }),
};

/**
 * Live login progress. The relay proxies the connector's SSE stream, which
 * emits `event: state` frames holding a whole SessionResponse.
 *
 * The stream never carries the bundle - a stream cannot acknowledge receipt,
 * so a bundle delivered into one nobody read would be lost. When the state
 * reaches `active` the caller must GET the session once to collect it.
 */
export function watchLogin(service, provider, sessionId, { onState, onError } = {}) {
  const source = new EventSource(`${BASE}/${service}/${provider}/login/${sessionId}/events`);

  source.addEventListener('state', (event) => {
    try {
      onState?.(JSON.parse(event.data));
    } catch (cause) {
      onError?.(cause);
    }
  });

  // EventSource reconnects by itself, so an error here is informational
  // until the caller decides the run is over and closes the stream.
  source.addEventListener('error', () => onError?.(new Error('event stream interrupted')));

  return () => source.close();
}
