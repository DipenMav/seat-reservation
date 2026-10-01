# SeatReservation — Requirements

## 1. Purpose

SeatReservation is a backend service that sells assigned seats for an event such as a concert or movie hall.

The primary challenge is **correctness under high concurrency**.

The service must remain correct when thousands of users attempt to reserve seats simultaneously, including many users competing for the same seat.

---

# 2. Core Business Invariants

The following invariants must always hold.

## INV-01 — No Double Booking

A seat can belong to at most one active/confirmed reservation at a time.

For a specific show and seat:

```text
At most one user can own the seat.
```

If 500 users concurrently attempt to reserve `A12`:

```text
Exactly one request succeeds.

All other requests receive a domain-level conflict.

No duplicate reservation may exist.
```

---

## INV-02 — Per-User Limit

Each show has a configurable `per_user_limit`.

Default:

```text
4
```

A user must never own more than this number of active/confirmed seats for a show.

The limit must hold under concurrency.

Example:

```text
User U1
10 concurrent requests
limit = 4
```

Final state:

```text
U1 owns <= 4 seats
```

Never rely solely on:

```text
SELECT COUNT(...)
```

followed by an application-level decision.

---

## INV-03 — Idempotency

A reservation request contains an idempotency key.

The same:

```text
user + show + idempotency_key
```

represents the same logical reservation attempt.

### Same key + same request

Must return the original reservation/result.

No additional reservation may be created.

### Same key + different request

Must return:

```text
409 Conflict
```

Example:

```text
First:
key = ABC
seats = [A12]

Second:
key = ABC
seats = [A13]

Result:
409 Conflict
```

---

## INV-04 — Identity

The authenticated user identity is authoritative.

The reservation request must not be able to override the authenticated identity.

A malicious request such as:

```json
{
  "user_id": "another-user",
  "seats": ["A12"]
}
```

must not allow the caller to act as another user.

Cancellation must only be allowed for the authenticated owner.

---

## INV-05 — Money

Money must be represented using integer paise.

Example:

```text
₹250.00 = 25000
```

Never use floating-point values for money.

---

## INV-06 — Reconciliation

For every show:

```text
available + held + confirmed = total_seats
```

This invariant must hold:

- before reservations
- after successful reservations
- after failed reservations
- after concurrent reservation bursts
- after cancellation
- after idempotent retries

---

# 3. Functional Requirements

## FR-01 — Create Show

Endpoint:

```http
POST /shows
```

Purpose:

Create a new show and its assigned seats.

Request:

```json
{
  "name": "friday-night",
  "seats": [
    "A1",
    "A2",
    "A3",
    "A4"
  ],
  "price_paise": 25000
}
```

Optional/configurable:

```text
per_user_limit
```

Default:

```text
4
```

The endpoint must create:

- show
- all seats
- initial seat state = `available`

The response must contain the created show and its identifier.

---

# 4. FR-02 — Reserve Seats

Endpoint:

```http
POST /shows/{showId}/reserve
```

Authentication:

```http
Authorization: Bearer <token>
```

Request:

```json
{
  "seats": [
    "A12"
  ],
  "idempotency_key": "unique-client-key"
}
```

The authenticated identity must come from the authentication context.

The request body must not be used to determine the owner.

---

## Successful Response

Status:

```text
201 Created
```

Example:

```json
{
  "reservation_id": "uuid",
  "show_id": "uuid",
  "user_id": "user-123",
  "seats": [
    "A12"
  ],
  "amount_paise": 25000,
  "status": "confirmed"
}
```

---

# 5. FR-03 — Seat Already Taken

If another user already owns the requested seat:

```text
409 Conflict
```

This is an expected domain outcome.

It must not become:

```text
500 Internal Server Error
```

Example:

```text
User A → A12 → success

User B → A12 → 409
User C → A12 → 409
```

---

# 6. FR-04 — Concurrent Seat Reservation

The implementation must be safe under concurrent requests.

Example:

```text
500 concurrent requests
        ↓
      A12
```

Expected:

```text
1 × 201
499 × 409
0 × 5xx
```

The exact winner is not important.

The invariant is that only one reservation owns the seat.

---

# 7. FR-05 — Multi-Seat Reservation

The selected business behavior is:

> **All-or-nothing.**

Example:

```json
{
  "seats": [
    "A12",
    "A13",
    "A14"
  ]
}
```

If all seats are available:

```text
success
```

If any requested seat is unavailable:

```text
entire request fails
```

No partial reservation is allowed.

---

# 8. FR-06 — Per-User Limit

Default:

```text
4 seats per user per show
```

Example:

```text
User U1 already owns 3 seats.

U1 requests 2 additional seats.

Result:
409 Conflict
```

The operation must remain correct when multiple requests from the same user arrive concurrently.

---

# 9. FR-07 — Idempotent Retry

Example:

```text
Request 1
---------
Key: ABC
Seats: A12

→ 201
→ Reservation R1
```

Retry:

```text
Request 2
---------
Key: ABC
Seats: A12

→ original reservation R1
```

No second reservation is created.

---

# 10. FR-08 — Idempotency Conflict

Example:

```text
Request 1
---------
Key: ABC
Seats: A12

Request 2
---------
Key: ABC
Seats: A13
```

Result:

```text
409 Conflict
```

The second request must not modify reservation state.

---

# 11. FR-09 — Cancellation

Endpoint:

```http
POST /reservations/{reservationId}/cancel
```

Only the reservation owner may cancel.

If the authenticated user is not the owner:

```text
403 Forbidden
```

A successful cancellation must release the associated seats.

The release must be atomic.

Cancellation must never make a seat available if it has already been assigned to another user.

---

# 12. FR-10 — Show State

Endpoint:

```http
GET /shows/{showId}
```

The response must expose:

- show information
- total seats
- available count
- held count
- confirmed count
- per-seat state

Example:

```json
{
  "id": "uuid",
  "name": "friday-night",
  "price_paise": 25000,
  "total_seats": 100,
  "available_seats": 96,
  "held_seats": 0,
  "confirmed_seats": 4,
  "seats": [
    {
      "seat": "A1",
      "status": "available"
    },
    {
      "seat": "A2",
      "status": "confirmed"
    }
  ]
}
```

The counts must reconcile:

```text
available_seats
+
held_seats
+
confirmed_seats
=
total_seats
```

---

# 13. FR-11 — Liveness

Endpoint:

```http
GET /health/live
```

Purpose:

Determine whether the application process is alive.

A database outage should not necessarily make liveness fail.

---

# 14. FR-12 — Readiness

Endpoint:

```http
GET /health/ready
```

Purpose:

Determine whether the application can serve traffic.

The readiness check must verify PostgreSQL connectivity.

If PostgreSQL is unavailable:

```text
503 Service Unavailable
```

The application must fail closed rather than claiming readiness.

---

# 15. FR-13 — Metrics

Endpoint:

```http
GET /metrics
```

Expose Prometheus-compatible metrics.

Minimum required metrics:

```text
reservations_confirmed_total

reservations_declined_total{reason="seat_taken"}

reservations_declined_total{reason="per_user_limit"}

reservations_declined_total{reason="idempotent_replay"}

reservations_declined_total{reason="idempotency_conflict"}

seats_available

seats_held

seats_confirmed
```

Metrics must represent real application state and must not contradict the API.

---

# 16. FR-14 — Structured Logging

Logs must be structured.

Important fields should include:

```text
request_id
trace_id
show_id
reservation_id
user_id
operation
result
decline_reason
duration_ms
```

Never log:

- bearer tokens
- passwords
- database credentials
- secrets

---

# 17. FR-15 — Burst Test

Provide a command-line burst-testing tool.

Example:

```bash
./burst.sh <BASE_URL>
```

or equivalent.

The tool must be capable of testing:

- hot-seat contention
- concurrent users
- same-user concurrency
- idempotent retries

The output should include:

```text
confirmed
seat_taken
per_user_limit
idempotent_replay
idempotency_conflict
other_4xx
5xx
duration
```

It must also verify the final show reconciliation invariant.

---

# 18. Non-Functional Requirements

## NFR-01 — Concurrency

The service must handle high contention without double booking.

---

## NFR-02 — Correctness

Database state must remain correct even when requests execute concurrently.

---

## NFR-03 — Atomicity

Multi-step reservation operations must be transactional.

---

## NFR-04 — Availability

Expected business conflicts must not produce 5xx responses.

---

## NFR-05 — Observability

The system must provide enough metrics and logs to diagnose reservation failures and concurrency problems.

---

## NFR-06 — Deployability

The application must run from a clean checkout.

A Dockerfile must be provided for reproducible deployment.

Local development does not require Docker.

---

## NFR-07 — Testability

Concurrency-sensitive behavior must be covered by automated tests.

---

# 19. Expected HTTP Status Codes

| Situation | Status |
|---|---:|
| Show created | 201 |
| Reservation created | 201 |
| Successful cancellation | 200 / 204 |
| Invalid request | 400 |
| Missing/invalid authentication | 401 |
| User not allowed to cancel | 403 |
| Show/reservation not found | 404 |
| Seat already taken | 409 |
| Per-user limit exceeded | 409 |
| Idempotency conflict | 409 |
| Dependency unavailable/readiness failure | 503 |
| Unexpected failure | 500 |

---

# 20. Critical Acceptance Tests

The following scenarios are mandatory.

## AT-01 — Hot Seat

Given a fresh show with seat `A12`.

Send:

```text
1000+ concurrent reservation requests
```

targeting:

```text
A12
```

Expected:

```text
exactly 1 confirmed reservation
all other valid contenders receive 409
zero unexpected 5xx
```

---

## AT-02 — Same User Limit

Given:

```text
per_user_limit = 4
```

Send:

```text
10 concurrent reservation requests
```

from the same user.

Expected:

```text
user owns no more than 4 seats
```

---

## AT-03 — Idempotency Storm

Send many concurrent requests with:

```text
same user
same show
same idempotency key
same body
```

Expected:

```text
one logical reservation
all retries resolve to the same reservation/result
```

---

## AT-04 — Idempotency Conflict

First request:

```text
key = ABC
seats = A12
```

Second request:

```text
key = ABC
seats = A13
```

Expected:

```text
409 Conflict
```

No additional reservation.

---

## AT-05 — Multi-Seat Atomicity

Request:

```text
A12
A13
A14
```

If one seat is unavailable:

```text
none of the requested seats are reserved
```

---

## AT-06 — Identity Spoofing

Authenticated user:

```text
user-A
```

Request attempts to specify:

```text
user-B
```

Expected:

```text
reservation belongs to user-A
```

The request body cannot override authentication identity.

---

## AT-07 — Cancellation Ownership

User A owns reservation R1.

User B attempts to cancel R1.

Expected:

```text
403 Forbidden
```

Reservation remains unchanged.

---

## AT-08 — Reconciliation

At every completed state transition:

```text
available + held + confirmed == total_seats
```

must hold.

---

# 21. Design Decisions Still To Be Finalized

The following decisions must be documented before implementation:

1. Exact PostgreSQL transaction strategy.
2. Exact seat acquisition mechanism.
3. Exact per-user concurrency mechanism.
4. Exact idempotency transaction strategy.
5. Whether immediate confirmation or temporary holds are used.
6. Exact isolation/locking behavior.
7. Cancellation state transition.
8. Metrics implementation.
9. Authentication implementation.

These decisions must be documented in:

```text
.ai/ARCHITECTURE.md
.ai/DATABASE.md
.ai/CONCURRENCY.md
.ai/API.md
```

before the corresponding implementation is finalized.

---

# 22. Engineering Principle

The system should be simple enough to understand but strong enough to survive the concurrency tests.

The core principle is:

> **The database must make the atomic correctness decision.**

Application-level checks may validate requests, but database transactions, constraints, locking, and conditional updates must protect the actual invariants.