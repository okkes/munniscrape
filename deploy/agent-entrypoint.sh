#!/bin/sh
# Starts a virtual display, then hands over to the agent.
#
# WHY: a headless browser is not a disguised browser, it is a different
# one. It reports no display, no window manager, no compositor, and a
# distinct set of GPU and rendering paths, and bot protection reads all of
# that directly. Albert Heijn's edge returned "Access Denied" on the login
# page to a containerised agent whose only meaningful difference from a
# working one was that it had no screen.
#
# Xvfb is a real X server and Chromium under it is really headed - it
# paints, composites and reports a display because there is one. That is
# the opposite of the fingerprint patching this project refuses to do: no
# navigator.webdriver rewriting, no canvas noise, no stealth plugin, no
# claim about the environment that is not true. We are not telling the
# provider something false about the browser; we are giving the browser
# the thing it was missing.
#
# Set ConnectorAgent__Headless=true to skip it - the display still exists
# and Chromium simply ignores it, so one image serves both modes.
set -eu

: "${AGENT_DISPLAY:=:99}"
: "${AGENT_SCREEN:=1920x1080x24}"

# 24-bit, because xvfb's own default is 8-bit and Chromium renders
# visibly wrong colours into a screenshot on an 8-bit root window - and a
# screenshot is the whole product here.
#
# -nolisten tcp only. Adding -nolisten unix as well, which looks like the
# same hardening one step further, leaves the server listening on nothing
# at all: the filesystem socket under /tmp/.X11-unix is how Chromium
# actually connects. It is the local socket or nothing, and nothing means
# every browser launch falls back to no display - the exact condition this
# script exists to remove, arrived at while appearing to fix it.
Xvfb "$AGENT_DISPLAY" -screen 0 "$AGENT_SCREEN" -nolisten tcp &

# Wait for the socket rather than assume it. Chromium started against a
# display that is not listening yet fails at launch instead of retrying.
socket="/tmp/.X11-unix/X${AGENT_DISPLAY#:}"
i=0
while [ "$i" -lt 100 ]; do
    [ -S "$socket" ] && break
    i=$((i + 1))
    sleep 0.1
done

# Loud, because the alternative is a browser that silently runs without a
# display and gets refused by a provider ten minutes later.
[ -S "$socket" ] || echo "agent-entrypoint: WARNING: $socket never appeared; the browser will have no display" >&2

export DISPLAY="$AGENT_DISPLAY"

# `exec` on purpose: the agent replaces this shell and therefore receives
# SIGTERM from Docker directly. Wrapping it in `xvfb-run` instead would
# leave a shell between Docker and the agent that does not reliably
# forward the signal, and the agent's shutdown drain - which exists so an
# in-flight fetch finishes rather than dying mid-job - would never run.
# Xvfb is reaped by the container exiting.
exec "$@"
