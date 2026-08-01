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
display_number="${AGENT_DISPLAY#:}"
socket="/tmp/.X11-unix/X${display_number}"
lock="/tmp/.X${display_number}-lock"

# A container starts with no X server, so a lock or socket for our display
# is by definition a leftover - and the Playwright base image SHIPS one,
# baked into /tmp at its own build time. Xvfb sees it, refuses with "Server
# is already active for display 99", exits, and leaves a zombie. The agent
# then starts perfectly happily with no screen at all and fails every
# browser job for the life of the container.
rm -f "$lock" "$socket" 2>/dev/null || true

Xvfb "$AGENT_DISPLAY" -screen 0 "$AGENT_SCREEN" -nolisten tcp &
xvfb_pid=$!

# Wait for the socket rather than assume it. Chromium started against a
# display that is not listening yet fails at launch instead of retrying.
i=0
while [ "$i" -lt 100 ]; do
    [ -S "$socket" ] && kill -0 "$xvfb_pid" 2>/dev/null && break
    i=$((i + 1))
    sleep 0.1
done

# Both checks, because either alone lies. A socket can be a file somebody
# else left behind - which is exactly the bug above, and the reason the
# original guard passed while nothing was listening. A live process can be
# one that has not finished binding yet.
if [ -S "$socket" ] && kill -0 "$xvfb_pid" 2>/dev/null; then
    export DISPLAY="$AGENT_DISPLAY"
else
    # Fatal when the browser is meant to be headed, because a headed agent
    # without a display is not degraded, it is broken: it will lease jobs
    # for every provider it advertises and fail all of them. Better the
    # container dies and Docker's restart policy says so out loud.
    if [ "${ConnectorAgent__Headless:-true}" = "false" ]; then
        echo "agent-entrypoint: FATAL: no X server on $AGENT_DISPLAY and Headless=false" >&2
        exit 1
    fi

    echo "agent-entrypoint: WARNING: no X server on $AGENT_DISPLAY; running headless" >&2
fi

# `exec` on purpose: the agent replaces this shell and therefore receives
# SIGTERM from Docker directly. Wrapping it in `xvfb-run` instead would
# leave a shell between Docker and the agent that does not reliably
# forward the signal, and the agent's shutdown drain - which exists so an
# in-flight fetch finishes rather than dying mid-job - would never run.
# Xvfb is reaped by the container exiting.
exec "$@"
