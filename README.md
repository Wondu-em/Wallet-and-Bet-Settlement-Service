# Wallet & Bet Settlement Service

A .NET 10 and PostgreSQL backend for a play-money ETB wallet and fixed-odds betting. It includes
wallet deposits and withdrawals, event and bet management, settlement and voiding, and a signed
simulated deposit webhook. No real money or payment provider is involved.

[![CI/CD](https://github.com/Wondu-em/Wallet_Bet_and_Settlement_Service/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/Wondu-em/Wallet_Bet_and_Settlement_Service/actions/workflows/ci-cd.yml)

## Requirements

- .NET 10 SDK
- PostgreSQL 17 (or compatible supported PostgreSQL version)
- Docker Engine and Docker Compose are optional; they are not required to build or test if you
  already have PostgreSQL installed

## Run locally with PostgreSQL

Create a local database and PostgreSQL login, then configure the API using .NET user secrets:

```powershell
dotnet user-secrets set --project src\Wallet.Api "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=walletbet;Username=wallet;Password=YOUR_LOCAL_PASSWORD"
dotnet user-secrets set --project src\Wallet.Api "Jwt:Key" "replace-with-a-random-secret-at-least-32-characters"
dotnet user-secrets set --project src\Wallet.Api "Webhook:Secret" "replace-with-a-different-random-secret-at-least-32-bytes"
dotnet user-secrets set --project src\Wallet.Api "Admin:Email" "admin@example.com"
dotnet user-secrets set --project src\Wallet.Api "Admin:Password" "replace-with-a-strong-local-password"
```

Start the API:

```powershell
dotnet run --project src\Wallet.Api\Wallet.Api.csproj
```

At startup the API applies pending EF Core migrations, creates system ledger accounts, and seeds
the configured admin if that email does not already exist. In the Development environment Swagger
is available at `http://localhost:5103/swagger` (or the URL printed by `dotnet run`); database
health is available at `/health`.

## Run with Docker Compose

If Docker Engine with Compose is available, run from the repository root:

```powershell
docker compose up --build
```

The API listens at `http://localhost:8080`, Swagger at `/swagger`, and PostgreSQL is bound to
localhost port 5432 with persistent data in the `pgdata` volume. Compose supplies demo credentials
for local development. Override `POSTGRES_PASSWORD`, `JWT_KEY`, `WEBHOOK_SECRET`, `ADMIN_EMAIL`,
and `ADMIN_PASSWORD` in the environment before use outside a local development machine.

Stop the stack with `docker compose down`. To also delete the local database and all its data, run
`docker compose down -v`.

## Tests

The integration tests use a real PostgreSQL instance and create a temporary database per test
collection. Start PostgreSQL first. The PostgreSQL role in the connection string must be allowed
to create databases. By default, the fixture uses the local `wallet` login against the `postgres`
database; set `TEST_PG_ADMIN` to an administrative PostgreSQL connection string if your local
setup differs:

```powershell
$env:TEST_PG_ADMIN = "Host=localhost;Port=5432;Database=postgres;Username=wallet;Password=YOUR_LOCAL_PASSWORD"
dotnet test WalletBet.slnx
```

The integration-test collection is serialized because it creates and drops temporary databases.
Unit tests do not need PostgreSQL.

## Continuous integration and image publishing

The GitHub Actions workflow runs on pull requests targeting `main`, pushes to `main`, and manual
dispatch. It provisions PostgreSQL 17, restores and builds the solution, and runs all unit and
integration tests. After validation succeeds, pushes to `main` and version tags matching `v*`
publish the API image to `ghcr.io/wondu-em/wallet-bet-and-settlement-service`. The default branch
also publishes the `latest` tag; commits and releases receive SHA and ref tags.

Set the repository Actions secret `CI_POSTGRES_PASSWORD` under **Settings → Secrets and variables
→ Actions**. The PostgreSQL service and test connection both read this secret. If it is unavailable
(for example, on a pull request from a fork), the workflow generates a run-specific password for
its temporary PostgreSQL service. GHCR authentication uses the automatically provided
`GITHUB_TOKEN`; enable read/write Actions permissions for packages in repository settings if
publishing is denied.

### Deploy to Render

The workflow can trigger a Render deploy after tests pass and the `latest` image is published from
`main`. To enable it:

1. Create a Render PostgreSQL database and a Render Web Service in the same region.
2. Create the service from the existing image
   `ghcr.io/wondu-em/wallet-bet-and-settlement-service:latest`. If the GHCR package is private,
   configure a Render registry credential using a GitHub token with `read:packages` access.
3. Set the service's health check path to `/health` and configure these environment variables:
   `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_HTTP_PORTS=10000`,
   `ConnectionStrings__Default` as an Npgsql connection string using the database's internal host,
   database name, username, and password (for example,
   `Host=<internal-host>;Port=5432;Database=<database>;Username=<user>;Password=<password>;SSL Mode=Require`),
   `Jwt__Key` (at least 32 characters), `Webhook__Secret` (at least 32 bytes), `Admin__Email`,
   and `Admin__Password`.
4. In the Render service's **Settings**, create/copy its deploy hook URL. Add that URL to the
   GitHub repository as the Actions secret `RENDER_DEPLOY_HOOK_URL`.
5. Push changes to `main`. Once validation and image publishing succeed, GitHub Actions calls the
   Render deploy hook. Version-tag pushes publish images but do not trigger this Render deployment.

The Docker image listens on the port configured by `ASPNETCORE_HTTP_PORTS`; Render uses port
`10000` here. The API applies pending database migrations and seeds the configured admin during
startup. Keep database credentials, JWT keys, webhook secrets, and the deploy hook URL only in
Render/GitHub secret settings, never in source control.

## API overview

| Method | Route | Access |
|---|---|---|
| `POST` | `/auth/register` | Anonymous |
| `POST` | `/auth/login` | Anonymous |
| `GET` | `/wallet` | User |
| `POST` | `/wallet/deposit` | User; `Idempotency-Key` required |
| `POST` | `/wallet/withdraw` | User; `Idempotency-Key` required |
| `GET` | `/wallet/transactions` | User |
| `GET` | `/events`, `/events/{id}` | Authenticated |
| `POST` | `/admin/events` | Admin |
| `POST` | `/admin/events/{id}/close` | Admin |
| `POST` | `/bets` | User; `Idempotency-Key` required; rate limited |
| `GET` | `/bets` | User (own bets only) |
| `POST` | `/admin/events/{id}/settle` | Admin; `Idempotency-Key` required |
| `POST` | `/admin/events/{id}/void` | Admin; `Idempotency-Key` required |
| `POST` | `/webhooks/deposit` | Signed provider request; `Idempotency-Key` required |

Protected endpoints use `Authorization: Bearer <token>` from `/auth/login`. Money amounts are
integer minor units: 100 means 1.00 ETB. Errors use Problem Details; insufficient funds and
idempotency-key payload mismatches return 422, state conflicts return 409, and rate limiting
returns 429.

### Deposit webhook signing

The JSON payload includes `providerEventId`, `providerReference`, `userId`, `amount`, and `status`
(`Pending`, `Confirmed`, or `Failed`). Include:

- `Idempotency-Key`: request retry key (1 to 200 characters)
- `X-Timestamp`: Unix seconds, within five minutes of the server clock
- `X-Signature`: hexadecimal HMAC-SHA256 using the configured `Webhook:Secret`, over the UTF-8
  bytes of `timestamp + "." + exact raw JSON body`

The webhook is simulated for this application; it does not call a payment provider. A confirmed
deposit is credited once. Provider event ID deduplication is independent of request-key replay.

## Design

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the system architecture and
[docs/DESIGN.md](docs/DESIGN.md) for the requested scaling, reconciliation, and production-readiness
discussion.
