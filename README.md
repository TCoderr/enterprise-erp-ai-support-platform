**English** · [Türkçe](README.tr.md)

# Kantar

An **order, purchasing, inventory and application support center** for a small-to-medium wholesale trading business. A product slice that runs end to end locally, with deliberately narrow boundaries: purchase order → goods receipt → stock → sales reservation → partial shipment, plus grounded help over a permission-aware knowledge base.

Author: **TCoderr**

> This repository is a development and portfolio work. It does not define a production deployment; it makes no claim of compliance for e-invoicing, accounting, payments, shipping or KVKK (the Turkish personal data protection law). All data is synthetic. Everything that was verified and everything that was not is written out separately in [`docs/final-verification.md`](docs/final-verification.md) and [`docs/known-limitations.md`](docs/known-limitations.md).

## What it does

| Area | Scope |
| --- | --- |
| Sales | Draft → Confirmed → Partially shipped → Shipped; cancel, and cancel the remaining quantity. On confirmation, all-or-nothing stock reservation for all lines; price and customer snapshot taken from the server |
| Purchasing | Draft → Approved → Partially received → Received; approval limit; goods receipt that is unique per supplier delivery note number |
| Inventory | On hand / Reserved / Available; immutable movement ledger and a separate reservation ledger; single-transaction transfer between warehouses; adjustment with a stated reason; read-only reconciliation |
| Support center | Support tickets, internal notes, allowlisted diagnostics summary; versioned knowledge base with visibility classes |
| Grounded help | Answers the question only from published documents the user is able to read, and shows document/version/section citations. It does not modify ERP data; there are no tools, SQL or automated actions |

Out of scope: accounting/ledger, payroll, manufacturing, CRM, e-invoice/e-archive, real payments and shipping, sales returns, lot/serial tracking, multi-tenancy, cloud deployment.

## Technology

- .NET 10 (SDK 10.0.401), ASP.NET Core Minimal API, modular monolith (`Domain ← Application ← Infrastructure ← Api`)
- **SQL Server 2022 Developer** (`mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04`), EF Core 10. The Developer edition is licensed for development and testing only. Why not PostgreSQL, and the measured memory cost: [`docs/adr/0001-database-provider.md`](docs/adr/0001-database-provider.md)
- React 19 + TypeScript + Vite; same origin, server-side session + cookie + CSRF
- Tests: xUnit v3 (unit + integration on real SQL Server), Vitest (component), Playwright (browser)
- Answer generation: a deterministic **demo provider** (it is not a language model) or an already-running **local** OpenAI-compatible model server. No paid/remote service is called

## Prerequisites

- Windows 10/11 (PowerShell 5.1+) or Linux/macOS (bash)
- Docker Desktop / Docker Engine (Compose v2)
- .NET SDK 10.0.401 and a .NET HTTPS development certificate (`dotnet dev-certs https` — create it once if you do not have one)
- Node.js ≥ 22.12 (only for building or testing the UI outside Docker)

**Memory**: the SQL Server container uses about 0.9 GB when idle (the official minimum requirement is 2 GB RAM). `local-stack up` does not start if free memory is below 1200 MB; it does not close other processes or containers. Run only one Kantar stack at a time.

## Running locally

All commands are run **from the repository root**:

```powershell
cd "D:\Enterprise ERP & AI Support Platform\enterprise-erp-ai-support-platform"

./scripts/local-stack.ps1 init      # creates the secret files (.local/, outside git); can be re-run
./scripts/local-stack.ps1 up        # builds the image, starts the stack, waits until it is healthy
./scripts/local-stack.ps1 seed      # demo data (can be re-run)
./scripts/local-stack.ps1 status
```

Linux/macOS: `./scripts/local-stack.sh init | up | seed | status`.

Address: **https://localhost:8943** (self-signed development certificate; the browser shows a warning).

| Command | Effect |
| --- | --- |
| `down` | Removes the containers; **the database is kept** |
| `logs` | Recent logs |
| `reset -ConfirmReset kantar` | **Destructive**: containers + database + `.local/`. Touches only volumes labeled `dev.kantar.stack=true` |

Only the API port is published, and only on `127.0.0.1`. SQL Server has no host port.

### Demo accounts

Passwords are not written down anywhere. `init` creates a random password for each user and writes it to this file: `.local/seed/Seed__Passwords__<username>`

| User | Role | Warehouse permission | Note |
| --- | --- | --- | --- |
| `satis1` | SalesAgent | IST-01 View | |
| `satis2` | SalesAgent | IST-01, ANK-01 View | Ownership scenarios |
| `satis.mudur` | SalesManager | IST-01, ANK-01 View | |
| `satinalma1` | PurchasingAgent | IST-01, ANK-01 View | Approval limit 0: cannot approve |
| `satinalma.onay` | PurchasingAgent | IST-01, ANK-01 View | Approval limit 250,000 TRY |
| `depo1` | WarehouseOperator | IST-01 Operate | |
| `depo.sorumlu` | WarehouseOperator | IST-01 Supervise, ANK-01 Operate | Adjustment (IST-01), transfer |
| `depo.izmir` | WarehouseOperator | IZM-01 Operate | A warehouse nobody else can see |
| `destek1` | SupportAgent | – | |
| `destek.mudur` | SupportManager | – | Publishes knowledge documents |
| `admin` | SystemAdmin | – | **Cannot see** commercial documents or stock |

Detailed permission matrix: [`docs/02-domain-and-state-machines.md`](docs/02-domain-and-state-machines.md) §2.4.

### Grounded help provider

The default is `Demo`: a rule-based provider that selects source sentences and cites them; it is **not a language model**, and it is labeled that way in the UI. If you already have a local model server running on this machine (e.g. Ollama, LM Studio), you can connect it with the `KANTAR_AI_*` variables in `.env.example`. Kantar does not download, install or start a model; it refuses to connect to endpoints other than loopback or an explicitly allowed private network address.

## Verification

```powershell
./scripts/verify.ps1 -List                      # gates, memory thresholds, results recorded for this source
./scripts/verify.ps1 -Gate scripts,restore,build,unit
./scripts/verify.ps1 -Gate integration          # real SQL Server (Testcontainers), restricted kantar_app login
./scripts/verify.ps1 -Gate web
./scripts/verify.ps1 -Gate migrations
./scripts/verify.ps1 -Gate e2e                  # its own stack: kantar-e2e, port 8963, a single browser worker
./scripts/verify.ps1 -Gate smoke                # its own stack: kantar-smoke, port 8953
./scripts/verify.ps1 -Gate scans
./scripts/verify.ps1 -Gate ai-live              # only if a local model is configured; otherwise "NOT RUN"
```

- Free memory is measured before every gate. If it is below the threshold the script waits; if it does not recover, the script stops with **exit code 3**; the same command is run again later.
- Results are stored in `artifacts/verification/checkpoints.json`, tied to the source fingerprint (`scripts/source-fingerprint.ps1`). A gate that has already passed on the same source is not re-run, and it is shown in the summary as "earlier invocation" together with the invocation it ran in. The results of separate invocations are not presented as if they were a single run.
- Exit codes: `0` the requested gates passed, `1` a gate failed or the source changed during the run, `3` stopped because of memory.

The scripts' own tests: `powershell -NoProfile -File scripts/tests/run.ps1`.

### Status of the latest verification

Latest run 2026-10-12, source fingerprint `sha256:1cd663dc…c6a0`. The gates were run in separate invocations, one after another; the invocation IDs, the intermediate source states and the failed intermediate runs are in [`docs/final-verification.md`](docs/final-verification.md).

This fingerprint belongs to the source state at the time the gates were run, before the repository was pushed (the "the repository was never pushed / remote CI was not run" statements in `docs/final-verification.md` describe that moment). Since then commit `6725cfb` changed only the executable bit of `scripts/local-stack.sh`, and this README was translated (`README.md` in English, Turkish text in `README.tr.md`); the translation is a documentation-only change. Because `scripts/source-fingerprint.ps1` excludes only the name `README.md` at the root, `README.tr.md` enters the fingerprint: the fingerprint of the current tree is no longer the value above, and the recorded gate results do not match it. No re-run of the gates is on record after these changes; the results below are the recorded ones, not results obtained on a new fingerprint.

| Gate | Result |
| --- | --- |
| `scripts` | 42 / 42 |
| `restore`, `build` (Release, warnings as errors) | passed |
| `unit` | 2036 / 2036, 0 skipped |
| `integration` (real SQL Server, restricted login) | 330 / 330, 0 skipped |
| `web` (typecheck, lint, build, component tests) | passed; 110 / 110 |
| `migrations` | model matches; real apply 20 / 20 |
| `e2e` (Chrome, single worker, image built from the current source) | 29 / 29, 0 skipped, 0 flaky |
| `smoke` | 30 / 30 checks |
| `scans` | no findings (this is not a security audit) |
| `ai-live` | **could not be run** — there is no local model on this machine |

| Stage | Status |
| --- | --- |
| Design documents and decisions | Done |
| Identity, master data, inventory, purchasing, sales, shipment/cancellation, worker/reconciliation | Done; verified by the gates |
| Docker/Compose stack and scripts | Done; verified by the smoke gate |
| UI | Done; verified by component and browser tests. Screen readers and browsers other than Chrome were not tested |
| Support center and grounded help | Done (with the demo provider). **Not verified with a live language model** |
| Remote CI | [Run 38181715225](https://github.com/TCoderr/enterprise-erp-ai-support-platform/actions/runs/38181715225) on commit `6725cfb` completed successfully (`web`, `backend`, `e2e` jobs; checked 2026-10-12, UTC+3). Tied to that commit only: no result is claimed for later commits, including the one that adds this text; current state: [GitHub Actions](https://github.com/TCoderr/enterprise-erp-ai-support-platform/actions). CI uses the demo provider, never runs the `ai-live` gate, and does not replace the local gates above |

ERP acceptance and live AI acceptance are separate: the former rests on the gates above; the latter has **not been given**. Tests that pass with the demo provider say nothing about the quality of a language model.

## Documentation

| Document | Contents |
| --- | --- |
| [`docs/01-product-requirements.md`](docs/01-product-requirements.md) | Scope, roles, demo data |
| [`docs/02-domain-and-state-machines.md`](docs/02-domain-and-state-machines.md) | Business rules, state machines, permission matrix |
| [`docs/03-target-architecture.md`](docs/03-target-architecture.md), [`docs/adr/`](docs/adr/) | Architecture and decisions |
| [`docs/04-database-design.md`](docs/04-database-design.md) | Schema, locking, roles, reconciliation |
| [`docs/05-api-contracts.md`](docs/05-api-contracts.md) | API contract |
| [`docs/06-security-and-failure-behavior.md`](docs/06-security-and-failure-behavior.md) | Security and failure behavior |
| [`docs/10-ai-grounding-and-access-design.md`](docs/10-ai-grounding-and-access-design.md) | Limits of grounded help |
| [`docs/inventory-correctness-verification.md`](docs/inventory-correctness-verification.md) | Stock invariants ↔ tests |
| [`docs/ai-verification.md`](docs/ai-verification.md) | AI: fake/real provider status, scenarios |
| [`docs/ui-review.md`](docs/ui-review.md) | Design sources, review findings |
| [`docs/final-verification.md`](docs/final-verification.md) | Commands run, counts, fingerprints |
| [`docs/known-limitations.md`](docs/known-limitations.md) | Known limitations |
| [`docs/portfolio-summary.md`](docs/portfolio-summary.md) | Short summary and reproducible tour |
| [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) | Third-party components and licenses |

## License and attribution

The author of the source code is TCoderr. Third-party components are used under their own licenses; details are in `THIRD-PARTY-NOTICES.md`.
