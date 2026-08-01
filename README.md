# munniscrape

Two privately-hosted connector products that read financial and retail
data no public API will give you, and hand it to exactly one caller.

- **Bank Connector** — transactions and balances from accounts open
  banking cannot reach: savings accounts, credit cards, and any bank
  outside PSD2 scope.
- **Shopping Connector** — receipts and order history from retailers with
  no public API.

Both present the same shape of API: a machine-readable catalogue of what
each provider needs from a user, a login endpoint, and provider-namespaced
resource endpoints (`/v1/ing/transactions?accounts=savings&since=…`,
`/v1/jumbo/receipts?since=…`). Both hide *how* the data is obtained —
plain HTTP, a one-time browser login, or a full browser session that stops
to ask the human a question. Neither ever calls its consumer back with
data.

**The point of the split is quarantine.** Private endpoints, headless
browsers and plaintext-credentials-in-memory live here, in services with
their own domains, databases, egress and kill switches — not in the
licensed app that holds people's finances. A takedown, an IP block or a
leak lands on a connector, not on the app.

The design is in [`docs/`](docs/README.md), and it is the specification:
[connector-platform-design.md](docs/connector-platform-design.md) first,
then [connector-api-spec.md](docs/connector-api-spec.md) for the wire
contract.

## The non-negotiables

These are enforced in the shared kit, in CI and in the infra files, not in
a review checklist:

- **A failed login is never retried. Not anywhere, not by anything.**
  Three retries locks a real bank account. `invalid_credentials` is a
  compile-time `retriable: false`, and a `login` job whose lease dies
  after the credential went upstream fails permanently rather than
  requeuing.
- **Connectors are pipes, not stores.** Normalised rows are staged and
  purged on ack, or after seven days regardless. Raw provider payloads are
  opt-in per fetch (`include=raw`), ride the same row as the record so the
  same ack purges them, and are withheld from the catalogue entirely in
  production. Neither service may become the honeypot holding both retail
  credentials and everyone's purchase history.
- **No CAPTCHA solving, no fingerprint spoofing, no proxy rotation to
  evade a block.** A challenge is relayed to the human who owns the
  account — that is what acting as a user's agent means. Solving it is
  abuse, and it destroys the quarantine argument in front of anyone who
  asks. If a provider deliberately blocks us, the adapter reports
  `blocked_by_provider`, the connection stops, and the provider's status
  flips. No escalation.
- **Only the authenticated user's own data**, only user-initiated or
  user-scheduled, never more often than the provider's declared minimum
  interval.
- **No user-facing English ever leaves a connector.** Errors, prompts and
  progress are message keys and closed enums; the consuming app owns the
  copy in every language it ships.

## Layout

```
connector-kit/src/
  Connector.Kit/          the frozen shared contract: manifests, sealed-bundle
                          crypto, error taxonomy, session and job state machines,
                          the agent protocol, normalisation
  Connector.Kit.Hosting/  the control plane as a library
  Connector.Kit.Agent/    the data plane as a library — the only assembly with a
                          browser dependency

bank-connector/src/       BankConnector.Api + BankConnector.Agent
shop-connector/src/       ShopConnector.Api + ShopConnector.Agent

deploy/                   compose files, database bootstrap, runbook
infra/                    stack definitions and the secrets manifest
docs/                     the design. Read it before changing anything here
```

Four images come out of it: two control planes (`*-connector-api`, amd64 +
arm64) and two agents (`*-connector-agent`, amd64). The agent images are
the only ones with browser binaries and the only ones that ever hold a
plaintext credential; they never get a database credential.

## Running it

```sh
docker compose -f deploy/docker-compose.local.yml up --build

curl http://localhost:8410/v1/providers   # bank catalogue
curl http://localhost:8420/v1/providers   # shopping catalogue
```

That is the whole setup — no `.env` to copy, no keys to mint. Every
credential in that file is a published throwaway, and the services refuse
those values outside `Development`.

Building and testing without Docker:

```sh
dotnet build connector-kit/src/Connector.Kit/Connector.Kit.csproj
dotnet test  bank-connector/tests/BankConnector.Api.Tests/BankConnector.Api.Tests.csproj
```

`TreatWarningsAsErrors` is on repo-wide and CI additionally builds with
`-warnaserror`, so a warning is a failed build.

[`deploy/README.md`](deploy/README.md) covers the NAS deployment, every
environment variable, and how the residential agent is run.
[`infra/README.md`](infra/README.md) is the zero-to-running checklist and
says plainly what is still manual.

## Status

Nothing is in production. The shared contract in `Connector.Kit` is
frozen; the two services are being built against it. Provider facts come
only from the public reference implementations named in
[`docs/README.md`](docs/README.md), read directly — anything not
confirmable from one is marked `unconfirmed` rather than guessed.
