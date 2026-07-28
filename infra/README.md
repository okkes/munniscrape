# infra/ — from zero to a running connector stack

Four stacks are defined here: `bank-prod`, `bank-staging`, `shop-prod`,
`shop-staging`. Each is one JSONC file describing a whole deployment —
hosts, ports, exposure, agent pool, retention, feature flags — plus the
secrets manifest that says who owns every credential those stacks need.

> The domain is a **secret** (this repository is public). Wherever this
> file says `<domain>`, that is the value of `CONNECTOR_DOMAIN`.

## What is here, and what is not yet

| | Status |
| --- | --- |
| `stacks/*.jsonc` | **here.** The source of truth for every deployment. Nothing about a stack is configured anywhere else. |
| `secrets.manifest.json` | **here.** Every secret and variable, with owner, scope and rotation policy. |
| `.github/workflows/iac.yml` | **here.** Validates the stack files and verifies a stack's GitHub Environment against the manifest, on every change to `infra/`. |
| `bootstrap.mjs`, `modules/` | **not written yet.** Until they land, the *apply* half of the checklist below is manual and the workflow's apply path fails loudly rather than pretending. |

That split is deliberate: verification is the half that catches mistakes,
so it exists first. What follows is written so a stack can be stood up
today by hand, and so each step says what will automate it later.

---

## Part A — once, ever

**A1. Repository secrets.** GitHub → Settings → Secrets and variables →
Actions → *Repository* secrets:

| Secret | What |
| --- | --- |
| `CONNECTOR_DOMAIN` | the DDNS domain the stacks hang their hostnames off |
| `IAC_GH_PAT` | fine-grained PAT for this repo: Administration + Secrets + Variables read/write. Bootstrap writes environment secrets, and `GITHUB_TOKEN` cannot |
| `GHCR_PAT` | fine-grained PAT, `read:packages`, for the NAS to pull images |
| `LOGTO_INFRA_M2M_ID` / `_SECRET` | the one manual auth step — see C1 |

**A2. Four GitHub Environments**, named exactly as the stack files say:
`bank-production`, `bank-staging`, `shop-production`, `shop-staging`. Each
carries that stack's own secrets and variables. Nothing falls back to a
repository default — a stack with a missing variable must fail CI, not
build something misconfigured.

**A3. Decide the names.** `ledgerbridge` and `basketbridge` are working
placeholders. Neither "munni" nor "scrape" may appear in a connector's
hostname: the quarantine argument dies the moment the domain advertises
both the parent app and the technique. Changing them later means new
server certificates, so decide before A4.

**A4. Mint the generated secrets** for each environment. Everything marked
`owner: generated` in `secrets.manifest.json`, and nothing else:

```sh
# 32-byte keys: BUNDLE_SEAL_KEY_K1, AGENT_ENROLLMENT_HMAC,
# WEBHOOK_SIGNING_KEY, VAULT_KEK, the postgres passwords
gh secret set BUNDLE_SEAL_KEY_K1 --env bank-production --body "$(openssl rand -base64 32)"

# the internal CA and the server certificate for one stack
openssl req -x509 -newkey rsa:4096 -days 1825 -nodes \
  -keyout ca.key.pem -out ca.crt.pem -subj "/CN=ledgerbridge internal CA"
openssl req -newkey rsa:4096 -nodes -keyout server.key.pem -out server.csr \
  -subj "/CN=ledgerbridge.<domain>"
openssl x509 -req -in server.csr -CA ca.crt.pem -CAkey ca.key.pem \
  -CAcreateserial -days 365 -out server.crt.pem
```

*Later:* `bootstrap.mjs --stack bank-prod` mints all of these with
`crypto.randomBytes`, writes them to the environment, and never prints
them.

**A5. Set the environment variables** listed under `variables` in the
manifest: `CONNECTOR_REGISTRY`, `BUNDLE_CURRENT_KID`, `LAN_BIND_IP`,
`AUTH_AUTHORITY`, `M2M_AUDIENCE`, `WEBHOOK_URL`.

---

## Part B — once per stack

**B1. Render the env file and the TLS material** onto the host, into
`deploy/env/`:

```
deploy/env/.env.prod          the variables from Part A, prefixed BANK_ / SHOP_
deploy/env/tls/ca.crt.pem     the internal CA
deploy/env/tls/server.crt.pem the control plane's certificate
deploy/env/tls/server.key.pem its key
```

A stack's GitHub Environment names its secrets unprefixed, because an
environment belongs to exactly one service. A host that runs both services
renders one env file, so the prefixes go on here. That mapping is the
`rendersTo` field in the manifest.

*Later:* the render module emits `.env.<stack>`, the compose file and the
`initdb/` folder as a CI artifact.

**B2. Start it.**

```sh
docker compose --env-file deploy/env/.env.prod -f deploy/docker-compose.yml up -d
```

See [`deploy/README.md`](../deploy/README.md) for what each variable does
and how the NAS shape works.

**B3. Nothing else is exposed.** The stack files set
`network.reverseProxy: false`: there is no DSM reverse-proxy entry to
create, and no certificate for DSM to manage. The consumer reaches the
control plane on the shared docker network or on `${LAN_BIND_IP}`, with a
pinned client certificate either way.

---

## Part C — the consumer side, once per pair

**C1. Logto (the one manual auth step).** In the Logto admin console,
create a machine-to-machine application with the Management API role, and
store its id and secret as `LOGTO_INFRA_M2M_ID` / `_SECRET`. Everything
else about Logto — the per-connector M2M applications, their audiences and
scopes — becomes code once the module lands; until then create one M2M app
per connector by hand and record the pair.

**C2. Mint the consumer's client certificates**, one per connector, from
each stack's own CA — never one certificate for both, because one
compromised client must not open both services:

```sh
openssl req -newkey rsa:4096 -nodes -keyout client.key.pem -out client.csr \
  -subj "/CN=munni-api"
openssl x509 -req -in client.csr -CA ca.crt.pem -CAkey ca.key.pem \
  -CAcreateserial -days 365 -out client.crt.pem
openssl x509 -in client.crt.pem -noout -fingerprint -sha256
```

Store the fingerprint as `CONSUMER_CLIENT_CERT_FINGERPRINT` in the
connector's environment, and the certificate and key as
`CONNECTOR_<SVC>_CLIENT_CERT_PEM` / `_CLIENT_KEY_PEM` in the consumer's.

**C3. Mint the two subject salts** and put them in the **consumer's**
environment only:

```sh
openssl rand -base64 32   # CONNECTOR_BANK_SUBJECT_SALT
openssl rand -base64 32   # CONNECTOR_SHOP_SUBJECT_SALT
```

A connector must never hold its own salt. Holding it would let it map a
subject back to a person, which is the exact property the pseudonymous
subject buys. Two separate salts are what stop the two connectors from
telling that a bank subject and a shop subject are the same human, even if
both databases leaked.

**C4. Copy the webhook keys across.** Each connector's
`WEBHOOK_SIGNING_KEY` is the consumer's `CONNECTOR_<SVC>_WEBHOOK_KEY`.

---

## Part D — agents

**D1. The http-tier agents** run on the NAS in the same compose project.
Enrol each once and store the token it returns
(`AGENT_TOKEN_NAS_HTTP` → `<SVC>_NAS_AGENT_TOKEN`).

**D2. The browser-tier agent runs somewhere else.** A mini PC or a Pi on
the domestic line, never the NAS: every browser-tier provider judges us on
the address we connect from, and the NAS's egress is the same
datacenter-shaped address as everything else it hosts. The compose snippet
for that box is in [`deploy/README.md`](../deploy/README.md).

**D3. BYO agents are the same image and the same protocol.** A user's
enrollment code is minted through the consumer and carries their subject,
so their agent can only ever serve them. Nothing in `infra/` changes when
one appears.

---

## What stays manual, permanently

- **The Logto OOBE step (C1).** Bootstrapping an identity provider needs
  an identity, once.
- **The CA's private key.** It is generated by the bootstrap and stored in
  the environment, but a human decides when to re-issue from it. A CA
  rotation invalidates the consumer's client certificate in the same
  breath, and doing that on a schedule is how a service locks its only
  caller out at 03:00.
- **Reading back a generated secret.** GitHub secrets are write-only by
  design. If you need the value, rotate it rather than trying to recover
  it.
- **Turning a provider on.** `mockProvidersOnly` and the runtime kill
  switch (`POST /v1/admin/providers/{id}/status`) are deliberate human
  decisions. A provider's runtime tier is a finding, not a plan, and a
  provider that reaches production without a canary account reaches it
  without a safety net.

## Day 2

- Every push touching `infra/` **verifies** both stacks in CI: the stack
  files must parse, their ports and hostnames must not collide, and every
  non-`module` secret in the manifest must exist in the environment.
  Verification never writes.
- Applying a stack is a manual dispatch of the *IaC* workflow, and it is
  idempotent.
- Rotating a bundle seal key is a `kid` bump, not a re-login event — the
  procedure is in [`deploy/README.md`](../deploy/README.md).
