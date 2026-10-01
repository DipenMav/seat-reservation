# Write-up

## 1. The atomic decision

**Mechanism: a conditional UPDATE guarded on the seat's current state, under PostgreSQL's row lock.**

```sql
UPDATE seats SET status = 'confirmed', reservation_id = $res, updated_at = now()
 WHERE show_id = $show AND seat_number = $seat AND status = 'available';
-- 1 row  → this transaction owns the seat
-- 0 rows → someone else does → 409 SEAT_UNAVAILABLE
```

**Why it's race-free.** 500 transactions run this statement against A12 at the same moment:

1. The first one to reach the row takes its row lock and flips it.
2. The other 499 queue on that lock.
3. Under READ COMMITTED, when the lock is released Postgres **re-evaluates the WHERE clause against the newly committed row version**. That's the EvalPlanQual recheck.
4. `status = 'available'` is now false, so each of them updates 0 rows and gets a clean 409.

The decision and the write happen in one statement, so there's no read-then-write window. The winner is whichever transaction Postgres serialises first; the application never picks one.

**Belt and braces in the schema.** Ownership is a single column on the seat row, `seats.reservation_id`, guarded by `CHECK ((status = 'available') = (reservation_id IS NULL))`. A seat row can point at only one reservation, so a double-sell can't even be represented. Even buggy application code couldn't commit one.

**The whole reserve is one READ COMMITTED transaction of guarded writes.** Each step either affects the expected rows or causes a rollback:

| # | Statement | Guard | 0 rows means |
|---|---|---|---|
| 1 | `INSERT reservations` | (new row) | |
| 2 | `INSERT idempotency_keys … ON CONFLICT DO NOTHING` | `UNIQUE(show_id, user_id, idempotency_key)` | key already used → replay or 409 |
| 3 | `INSERT show_user_limits … ON CONFLICT DO UPDATE SET reserved_count = reserved_count + n WHERE reserved_count + n <= limit` | conditional upsert on the user's counter row | over the limit → 409 |
| 4 | `UPDATE seats … WHERE status = 'available'`, once per seat in sorted order | conditional update | seat taken → 409 |
| 5 | `INSERT reservation_seats`, `COMMIT` | | |

**The per-user limit.** A `SELECT count(*)` followed by an insert would let 10 parallel requests all see "3 < 4". Instead the limit is a counter row per `(show, user)`. Step 3 is a single conditional upsert, so the row lock serialises one user's concurrent requests, and each one re-checks `reserved_count + n <= limit` against the committed value.

**Multi-seat requests and deadlock.** Requests are all-or-nothing. Seats are trimmed, de-duplicated and **sorted ordinally**, then locked one statement at a time in that order (pipelined in one round trip). Every transaction acquires seat locks in the same global order, so there's no circular wait between two multi-seat requests.

Across operations the lock order is also fixed:
- **Reserve:** idempotency key → user counter → seats.
- **Cancel:** reservation row → user counter → seats.

Both take the user counter before seats, and nothing takes them in reverse. A deadlock or serialization failure (`40P01`/`40001`) is still retried up to 3 times; the transaction is all-or-nothing and the key makes the retry safe. `reservation_transaction_retries_total` makes any regression visible. In the 300-way overlapping multi-seat test and the 20k bursts it stayed at 0.

**Fast-path decline.** Before opening a write transaction, a single read-only statement checks show, key, user count and seat availability, all from one MVCC snapshot. It declines the obvious losers of a hot-seat storm cheaply.

- It can only **decline** or pass a request through to the transaction; it never grants anything.
- A decline based on a slightly stale read is still a correct answer ("it was taken when you asked").
- Being one statement matters. A retry of the winning request sees *both* the winner's idempotency row and its seat change, or *neither*. It's never told "seat taken" for a seat its own key won.

## 2. Idempotency

- **Where:** the `idempotency_keys` table in Postgres, unique on `(show_id, user_id, idempotency_key)`. Each row stores a SHA-256 of the normalised request (the sorted seat set) and the `reservation_id`. It survives restarts and is shared across instances.
- **Exactly-once:** the key row is inserted **inside the same transaction** that books the seats (step 2).
  - Two concurrent requests with the same key collide on the unique index, and the second blocks until the first finishes.
  - If the first **commits**, the second's `ON CONFLICT DO NOTHING` affects 0 rows. It rolls back without touching seats and returns the stored reservation.
  - If the first **rolls back** (e.g. seat taken), the second claims the key and is evaluated on its own merits.
  - The key and the booking commit together or not at all. There's no state where the key exists without the booking, or vice versa.
- **Same key, same body:** `201` with the *original* reservation and `Idempotent-Replayed: true`, so a client retrying after a timeout gets exactly what the first call would have returned. It's counted as `reservations_declined_total{reason="idempotent_replay"}`.
- **Same key, different body:** the hashes differ, so `409 IDEMPOTENCY_KEY_REUSED` and no state change.
- **Declines are not stored against the key.** A key is bound only to a successful reservation. After "seat taken" a retry is re-evaluated, which keeps the table small, but the key isn't burned by a failure. That was a deliberate choice; Stripe-style "replay the decline too" is the alternative.

A bug found while building this: the first version opened a **second** pooled connection to read the winning key while still holding the transaction's connection. In the 100-way storm test, 60 losers each held one connection and waited for another, so the pool deadlocked until the 60s acquire timeout and returned 90 × 503. The rule now is: never hold a pooled connection while acquiring another.

## 3. Holds and expiry

**Chosen model: immediate confirmation plus an explicit, owner-only cancel.** `held` exists in the schema and the API, but nothing produces it, so `held_seats` is always 0. Without a payment step, a hold only adds a second state machine and a background sweeper to get wrong.

Cancel is one transaction:
1. `UPDATE reservations SET cancelled WHERE id = $id AND user_id = $caller AND status = 'confirmed'`. Ownership and "still confirmed" are part of the guard, so a non-owner changes nothing (403), and two concurrent cancels release once.
2. Decrement the user's counter.
3. Release seats with `WHERE id = $seat AND reservation_id = $this_reservation AND status = 'confirmed'`.

Step 3's guard is the "never resurrect" guarantee: a release can only touch seats this reservation still owns. It cannot flip a seat that now belongs to someone else, even with a stale view. The cancel-racing-a-storm test checks this: the seat ends up either available or owned by exactly one racer.

**Adding timed holds:**
- Add `held_until` to seats. Reserve writes `status = 'held', held_until = now() + 10 min`.
- Confirm is `UPDATE … WHERE reservation_id = $r AND status = 'held' AND held_until > now()`.
- Expiry is a sweeper running `UPDATE seats SET status = 'available', reservation_id = NULL WHERE status = 'held' AND held_until < now()`, plus decrementing the user counters in the same transaction.
- Readers treat an expired `held` as available. The same conditional-update discipline applies, so expiry can never release a confirmed seat.

## 4. Consistency vs availability under a partition

**Consistency wins for writes.** There is one primary Postgres and no local fallback.

- If the API can't reach the database, `/health/ready` goes 503, so the load balancer stops sending traffic, and reserve/cancel return `503 SERVICE_UNAVAILABLE` with `Retry-After`.
- The service never accepts a reservation it can't durably decide. A partition costs availability (people can't book for a while), never correctness (two people holding A12).
- **Ambiguous outcome** (the connection drops during COMMIT): the client doesn't know whether it got the seat. That's what the idempotency key is for. Retrying with the same key returns the committed reservation, or books it if the commit didn't land.
- The reserve transaction deliberately ignores the HTTP request's cancellation token. Once started, it finishes, so a client disconnect never leaves the server-side outcome half-done.
- Reads (`GET /shows/{id}`) could come from a replica and be allowed to go slightly stale. They aren't in this version.

## 5. Observability: what pages at 2am

| Alert | Why |
|---|---|
| `reconciliation_drift > 0` (sev1) | Three independent counts, computed in one snapshot, disagree: seat statuses vs `total_seats`, confirmed seats vs active reservation_seats, confirmed seats vs the per-user counters. That means a correctness bug: a possible double-sell, lost seat or limit leak. Freeze sales. |
| Reserve 5xx above 0.5% for 5m | The database is down, the pool is exhausted, or there's a bug. Declines are 409s and never page. |
| `/health/ready` failing | Postgres is unreachable. |
| Reserve p99 above 2s (ticket) | Lock queues on hot rows or pool saturation. A capacity problem, not a correctness one. |
| Transaction retries rising (ticket) | Deadlocks shouldn't happen given the lock order, so this suggests a regression. |
| Seat metrics stale (ticket) | The gauges come from DB reads, so stale gauges mean dashboards are lying. |

Rules are in `ops/prometheus-alerts.yml`.

**Seat-taken spikes are not an alert.** That's an on-sale working. The seat gauges are queried from Postgres at scrape time instead of being tracked in process memory, which is why they reconcile with the API exactly and survive restarts. The burst script checks this: `show_seats` equals the API state, and the counter deltas equal the observed outcomes.

Every log line carries `request_id` (also returned in `X-Correlation-ID` and in error bodies), so a user's complaint can be traced to its single log line with `decline_reason`, `attempts` and `duration_ms`.

## 6. AI usage: directed vs decided

> **Note to self before submitting: edit this section so it accurately describes your own role.** The facts below describe how the implementation was produced; only you can state which design decisions were yours.

- **Design docs** (`docs/`) were written before any code. They fix the invariants, the stack (ASP.NET Core, EF Core, PostgreSQL), "the database makes the decision", all-or-nothing, explicit cancel, the per-user counter row, database-backed idempotency, and READ COMMITTED with row locks rather than SERIALIZABLE.
- **Implementation:** an AI coding agent (Claude Code) generated the code, tests, burst tool, Dockerfile, deploy configs and this write-up from those docs, working in small verified steps.
- **Decisions the AI made during implementation, which I reviewed and accepted:**
  - `seats.reservation_id` with a CHECK constraint, so a double-sell is unrepresentable
  - the single-snapshot read-only preflight
  - pipelining sorted per-seat UPDATEs in one batch
  - a separate tiny connection pool for health and metrics
  - replay returns 201 with a header, and declines are not bound to keys
  - DB-sourced gauges with `reconciliation_drift` cross-checks
  - not tying the transaction to the request's cancellation token
- **Verification:** none of the AI's claims were taken on trust. Every concurrency property has a test against real Postgres, and the burst tool checks them end to end against the running container.
  - The idempotency storm test caught a real pool-starvation deadlock in the AI's first version; that's the `fix(reserve)` commit.
  - A self-review caught that the per-user upsert's insert branch relied on the preflight; that's the second `fix(reserve)` commit.
  - The commit history shows these steps.

## 7. What I'd do next

1. **Holds with expiry, then payment**, as described in section 3.
2. **Real auth:** JWT validation (issuer, audience, expiry) replacing the token-is-the-user scheme. Nothing downstream changes.
3. **Admission control for on-sale:** a per-show queue or token bucket in front of reserve. At 20k requests for 2,000 seats, most work is declining people; a virtual waiting room is kinder and cheaper than 409s.
4. **A hot-row strategy at higher scale:** the current design is bounded by row-lock throughput on single seats and by the connection pool. Options are a pre-check against an in-memory/Redis "sold" bitmap as a pure optimisation (the database stays the authority), or sharding shows across databases.
5. **Idempotency key retention:** a TTL and cleanup job, and possibly storing declines if clients need byte-identical replays.
6. **Read replicas for `GET /shows/{id}`**, and caching the immutable show metadata.
7. **Run the burst against two or more instances** to demonstrate that correctness doesn't depend on instance count (the design guarantees it; it should also be shown).
8. **OpenTelemetry traces** around the transaction steps, and a Grafana dashboard checked into `ops/`.
