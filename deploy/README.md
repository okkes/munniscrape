# deploy/ — running the connectors

Three files do the work:

| File | Runs where | Shape |
| --- | --- | --- |
| `docker-compose.local.yml` | your machine | Postgres + both control planes + one agent each, dev auth, published throwaway keys |
| `docker-compose.yml` | the Synology NAS | Postgres + both control planes + the two http-tier agents, mTLS, LAN-only |
| `initdb/01-create-databases.sql` | first Postgres start | one database and one role per service, cross-access revoked |

The browser-tier agents are not in either file. They belong on a different
machine — see [The residential agent](#the-residential-agent).

---

## Local

```sh
docker compose -f deploy/docker-compose.local.yml up --build
```

That is the whole setup. Nothing to create first, no `.env` to copy: every
credential in that file has a published dev default. Then:

```sh
curl http://localhost:8410/v1/providers    # bank catalogue
curl http://localhost:8420/v1/providers    # shopping catalogue
curl http://localhost:8410/v1/health
psql "postgres://postgres:localtest@localhost:8432/bank_connector"
```

| Port | What |
| --- | --- |
| 8410 | bank control plane |
| 8420 | shopping control plane |
| 8432 | Postgres |

The 84xx block is chosen so a munni stack (80xx production, 81xx staging,
82xx iac) can run on the same machine untouched.

**The dev defaults are not a fallback for a real deployment.** The control
planes refuse them unless `ASPNETCORE_ENVIRONMENT=Development`, and dev
auth mode — no mTLS, no M2M token, no per-agent token — is refused the same
way. To run locally against real key material, export the variables or pass
`--env-file`; every one of them is `${VAR:-default}`:

```sh
# a bundle seal key is 32 bytes, base64
openssl rand -base64 32
# an enrollment / webhook HMAC key is the same shape
openssl rand -base64 32
```

### The local topology is the production topology

Two networks, and the agents are only on one of them:

```
data  ── postgres, bank-api, shop-api
mesh  ── bank-api, shop-api, bank-agent, shop-agent
```

An agent holds a plaintext credential for the length of a job. It never
gets a database credential, and it cannot resolve `postgres` at all. That
is worth having locally too: a shortcut taken in dev is a shortcut that
eventually ships.

---

## NAS

The production stack runs as a Container Manager project (or plain
`docker compose`) on the NAS that already hosts munni.

```sh
docker compose --env-file deploy/env/.env.prod -f deploy/docker-compose.yml up -d
```

`deploy/env/` is git-ignored. The file is rendered by the IaC bootstrap
from the stack definition and the GitHub Environment — see
[`infra/README.md`](../infra/README.md).

What the shape gives you:

- **Nothing is on the internet.** The control planes publish only on
  `${LAN_BIND_IP}`; Postgres publishes nothing at all.
- **The LAN is not trusted either.** Each control plane terminates TLS
  itself and pins the consumer's client-certificate fingerprint. A LAN
  port with mTLS in front of it is a different thing from a LAN port.
- **The consumer can skip the port entirely.** `docker network connect
  connectors_mesh <munni-api-container>` and it reaches
  `https://bank-api:8443` container-to-container. That is the preferred
  path; the published ports exist for the case where the consumer runs on
  another machine on the network.
- **Restart policy is `unless-stopped` everywhere**, so a NAS reboot
  brings the stack back but an operator's `docker compose stop` sticks.
- **There is no backup service.** A connector stages data for at most
  seven days and owns no durable user record. A nightly dump would back up
  sealed sessions and little else, which is a liability, not a safety net.
  What must survive is in the GitHub Environment, not in Postgres.

### Updating

Images are built and pushed by `.github/workflows/release-images.yml`.
`master` publishes `:latest`, `dev` publishes `:dev`. To move the NAS:

```sh
docker compose --env-file deploy/env/.env.prod -f deploy/docker-compose.yml pull
docker compose --env-file deploy/env/.env.prod -f deploy/docker-compose.yml up -d
```

Migrations run at startup (`Db__AutoMigrate=true`). Rolling back an image
does not roll back a migration; the schema is forward-only, as it is for
munni.

### The residential agent

Browser-tier providers judge us on the address we connect from, and the
NAS's egress is the same address as everything else it hosts — Jumbo
tarpits exactly that. The `browser_once` / `browser_interactive` agents run
on a mini PC or a Pi on the domestic line, with their own compose file on
that box:

```yaml
# /opt/connector-agent/docker-compose.yml on the residential host
name: connector-agents
services:
  bank-agent-browser:
    image: ghcr.io/okkes/bank-connector-agent:latest
    restart: unless-stopped
    environment:
      Agent__Name: home-browser-bank
      Agent__Class: pooled
      Agent__ControlPlaneUrl: https://<nas-lan-ip>:8390
      Agent__Token: ${BANK_BROWSER_AGENT_TOKEN}
      # copy ca.crt.pem from the NAS: the control plane's certificate is
      # signed by an internal CA, not a public one
      Agent__ControlPlaneCaPath: /tls/ca.crt.pem
      Agent__Runtimes__0: browser_once
      Agent__Runtimes__1: browser_interactive
      Agent__Egress__Country: NL
      Agent__Egress__Kind: residential
      Agent__MaxConcurrency: "1"
    volumes:
      - ./ca.crt.pem:/tls/ca.crt.pem:ro
      - bank_profiles:/profiles
volumes:
  bank_profiles:
```

No published ports, here or anywhere: every call an agent makes is
outbound, which is the property that lets it sit behind NAT on a domestic
line — and, unchanged, on a user's own machine as a BYO agent.

This is deliberately not a disabled profile inside
`docker-compose.yml`: compose interpolates every service regardless of
profile, so a single file's `${VAR:?}` guards can only be satisfied on one
of the two hosts. A file that lies about the other host is worse than two
honest files.

### Enrolling an agent

An agent authenticates with a per-agent token, scoped to `/agent/v1/*` and
revocable on its own. Getting one is a single-use, short-lived,
HMAC-signed code:

```sh
# through the consumer, or directly against the control plane as an operator
curl -X POST https://<host>:8390/v1/agents/enrollment \
     -d '{"subject":"<subject>","name":"home-browser-bank"}'
# → { "code": "AGNT-4F2K-8XQ1", "expires_at": "…" }
```

The agent redeems it once at startup (`Agent__EnrollmentCode`) and stores
the token it gets back. Locally, `Agents__DevEnrollmentCode` short-circuits
this so `docker compose up` needs no manual step; it is ignored outside
Development.

---

## Environment variables

### Both control planes

| Variable | Meaning |
| --- | --- |
| `REGISTRY` | image registry, default `ghcr.io/okkes` |
| `TAG` | image channel or pinned build, default `latest` |
| `LAN_BIND_IP` | the NAS's LAN address. **Required**; `0.0.0.0` defeats the point |
| `AUTH_AUTHORITY` | OIDC issuer that mints the consumer's M2M tokens |
| `CONSUMER_CLIENT_CERT_FINGERPRINT` | the one client certificate allowed to call `/v1/*` |
| `POSTGRES_SUPERUSER_PASSWORD` | Postgres superuser; used by initdb and nothing else |
| `BUNDLE_CURRENT_KID` | which seal key new bundles use |

### Per service (`BANK_` / `SHOP_`)

| Variable | Meaning |
| --- | --- |
| `<SVC>_DB_PASSWORD` | the service's own database role. The two roles cannot reach each other's database |
| `<SVC>_M2M_AUDIENCE` | audience the M2M token must carry (`connector.bank` / `connector.shop` scope on top) |
| `<SVC>_BUNDLE_SEAL_KEY_K1` | AES-256-GCM key sealing session bundles. 32 bytes, base64 |
| `<SVC>_BUNDLE_SEAL_KEY_K2` | the next key, empty until a rotation. Empty entries are ignored |
| `<SVC>_AGENT_ENROLLMENT_HMAC` | signs one-time agent enrollment codes |
| `<SVC>_WEBHOOK_URL` | where the payload-free "something changed" events go |
| `<SVC>_WEBHOOK_SIGNING_KEY` | HMAC over `t.body` for those events |
| `<SVC>_FEATURE_VAULT` | `server` custody. Off until a provider genuinely needs it |
| `<SVC>_VAULT_KEK` | wraps per-connection DEKs. Only read when the vault feature is on |
| `<SVC>_NAS_AGENT_TOKEN` | the http-tier agent's own token |
| `<SVC>_CONTROL_PLANE_URL` | where an agent finds its control plane |

### Rotating a bundle seal key

Rotation is a `kid` bump, never a re-login event:

1. `openssl rand -base64 32` → `<SVC>_BUNDLE_SEAL_KEY_K2`.
2. Point `BUNDLE_CURRENT_KID` at `k2` and redeploy. New bundles seal with
   k2; every bundle already on a user's device still opens under k1.
3. Once the provider's `session.ttl_seconds` has elapsed for every k1
   bundle, blank `<SVC>_BUNDLE_SEAL_KEY_K1`.

### TLS material

`deploy/env/tls/` is mounted read-only at `/tls` and holds three files:

| File | What |
| --- | --- |
| `server.crt.pem` | the control plane's server certificate |
| `server.key.pem` | its private key |
| `ca.crt.pem` | the internal CA. The control plane validates the consumer's client certificate against it; every agent needs a copy to verify the control plane's own certificate |

Files rather than environment variables on purpose: environment shows up in
`docker inspect` and in every crash dump. Only `ca.crt.pem` travels — to
each agent host, including a user's BYO machine. It is public information;
the key that signed it never leaves the bootstrap.
