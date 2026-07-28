#!/usr/bin/env bash
#
# The whole demo topology on one machine, in the order it has to come up:
#
#     ShopConnector.Api   http://localhost:8420
#     BankConnector.Api   http://localhost:8410
#     ShopConnector.Agent (browser, so the browser-tier providers are testable)
#     DemoClient.Web      http://localhost:8430   <- open this
#
# Each connector's /v1/health must answer before the next process starts.
# Starting them all at once and hoping is how a startup failure surfaces three
# services later as a connection error against the wrong thing.
#
# Ctrl-C stops everything this script started, youngest first.
#
#     ./run-demo.sh                 everything
#     ./run-demo.sh --no-agent      the http-tier providers and the mocks
#     ./run-demo.sh --bank-agent    also run the bank agent (mock SCA/slow)
#     ./run-demo.sh --no-build      skip the build, start what is in bin/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

TFM="net10.0"
CONFIGURATION="Debug"

# The ports the connectors' own launch profiles use, so `dotnet run` in a
# project directory and this script are the same system. Overridable only to
# get out of a collision - everything else, including the README, says 8430.
SHOP_PORT="${DEMO_SHOP_PORT:-8420}"
BANK_PORT="${DEMO_BANK_PORT:-8410}"
WEB_PORT="${DEMO_PORT:-8430}"
SHOP_URL="http://localhost:$SHOP_PORT"
BANK_URL="http://localhost:$BANK_PORT"
DEMO_URL="http://localhost:$WEB_PORT"
CREDENTIALS="$SCRIPT_DIR/credentials.local.jsonc"

NO_AGENT=0
BANK_AGENT=0
NO_BUILD=0

# The one human this demo has. Everything identity-shaped is derived from it.
USER_ID="${DEMO_USER_ID:-demo-user}"

# The relay mints one pseudonymous subject PER SERVICE:
#
#     subject = "u_" + base64url(HMACSHA256(key: salt, msg: userId))[0..21]
#
# This script has to mint the same value, because an agent's enrollment code
# carries the subject and the agents screen lists agents BY SUBJECT - enroll
# under anything else and the UI shows an empty list beside a running agent. So
# the salts are passed to the relay rather than read from it: one source of
# truth, and no parsing of a settings file with comments in it.
#
# The two must differ. Identical salts would hand both connectors the same
# pseudonym for the same person, and the relay refuses to start.
SHOP_SALT="dev-subject-salt-shop-4c1f8a"
BANK_SALT="dev-subject-salt-bank-9e07b3"

if [ -t 1 ]; then
  C_STEP=$'\033[36m'; C_OK=$'\033[32m'; C_WARN=$'\033[33m'; C_FAIL=$'\033[31m'; C_OFF=$'\033[0m'
else
  C_STEP=""; C_OK=""; C_WARN=""; C_FAIL=""; C_OFF=""
fi

step() { printf '%s==> %s%s\n' "$C_STEP" "$1" "$C_OFF"; }
warn() { printf '%s!!  %s%s\n' "$C_WARN" "$1" "$C_OFF"; }
fail() { printf '%s!!  %s%s\n' "$C_FAIL" "$1" "$C_OFF"; }

usage() {
  cat <<'USAGE'
usage: run-demo.sh [--no-agent] [--bank-agent] [--no-build] [--user-id <id>]

  --no-agent     do not start ShopConnector.Agent. The browser-tier providers
                 report agent_unavailable without it: Albert Heijn, Amazon.nl,
                 bol.com, Coolblue and Jumbo. The http-tier ones need no agent
                 at all and still work: Lidl Plus, Picnic, the guest-order
                 providers, and every mock-* except mock-store-persistent.
  --bank-agent   also start BankConnector.Agent, which is what mock-bank-sca
                 and mock-bank-slow need. No browser binaries required.
  --no-build     start whatever is already in bin/ instead of building first.
USAGE
}

while [ $# -gt 0 ]; do
  case "$1" in
    --no-agent)   NO_AGENT=1 ;;
    --bank-agent) BANK_AGENT=1 ;;
    --no-build)   NO_BUILD=1 ;;
    --user-id)    shift; USER_ID="${1:-}" ;;
    -h|--help)    usage; exit 0 ;;
    *)            fail "unknown argument '$1'"; usage; exit 2 ;;
  esac
  shift
done

command -v dotnet  >/dev/null 2>&1 || { fail "dotnet is not on PATH"; exit 1; }
command -v curl    >/dev/null 2>&1 || { fail "curl is not on PATH; the health probes need it"; exit 1; }
command -v openssl >/dev/null 2>&1 || { fail "openssl is not on PATH; it is what mints the demo's subjects"; exit 1; }

# base64url, unpadded, truncated to 21 characters - the relay's SubjectMinter,
# character for character.
mint_subject() {
  local salt="$1" encoded
  encoded="$(printf '%s' "$USER_ID" \
    | openssl dgst -sha256 -hmac "$salt" -binary \
    | openssl base64 -A \
    | tr '+/' '-_' | tr -d '=')"
  printf 'u_%s' "${encoded:0:21}"
}

SHOP_SUBJECT="$(mint_subject "$SHOP_SALT")"
BANK_SUBJECT="$(mint_subject "$BANK_SALT")"

NAMES=()
PIDS=()
# Set by the caller immediately before start_child, consumed and cleared by it.
# A `VAR=x start_child ...` prefix would rely on how bash exports assignment
# prefixes into a *function*, and a demo runner is the wrong place to bet on
# that; an explicit `env` list is unambiguous in every shell that runs this.
CHILD_ENV=()
LAST_PID=""

# ---------------------------------------------------------------------------

project_dir()  { dirname "$1"; }
project_name() { basename "$1" .csproj; }
project_dll()  { echo "$(project_dir "$1")/bin/$CONFIGURATION/$TFM/$(project_name "$1").dll"; }

build_project() {
  local csproj="$1"
  [ "$NO_BUILD" -eq 1 ] && return 0
  step "building $(project_name "$csproj")"
  dotnet build "$csproj" -c "$CONFIGURATION" --nologo -v quiet
}

# Starts the built assembly rather than `dotnet run`. `dotnet run` forks the
# app as a child of the build host, so the pid recorded here would not be the
# pid that has to be stopped - and a demo that leaves a connector holding port
# 8420 after Ctrl-C is worse than one that never started.
#
# The working directory is the project directory so relative paths in
# appsettings (the Sqlite dev database, the agent's artifacts/) land exactly
# where a plain `dotnet run` in that directory would put them.
start_child() {
  local name="$1"; shift
  local csproj="$1"; shift
  local dll dir
  dll="$(project_dll "$csproj")"
  dir="$(project_dir "$csproj")"

  if [ ! -f "$dll" ]; then
    fail "$name is not built: $dll does not exist. Drop --no-build."
    exit 1
  fi

  ( cd "$dir" && exec env ${CHILD_ENV[@]+"${CHILD_ENV[@]}"} dotnet "$dll" "$@" ) &
  LAST_PID=$!

  NAMES+=("$name")
  PIDS+=("$LAST_PID")
  CHILD_ENV=()
}

# A port already in use is the failure this script hits most: a previous run
# that was killed rather than stopped, or a `dotnet run` in another terminal.
# Caught here it names the port; caught by the build it is ten retries of an
# MSBuild file-lock warning that names nothing useful.
assert_port_free() {
  local what="$1" port="$2"
  if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
    exec 3>&- 2>/dev/null || true
    fail "port $port is already in use, so $what cannot start."
    echo  "    Stop whatever is on it, or set DEMO_SHOP_PORT / DEMO_BANK_PORT / DEMO_PORT."
    exit 1
  fi
}

# An HTTP error response still proves something is listening, which is all a
# readiness probe on a relay can honestly assert - so "any status" is a
# separate mode from "healthy".
http_ok() {
  local url="$1" any="${2:-0}" code
  code="$(curl -s -o /dev/null -m 3 -w '%{http_code}' "$url" 2>/dev/null || echo 000)"
  [ "$code" = "000" ] && return 1
  if [ "$any" = "1" ]; then return 0; fi
  [ "$code" -ge 200 ] && [ "$code" -lt 400 ]
}

wait_http() {
  local what="$1" url="$2" pid="$3" timeout="${4:-90}" any="${5:-0}"
  local waited=0

  while [ "$waited" -lt "$((timeout * 5))" ]; do
    if [ -n "$pid" ] && ! kill -0 "$pid" 2>/dev/null; then
      fail "$what exited before it answered $url"
      exit 1
    fi
    if http_ok "$url" "$any"; then return 0; fi
    sleep 0.2
    waited=$((waited + 1))
  done

  fail "$what did not answer $url within ${timeout}s"
  exit 1
}

# An enrollment code is one-time, HMAC-signed and minted by the running
# control plane, so it cannot be pre-configured. Minting one on every start is
# safe: an agent that already has state ignores it.
mint_enrollment_code() {
  local base="$1" agent_name="$2" subject="$3" body
  body="$(curl -s -m 15 -X POST "$base/v1/agents/enrollment" \
    -H 'Content-Type: application/json' \
    -d "{\"subject\":\"$subject\",\"name\":\"$agent_name\"}" || true)"
  printf '%s' "$body" | sed -n 's/.*"code"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p'
}

wait_agent() {
  local base="$1" subject="$2" timeout="${3:-60}" waited=0 body id
  while [ "$waited" -lt "$((timeout * 2))" ]; do
    body="$(curl -s -m 5 "$base/v1/agents?subject=$subject" 2>/dev/null || true)"
    case "$body" in
      *'"online":true'*|*'"online": true'*)
        id="$(printf '%s' "$body" | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\(agt_[^"]*\)".*/\1/p')"
        echo "$id"
        return 0
        ;;
    esac
    sleep 0.5
    waited=$((waited + 1))
  done
  return 1
}

playwright_root() {
  if [ -n "${PLAYWRIGHT_BROWSERS_PATH:-}" ]; then
    echo "$PLAYWRIGHT_BROWSERS_PATH"
    return
  fi

  case "$(uname -s)" in
    Darwin)
      echo "$HOME/Library/Caches/ms-playwright" ;;
    MINGW*|MSYS*|CYGWIN*)
      # Git Bash on Windows: Playwright installs to the Windows profile, not to
      # the POSIX HOME this shell reports. Looking in the wrong place would
      # print an install command for browsers that are already there.
      echo "$(cygpath -u "${LOCALAPPDATA:-$USERPROFILE\\AppData\\Local}")/ms-playwright" ;;
    *)
      echo "$HOME/.cache/ms-playwright" ;;
  esac
}

# Detected, never installed unasked: `playwright install` downloads a few
# hundred megabytes from Microsoft's CDN, and a script that does that silently
# because someone typed run-demo is a script nobody trusts twice.
check_playwright() {
  local csproj="$1" root installer
  root="$(playwright_root)"

  if [ -d "$root" ] && ls -d "$root"/chromium-* >/dev/null 2>&1; then
    echo "    Playwright chromium: found in $root"
    return 0
  fi

  installer="$(project_dir "$csproj")/bin/$CONFIGURATION/$TFM/playwright.ps1"
  warn "Playwright browsers are not installed ($root has no chromium-*)."
  echo  "    The browser-tier providers (AH, Amazon.nl, bol.com, Coolblue, Jumbo) will fail at browser launch until they are. Install them with:"
  echo  ""
  printf '        %spwsh "%s" install chromium%s\n' "$C_OK" "$installer" "$C_OFF"
  echo  ""
  return 1
}

stop_all() {
  local status=$?
  trap - INT TERM EXIT
  [ "${#PIDS[@]}" -eq 0 ] && exit "$status"

  printf '\n'
  step "stopping"

  local i
  for (( i=${#PIDS[@]}-1; i>=0; i-- )); do
    kill -TERM "${PIDS[$i]}" 2>/dev/null || true
  done

  # A shared deadline rather than one per process: three sequential grace
  # periods would make Ctrl-C feel broken.
  local waited=0
  while [ "$waited" -lt 40 ]; do
    local alive=0
    for (( i=0; i<${#PIDS[@]}; i++ )); do
      kill -0 "${PIDS[$i]}" 2>/dev/null && alive=1
    done
    [ "$alive" -eq 0 ] && break
    sleep 0.2
    waited=$((waited + 1))
  done

  for (( i=${#PIDS[@]}-1; i>=0; i-- )); do
    kill -KILL "${PIDS[$i]}" 2>/dev/null || true
    echo "    stopped ${NAMES[$i]}"
  done

  exit "$status"
}

trap stop_all INT TERM EXIT

# ---------------------------------------------------------------------------

SHOP_API="$REPO_ROOT/shop-connector/src/ShopConnector.Api/ShopConnector.Api.csproj"
BANK_API="$REPO_ROOT/bank-connector/src/BankConnector.Api/BankConnector.Api.csproj"
SHOP_AGENT="$REPO_ROOT/shop-connector/src/ShopConnector.Agent/ShopConnector.Agent.csproj"
BANK_AGENT_PROJECT="$REPO_ROOT/bank-connector/src/BankConnector.Agent/BankConnector.Agent.csproj"

for required in "$SHOP_API" "$BANK_API" "$SHOP_AGENT"; do
  if [ ! -f "$required" ]; then
    fail "this script must live in <repo>/demo-client - $required is missing"
    exit 1
  fi
done

# The demo client is a separate deliverable and may not exist yet. Its absence
# is reported and the connectors still come up, because they are useful on
# their own with curl.
DEMO_PROJECT="$(find "$SCRIPT_DIR" -name 'DemoClient.Web.csproj' -type f 2>/dev/null | head -n 1 || true)"

assert_port_free 'ShopConnector.Api' "$SHOP_PORT"
assert_port_free 'BankConnector.Api' "$BANK_PORT"
[ -n "$DEMO_PROJECT" ] && assert_port_free 'DemoClient.Web' "$WEB_PORT"

build_project "$SHOP_API"
build_project "$BANK_API"
[ "$NO_AGENT" -eq 0 ] && build_project "$SHOP_AGENT"
if [ "$BANK_AGENT" -eq 1 ] && [ -f "$BANK_AGENT_PROJECT" ]; then build_project "$BANK_AGENT_PROJECT"; fi
[ -n "$DEMO_PROJECT" ] && build_project "$DEMO_PROJECT"

step "starting ShopConnector.Api on $SHOP_URL"
CHILD_ENV=(ASPNETCORE_ENVIRONMENT=Development)
start_child 'ShopConnector.Api' "$SHOP_API" --urls "$SHOP_URL"
SHOP_PID="$LAST_PID"
wait_http 'ShopConnector.Api' "$SHOP_URL/v1/health" "$SHOP_PID"

step "starting BankConnector.Api on $BANK_URL"
CHILD_ENV=(ASPNETCORE_ENVIRONMENT=Development)
start_child 'BankConnector.Api' "$BANK_API" --urls "$BANK_URL"
BANK_PID="$LAST_PID"
wait_http 'BankConnector.Api' "$BANK_URL/v1/health" "$BANK_PID"

SHOP_AGENT_ID=""
if [ "$NO_AGENT" -eq 1 ]; then
  # Said once, up front, rather than only in the summary: someone typing
  # --no-agent out of habit deserves to be told which half of the catalogue
  # they just switched off, before they pick one from it.
  warn "--no-agent: Albert Heijn, Amazon.nl, bol.com, Coolblue and Jumbo are browser-tier and will report agent_unavailable."
  echo "    Lidl Plus, Picnic and the guest-order providers are http tier - they run in the control plane and are unaffected."
  echo "    So are the mock-* providers, except mock-store-persistent, which wants a BYO agent this script never starts."
else
  step "starting ShopConnector.Agent"
  check_playwright "$SHOP_AGENT" || true

  CODE="$(mint_enrollment_code "$SHOP_URL" 'local-shop-agent' "$SHOP_SUBJECT")"
  if [ -z "$CODE" ]; then
    fail "could not mint an enrollment code at $SHOP_URL/v1/agents/enrollment"
    exit 1
  fi

  # The control plane is stated rather than left to the agent's own settings:
  # those name a fixed port, and an agent long-polling a different instance
  # than the one the UI talks to looks exactly like an agent that never picks
  # up work.
  CHILD_ENV=(DOTNET_ENVIRONMENT=Development)
  start_child 'ShopConnector.Agent' "$SHOP_AGENT" \
    "--ConnectorAgent:ControlPlaneBaseUrl=$SHOP_URL/" "--ConnectorAgent:EnrollmentCode=$CODE"
  SHOP_AGENT_PID="$LAST_PID"

  SHOP_AGENT_ID="$(wait_agent "$SHOP_URL" "$SHOP_SUBJECT" || true)"
  if [ -z "$SHOP_AGENT_ID" ]; then
    if kill -0 "$SHOP_AGENT_PID" 2>/dev/null; then
      warn "the shopping agent has not enrolled yet; the browser-tier providers will report agent_unavailable until it does"
    else
      warn "the shopping agent exited. If its stored token was rejected it has cleared its state - run this again."
    fi
  fi
fi

BANK_AGENT_ID=""
if [ "$BANK_AGENT" -eq 1 ]; then
  if [ ! -f "$BANK_AGENT_PROJECT" ]; then
    warn "BankConnector.Agent is not in this repo; skipping --bank-agent"
  else
    step "starting BankConnector.Agent"
    CODE="$(mint_enrollment_code "$BANK_URL" 'local-bank-agent' "$BANK_SUBJECT")"
    if [ -z "$CODE" ]; then
      fail "could not mint an enrollment code at $BANK_URL/v1/agents/enrollment"
      exit 1
    fi

    CHILD_ENV=(DOTNET_ENVIRONMENT=Development)
    start_child 'BankConnector.Agent' "$BANK_AGENT_PROJECT" \
      "--ConnectorAgent:ControlPlaneBaseUrl=$BANK_URL/" "--ConnectorAgent:EnrollmentCode=$CODE"

    BANK_AGENT_ID="$(wait_agent "$BANK_URL" "$BANK_SUBJECT" || true)"
    [ -z "$BANK_AGENT_ID" ] && warn "the bank agent has not enrolled yet"
  fi
fi

DEMO_PID=""
if [ -n "$DEMO_PROJECT" ]; then
  step "starting DemoClient.Web on $DEMO_URL"
  # Plain names for a relay that reads the environment directly, hierarchical
  # ones for a relay that binds IConfiguration. The demo is the reference
  # implementation of munni's server, so the hierarchical names match the shape
  # in docs/munni-integration-plan.md 2.3.
  # The ports and the identity, stated rather than inherited from appsettings:
  # this script already minted the agent's subject from these salts, and the
  # two must not drift. CredentialsFile is deliberately not set - the relay
  # resolves it from its content root upwards, which finds the file whether it
  # sits beside the project or beside this script.
  CHILD_ENV=(
    ASPNETCORE_ENVIRONMENT=Development
    Relay__Connectors__shop__BaseUrl="$SHOP_URL"
    Relay__Connectors__shop__SubjectSalt="$SHOP_SALT"
    Relay__Connectors__bank__BaseUrl="$BANK_URL"
    Relay__Connectors__bank__SubjectSalt="$BANK_SALT"
    Demo__UserId="$USER_ID"
  )
  start_child 'DemoClient.Web' "$DEMO_PROJECT" --urls "$DEMO_URL"
  DEMO_PID="$LAST_PID"

  wait_http 'DemoClient.Web' "$DEMO_URL/api/services" "$DEMO_PID" 60 1
else
  warn "DemoClient.Web was not found under $SCRIPT_DIR - the connectors are running, the UI is not"
fi

printf '\n%s  running%s\n' "$C_OK" "$C_OFF"
printf '  -------\n'
printf '  %-20s %-32s pid %s\n' 'Shopping connector' "$SHOP_URL/v1/providers" "$SHOP_PID"
printf '  %-20s %-32s pid %s\n' 'Bank connector' "$BANK_URL/v1/providers" "$BANK_PID"

if [ "$NO_AGENT" -eq 1 ]; then
  printf '  %-20s %s\n' 'Shopping agent' 'not started (--no-agent): browser tier reports agent_unavailable; http tier and mock-* still work'
elif [ -n "$SHOP_AGENT_ID" ]; then
  printf '  %-20s %s\n' 'Shopping agent' "$SHOP_AGENT_ID"
fi

[ -n "$BANK_AGENT_ID" ] && printf '  %-20s %s\n' 'Bank agent' "$BANK_AGENT_ID"

if [ -n "$DEMO_PID" ]; then
  printf '  %-20s %-32s pid %s\n' 'Demo client' "$DEMO_URL" "$DEMO_PID"

  # One honest line about what the relay can actually reach, straight from the
  # endpoint the SPA uses.
  SERVICES="$(curl -s -m 5 "$DEMO_URL/api/services" 2>/dev/null || true)"
  if [ -n "$SERVICES" ]; then
    printf '%s' "$SERVICES" | tr '}' '\n' | awk '
      /"key"/ {
        key = $0
        sub(/.*"key"[[:space:]]*:[[:space:]]*"/, "", key)
        sub(/".*/, "", key)
        state = "unreachable"
        if ($0 ~ /"reachable"[[:space:]]*:[[:space:]]*true/) state = "reachable"
        printf "      %-16s %s\n", key, state
      }'
  fi

  printf '\n%s  Open %s%s\n' "$C_OK" "$DEMO_URL" "$C_OFF"
fi

# One human, two pseudonyms, and no way to line them up. Printing them is the
# cheapest demonstration that the two connectors cannot correlate the same
# person - and it is how you check the agent enrolled under the subject the
# agents screen asks for.
printf "\n  user '%s' is %s to the shopping connector and %s to the bank\n" "$USER_ID" "$SHOP_SUBJECT" "$BANK_SUBJECT"

# The relay looks from its content root upwards, so a file beside the project
# counts too.
if [ ! -f "$CREDENTIALS" ] && [ ! -f "$(dirname "${DEMO_PROJECT:-/nonexistent}")/credentials.local.jsonc" ]; then
  printf "\n  No %s - the real providers' forms come up empty.\n" "$CREDENTIALS"
  printf '  Copy credentials.example.jsonc to credentials.local.jsonc and fill it in.\n'
fi

printf '\n  Ctrl-C stops everything.\n\n'

# The supervision loop. A service that dies takes the rest down with it: a
# half-running topology produces errors that describe the wrong thing.
while true; do
  sleep 1
  for (( i=0; i<${#PIDS[@]}; i++ )); do
    if ! kill -0 "${PIDS[$i]}" 2>/dev/null; then
      fail "${NAMES[$i]} exited"
      exit 1
    fi
  done
done
