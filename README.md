# Seat Reservation at Scale

A JSON API that sells assigned seats for a show and stays correct when thousands of buyers stampede the same seats at once:
no seat sold twice, no user over their limit, no retry booked twice. ASP.NET Core (.NET 10) + PostgreSQL, one service, one database.

- **Live URL:** `https://<your-deployment>` ← _fill in after deploy_
- **Metrics:** `GET /metrics` (Prometheus) · **Health:** `GET /health/live`, `GET /health/ready`
- **Design write-up:** [WRITEUP.md](WRITEUP.md) · design docs written before the code: [docs/](docs/)

## Run it (clean checkout)

```bash
docker compose up --build          # api on :8080 + postgres, the same image that is deployed
curl localhost:8080/health/ready   # {"status":"Healthy",...}
./burst.sh http://localhost:8080   # the on-sale stampede + verification
```

No Docker? Any Postgres + .NET 10 SDK works:
`ConnectionStrings__Default="Host=localhost;Database=seatres;Username=postgres;Password=postgres" dotnet run --project src/SeatReservation.Api`
(`DATABASE_URL=postgres://user:pass@host:5432/db` is also accepted). Migrations run automatically at startup.

## One-command burst

```bash
./burst.sh <BASE_URL> [--requests 20000] [--concurrency 500] [--seats 2000] [--hot-seats 5] [--admin-token admin]
# or: make burst URL=<BASE_URL>
```

It waits for readiness (so it survives a cold start), creates a fresh show and fires all requests at once:

| Share | Scenario |
|---|---|
| 40% | **Hot-seat storm.** Thousands of distinct users fight over 5 seats (A12–A16). |
| 35% | General traffic: 1–2 random seats each, from a user pool small enough that limits get hit. |
| 5% | Same-user stampede: 10 parallel requests per user (limit 4). |
| 2% | Spoofed identity: the body says `"user_id": "victim-N"`. |
| ~18% | Retries of earlier requests: 2/3 with the same key and body, 1/3 with the same key and *different* seats. |

While the burst runs it samples `GET /shows/{id}` to check the invariant *during* the burst. Afterwards it prints the outcome distribution and latency, then checks:

- exactly one 201 per hot seat, and every other contender got a 409
- 0 5xx
- no seat was granted to two reservations
- one reservation per idempotency key
- every booking belongs to the token's user
- the per-user limit holds
- `available + held + confirmed == total`, both during and after the burst
- metrics agree with the API state
- only the owner can cancel, and a cancelled seat can be re-booked

The exit code is non-zero if any check fails, and a JSON summary is written to `burst-results/`.

Result against the local Docker stack (Apple M-series laptop):

```
Outcome distribution
  confirmed                1,626
  idempotent_replay          279
  seat_taken              17,505
  per_user_limit             269
  idempotency_conflict       321
  other_4xx                    0
  5xx                          0
  transport_error              0
  total                   20,000
Duration          2.72s  (7,346 req/s)
Latency (client)  p50 59 ms   p95 138 ms   p99 193 ms
  [PASS] hot seat A12: exactly one 201 — 1 winner(s), 1599 declined (1599 x 409)
  ...
  [PASS] final reconciliation: available + held + confirmed == total — 48 + 0 + 1952 = 2000 (total 2000)
  [PASS] metrics: counters match observed outcomes — confirmed 1626 vs 1626, seat_taken 17505 vs 17505, ...
RESULT: PASS (22 checks)
```

## API

All routes are served at the root (as in the assignment) and also under `/api`. Money is always integer paise.

### Auth

`Authorization: Bearer <token>`. **The token is the identity:** `Bearer user-123` acts as user `user-123`.
`Bearer <ADMIN_TOKEN>` (env var, default `admin`) is the admin. No handler reads a user from the request body.
The token scheme is deliberately simple so reviewers can mint any number of users. Swapping in JWT validation only changes the auth handler.

### Create a show (admin)

```bash
curl -X POST $URL/shows -H 'Authorization: Bearer admin' -H 'Content-Type: application/json' \
  -d '{"name":"friday-night","seats":["A1","A2","A3","A12"],"price_paise":25000,"per_user_limit":4}'
```
`201` with the show `id` and every seat `available`. `per_user_limit` is optional and defaults to 4. A float or string `price_paise` is rejected with 400.

### Reserve

```bash
curl -X POST $URL/shows/$SHOW/reserve -H 'Authorization: Bearer user-123' -H 'Content-Type: application/json' \
  -d '{"seats":["A12"],"idempotency_key":"9e7c6f2b"}'          # or send the key as an Idempotency-Key header
```
```json
201 {"reservation_id":"…","show_id":"…","user_id":"user-123","seats":["A12"],"amount_paise":25000,"status":"confirmed","created_at":"…"}
```

| Situation | Status | `error.code` |
|---|---|---|
| Reserved | 201 | |
| Retry with the same key and the same seats | 201: the **original** reservation, plus the header `Idempotent-Replayed: true` | |
| Seat already confirmed to someone | 409 | `SEAT_UNAVAILABLE` |
| Would exceed the per-user limit | 409 | `USER_LIMIT_EXCEEDED` |
| Same key, different seats | 409 | `IDEMPOTENCY_KEY_REUSED` |
| Bad input (duplicate seats, missing key, seat not in show…) | 400 | `VALIDATION_ERROR` / `UNKNOWN_SEAT` |
| Unknown show | 404 | `SHOW_NOT_FOUND` |
| Database unreachable | 503 + `Retry-After` | `SERVICE_UNAVAILABLE` |

- **Multi-seat requests are all-or-nothing.** `["A12","A13"]` with A13 taken books nothing and returns 409. This holds under concurrency.
- **Same body means the same normalised seat set**, so `["A13","A12"]` and `["A12","A13"]` count as the same request.
- **Only successful reservations are bound to a key.** A declined attempt isn't, so a retry after "seat taken" is simply re-evaluated.

Errors always look like `{"error":{"code","message","correlation_id"}}`. They never include stack traces or SQL.

### Cancel (owner only)

```bash
curl -X POST $URL/reservations/$RID/cancel -H 'Authorization: Bearer user-123'
```
- **Owner:** `200` with `"status":"cancelled"`. The seats return to `available` and can be booked again.
- **Anyone else:** `403 RESERVATION_ACCESS_DENIED`, and nothing changes.
- **Cancelling again:** `200`, with no extra release.

The release model is **explicit cancel**, not timed holds: seats are confirmed immediately, so `held_seats` is always 0. See WRITEUP.md.

### Show state

```bash
curl $URL/shows/$SHOW                                   # public
curl $URL/shows/$SHOW -H 'Authorization: Bearer admin'  # also shows reservation_id + user_id per confirmed seat
```
```json
{"id":"…","name":"friday-night","price_paise":25000,"per_user_limit":4,"total_seats":4,
 "available_seats":3,"held_seats":0,"confirmed_seats":1,"reconciled":true,
 "seats":[{"seat":"A1","status":"available"},{"seat":"A12","status":"confirmed"}, …]}
```
`GET /reservations/{id}` returns a single reservation, to its owner or an admin.

## Observability

**Metrics** (`/metrics`, Prometheus text):

| Metric | Meaning |
|---|---|
| `reservations_confirmed_total` | New reservations committed |
| `reservations_declined_total{reason}` | Declines by `seat_taken`, `per_user_limit`, `idempotent_replay`, `idempotency_conflict`, `invalid_request`, `show_not_found` |
| `reservations_cancelled_total`, `seats_reserved_total`, `seats_released_total` | Lifecycle, in reservations and in seat units |
| `seats_available`, `seats_held`, `seats_confirmed`, `seats_total` | Live seat state. **Read from Postgres at scrape time**, so the gauges always agree with the API and survive restarts and multiple instances |
| `show_seats{show_id,status}` | The same, per show, for the 20 newest shows |
| `reconciliation_drift{check}` | Three independent counts cross-checked in one snapshot: seat status sum vs `total_seats`, confirmed seats vs active `reservation_seats`, confirmed seats vs per-user counters. **Must be 0** |
| `reservation_duration_seconds{outcome}`, `http_request_duration_seconds{endpoint,code}` | Latency |
| `reservation_transaction_retries_total` | Deadlock/serialization retries (expected to stay at 0) |

Alert rules for what pages at 2am are in [ops/prometheus-alerts.yml](ops/prometheus-alerts.yml).

**Logs:** one flat JSON line per request on stdout, so they show up in the platform's log viewer.
Each line has `request_id` (the `X-Correlation-ID` header, echoed back and included in error bodies), `trace_id`, `operation`, `show_id`, `user_id`, `reservation_id`, `result`, `decline_reason`, `seat_count`, `attempts`, `status_code` and `duration_ms`. Tokens are never logged.

```json
{"ts":"…","level":"info","msg":"POST /shows/…/reserve -> 409 in 3.1 ms","request_id":"01a0f8d6…","operation":"reserve","show_id":"…","user_id":"fan-17","result":"declined","decline_reason":"seat_taken","seat_count":1,"attempts":0,"status_code":409,"duration_ms":3.1}
```

**Health:**
- `/health/live` never touches the database.
- `/health/ready` returns 503 until migrations have run, and again whenever Postgres can't answer `SELECT 1` within 3s, so it fails closed.
  Health checks and metrics scrapes use their own tiny connection pool, so a burst that saturates the main pool can't make the instance look dead.

## Tests

```bash
dotnet test     # 31 tests against real Postgres via Testcontainers (Docker), or set TEST_DATABASE_URL
```
Concurrency tests run through the real HTTP pipeline and the database's real locking:
- 500-way hot seat
- 10 parallel requests from one user on a limit-4 show
- idempotency storms: 100 identical requests, and 60 with mixed bodies on one key
- 300 overlapping multi-seat requests, checking all-or-nothing and no deadlock 5xx
- a cancel racing a 200-request storm, checking for no resurrection
- 20 concurrent cancels

The tests also cover readiness failing closed with the database unreachable.

## Deploy

The image is the same everywhere (`Dockerfile`), and `/health/ready` is the platform health check.

- **Render:** New → Blueprint → this repo. [render.yaml](render.yaml) creates the web service and a Postgres, and wires up `DATABASE_URL`. Set `ADMIN_TOKEN` when prompted. Free instances sleep when idle; the service is built to come back healthy from that cold start.
- **Railway:** New project → deploy from repo ([railway.json](railway.json)) → add Postgres → set `DATABASE_URL=${{Postgres.DATABASE_URL}}` and `ADMIN_TOKEN`.
- **Fly.io:** `fly launch --copy-config --no-deploy && fly postgres create && fly postgres attach <pg> && fly secrets set ADMIN_TOKEN=… && fly deploy` ([fly.toml](fly.toml) raises Fly's default 25-request concurrency limit).

| Env var | Default | |
|---|---|---|
| `DATABASE_URL` or `ConnectionStrings__Default` | none | Postgres connection |
| `ADMIN_TOKEN` | `admin` | Bearer token that may create shows |
| `DB_MAX_POOL_SIZE` | 40 | Keep below the database's `max_connections` |
| `DB_CONNECT_TIMEOUT` | 60 | Seconds a request may wait for a pooled connection during a stampede, so it queues instead of getting a 5xx |
| `PORT` | 8080 | Set automatically by most platforms |

## Layout

```
src/SeatReservation.Api/
  Features/Shows/{CreateShow,GetShow}         vertical slices
  Features/Reservations/{Reserve,Cancel,GetReservation}
      Reserve/ReserveHandler.cs              ← the atomic decision lives here
  Infrastructure/{Persistence,Authentication,Observability,Health}
tests/SeatReservation.Tests/                 integration + concurrency tests
tools/Burst/                                 the burst tool behind ./burst.sh
docs/                                        requirements / architecture / database / concurrency / API design
```
