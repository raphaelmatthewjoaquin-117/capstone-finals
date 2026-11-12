# Store Platform

ASP.NET Core store platform capstone: Catalog, Inventory, Orders, Payments, Identity, Ocelot gateway, Storefront, SQLite databases, and a RabbitMQ-backed asynchronous order saga.

```mermaid
flowchart LR
    Browser[Storefront / external client] -->|JWT| Gateway[Ocelot gateway]
    Gateway --> Identity[Identity API]
    Gateway --> Catalog[Catalog API]
    Gateway --> Inventory[Inventory API]
    Gateway --> Orders[Orders API]
    Catalog --> CatalogDb[(Catalog SQLite)]
    Inventory --> InventoryDb[(Inventory SQLite)]
    Orders --> OrdersDb[(Orders SQLite)]
    Identity --> IdentityDb[(Identity SQLite)]
    Payments --> PaymentsDb[(Payments SQLite)]
    Orders -->|typed resilient HTTP| Catalog
    Orders -->|reserve / commit / release| Inventory
    Orders -->|charge / refund| Payments
    Orders -->|transactional outbox| Broker[RabbitMQ]
    Broker -->|OrderPlaced| OrderWorker[Orders saga consumer]
```

## Run on this Windows device (no Docker)

Prerequisites: .NET SDK 11. The local runner uses SQLite files under `data\` and disables RabbitMQ publishing because this machine has no broker configured. The order workflow still runs end to end using resilient typed HTTP clients.

```powershell
.\scripts\run-local.ps1
```

Open [http://localhost:5200](http://localhost:5200). Browse without signing in; register a customer with a password of at least 10 characters; log in; place a successful order; then select **Simulate payment failure** to see stock released and a cancelled order. `scripts\stop-local.ps1` stops the processes started by the runner. Per-service logs are in `logs\`.

To create an Admin account for the local demo, set these before starting Identity for the first time (the local SQLite database persists the account):

```powershell
$env:SEED_ADMIN_EMAIL = "admin@example.test"
$env:SEED_ADMIN_PASSWORD = "choose-a-long-unique-password"
.\scripts\run-local.ps1
```

Change those values to credentials you control. Registration always creates a Customer; it cannot assign the Admin role.

Run the local smoke checks while the system is up:

```powershell
.\scripts\test-local.ps1
```

## Container deployment (for GitHub Actions or another Docker-capable environment)

Docker is not required on this device. On a machine with Docker Compose installed:

1. Copy `.env.example` to `.env` (`Copy-Item .env.example .env` in PowerShell).
2. Replace the signing key, admin password, and RabbitMQ password with unique values. Do not commit `.env`.
3. Run `docker compose up --build`.
4. Open [http://localhost:5200](http://localhost:5200), with RabbitMQ management at [http://localhost:15672](http://localhost:15672).

Compose starts the services, their isolated SQLite database volumes, RabbitMQ, gateway, and Storefront. API/database paths, broker settings, and JWT parameters are environment-configurable. The Compose file includes dependency health checks. Docker and Docker Compose were not run on this device.

The `Compose smoke test` GitHub Actions workflow runs automatically for pushes and pull requests to `main`, or can be started from the repository's **Actions** tab. It builds and starts the Compose stack on a GitHub-hosted runner, waits for health checks, and tests the RabbitMQ order saga, payment-failure stock compensation, and Payments gateway authorization. Its test-only credentials are generated on the runner and are not project secrets.

## Port map

| Component | Local URL |
|---|---|
| Storefront | `http://localhost:5200` |
| Ocelot gateway | `http://localhost:5100` |
| Catalog API / Swagger | `http://localhost:5101` / `http://localhost:5101/swagger` |
| Inventory API / Swagger | `http://localhost:5102` / `http://localhost:5102/swagger` |
| Orders API / Swagger | `http://localhost:5103` / `http://localhost:5103/swagger` |
| Payments API / Swagger | `http://localhost:5104` / `http://localhost:5104/swagger` |
| Identity API / Swagger | `http://localhost:5105` / `http://localhost:5105/swagger` |
| RabbitMQ management (Compose) | `http://localhost:15672` |

The browser Storefront calls the gateway only. Direct service ports are exposed for local development and should not be published outside a trusted environment.

## APIs and integration

Versioned routes are under `/api/v1` in each service and are exposed by the gateway as `/catalog/v1`, `/inventory/v1`, `/orders/v1`, `/payments/v1`, and `/identity/v1`. Catalog browsing and registration/login are public. Order routes require a valid JWT. Product creation, stock adjustment, and payment/refund operations require the Admin role; anonymous requests receive 401 and authenticated non-admin requests receive 403 through the gateway.

Each service owns its own SQLite file in local development (separate named volumes under Compose). Catalog and Inventory start with sample products and stock. Orders store status history and orchestrate reserve → charge → commit, compensating by releasing stock when payment fails or refunding if a post-charge stock commit fails. With RabbitMQ enabled, the API atomically stores a pending order and its outbox event, returns HTTP 202, and the Orders consumer completes the saga asynchronously. Poll `GET /orders/v1/orders/{id}` for the terminal status. The payment-failure switch is part of the order request and Storefront.

Orders use generated NSwag clients from the Catalog, Inventory, and Payments OpenAPI contracts, registered with `IHttpClientFactory` and the standard resilience handler. All five APIs expose versioned Swagger documentation. Contracts are the source inputs under `contracts\`; editing one regenerates its Orders client during build. Each service applies its EF Core migration at startup. APIs and the gateway share Problem Details handling for empty error responses and unhandled exceptions; exception responses avoid exposing internal details. Correlation IDs are included in HTTP requests/responses, downstream client calls, RabbitMQ message properties/headers, and structured logging scopes, including worker processing. Integration events and RabbitMQ publisher-confirm / manual-ack consumer infrastructure are in `src\Shared.Contracts` and `src\Shared.Messaging`. The Orders API uses a SQLite outbox and processed-message store; its saga consumer advances idempotently through inventory reservation, payment, stock commit, and compensation. Local mode intentionally disables RabbitMQ and keeps synchronous order responses; Compose enables the broker-backed asynchronous workflow and waits for RabbitMQ health. Compose and broker behavior still need integration testing on a Docker-capable runner.

The local Catalog exposes a development-only transient-fault injector through `POST /catalog/v1/chaos/fail-next/1`; the smoke test uses it to verify the Orders typed client retries a failed product lookup. The route is not present in the Docker gateway configuration, and the Catalog endpoint returns 404 outside Development.

**Eventual consistency:** each service commits only its own database. Cross-service order steps cannot share one ACID transaction; the Order status history exposes progress and compensating actions restore stock/payment state after failures.

## Troubleshooting

- If a port is already in use, stop the other process or change the corresponding local URL/port in `scripts\run-local.ps1` and `src\Gateway\ocelot.Local.json`.
- If startup fails, inspect the matching file in `logs\`.
- If the Admin account was not seeded, Identity may already have created its SQLite file. Stop the services, remove only `data\identity.db` if it is safe to recreate, set `SEED_ADMIN_EMAIL` and `SEED_ADMIN_PASSWORD`, then start again. This removes local identity accounts only.
- Local orders need no RabbitMQ. Compose requires valid `.env` values and a Docker-capable host.
- For a failed order, inspect the response/status history in the Storefront; a 409 response is expected for a compensated cancellation.

## Project contents

- `contracts\`: OpenAPI source contracts
- `src\`: services, shared projects, gateway, and UI
- `requests.http`: representative gateway requests
- `scripts\test-local.ps1`: local end-to-end smoke checks
- `Evidence\`: checklist for capturing actual demo evidence; no screenshots are fabricated
- `CONTRIBUTIONS.md`: record actual student ownership and commits

## Remaining verification

The local build and smoke checks cover generated Orders clients, startup migrations, retry, the synchronous local compensation flow, and gateway role checks. Verify the asynchronous outbox/consumer flow and Compose using a real RabbitMQ broker and a Docker-capable CI runner before submission. Capture genuine demo evidence after those checks.
