# Wallet & Bet Settlement Service

A .NET 10 and PostgreSQL backend for a play-money ETB wallet and fixed-odds betting. It includes
wallet deposits and withdrawals, event and bet management, settlement and voiding, and a signed
simulated deposit webhook. No real money or payment provider is involved.

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
