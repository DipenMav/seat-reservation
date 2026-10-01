# API Contract

## 1. Overview

The Seat Reservation API is a JSON HTTP API for creating shows, reserving seats, viewing show state, and cancelling reservations.

### Base URL

```text
/api
```

Example deployed URL:

```text
https://<public-host>/api
```

All request and response bodies use:

```http
Content-Type: application/json
```

All monetary values are represented as integer **paise**.

Example:

```json
{
  "price_paise": 25000
}
```

represents ₹250.00.

---

# 2. Authentication

The API uses bearer authentication.

```http
Authorization: Bearer <token>
```

The authenticated user identity is taken from the token.

The client must **not** provide `user_id` in the reservation request.

For the take-home exercise, the implementation may use a lightweight development authentication handler.

Example:

```http
Authorization: Bearer user-123
```

resolves the authenticated identity to:

```text
user-123
```

An administrative token is required for show creation.

Example:

```http
Authorization: Bearer admin
```

### Authentication rules

- Reservation user identity comes only from authentication.
- `user_id` supplied in a request body is ignored/rejected.
- A user cannot reserve on behalf of another user.
- Cancellation requires the authenticated user to own the reservation.
- Unauthenticated requests return `401 Unauthorized`.
- Authenticated users without permission return `403 Forbidden`.

---

# 3. Common Response Format

Successful responses return JSON specific to the endpoint.

Errors use a consistent structure:

```json
{
  "error": {
    "code": "SEAT_UNAVAILABLE",
    "message": "One or more requested seats are no longer available.",
    "correlation_id": "01J..."
  }
}
```

### Error fields

| Field | Type | Description |
|---|---|---|
| `error.code` | string | Machine-readable error code |
| `error.message` | string | Human-readable explanation |
| `error.correlation_id` | string | Correlation identifier for troubleshooting |

The API must not expose database exceptions, stack traces, SQL statements, or internal infrastructure details.

---

# 4. Create Show

Creates a new show and its assigned seats.

Every newly created seat starts as:

```text
available
```

## Endpoint

```http
POST /shows
```

### Authentication

Admin required.

```http
Authorization: Bearer admin
```

### Request

```json
{
  "name": "friday-night",
  "seats": [
    "A1",
    "A2",
    "A3",
    "A4",
    "B1",
    "B2"
  ],
  "price_paise": 25000
}
```

### Request fields

| Field | Type | Required | Description |
|---|---|---:|---|
| `name` | string | Yes | Show name |
| `seats` | string[] | Yes | Unique seat identifiers |
| `price_paise` | integer | Yes | Price per seat in paise |

### Validation

- `name` must not be empty.
- At least one seat is required.
- Seat identifiers must be unique within the request.
- Seat identifiers must not be empty.
- `price_paise >= 0`.
- Money must be represented as an integer.
- Floating-point money values are rejected.

### Success

```http
201 Created
```

Example:

```json
{
  "id": "7c7d9f6b-9f6e-4a5c-8d7b-2c0e7d3d7c11",
  "name": "friday-night",
  "price_paise": 25000,
  "per_user_limit": 4,
  "total_seats": 6,
  "created_at": "2026-10-01T10:00:00Z"
}
```

### Errors

| Status | Code | Description |
|---|---|---|
| `400` | `VALIDATION_ERROR` | Invalid request |
| `401` | `UNAUTHORIZED` | Missing/invalid authentication |
| `403` | `FORBIDDEN` | User is not allowed to create shows |
| `409` | `SHOW_ALREADY_EXISTS` | Show creation conflicts with an existing resource |

---

# 5. Get Show State

Returns the current state of all seats and aggregate counts.

## Endpoint

```http
GET /shows/{showId}
```

### Authentication

Authenticated user.

### Example response

```json
{
  "id": "7c7d9f6b-9f6e-4a5c-8d7b-2c0e7d3d7c11",
  "name": "friday-night",
  "price_paise": 25000,
  "per_user_limit": 4,
  "total_seats": 6,
  "available_seats": 4,
  "held_seats": 0,
  "confirmed_seats": 2,
  "seats": [
    {
      "seat": "A1",
      "status": "confirmed"
    },
    {
      "seat": "A2",
      "status": "available"
    },
    {
      "seat": "A3",
      "status": "available"
    },
    {
      "seat": "A4",
      "status": "confirmed"
    },
    {
      "seat": "B1",
      "status": "available"
    },
    {
      "seat": "B2",
      "status": "available"
    }
  ]
}
```

### Seat status

Possible values:

```text
available
held
confirmed
```

This implementation uses **explicit cancellation** rather than time-boxed holds.

Therefore:

```text
held_seats = 0
```

for normal operation.

The `held` state remains part of the API contract because the assignment defines the show state as:

```text
available + held + confirmed = total
```

### Reconciliation invariant

For every show:

```text
available_seats + held_seats + confirmed_seats = total_seats
```

This invariant must always hold.

### Errors

| Status | Code | Description |
|---|---|---|
| `401` | `UNAUTHORIZED` | Missing/invalid authentication |
| `404` | `SHOW_NOT_FOUND` | Show does not exist |

---

# 6. Reserve Seats

Creates a reservation for one or more seats.

Reservation is **all-or-nothing**.

If any requested seat cannot be reserved, no requested seat is reserved.

## Endpoint

```http
POST /shows/{showId}/reserve
```

### Authentication

Authenticated user required.

```http
Authorization: Bearer user-123
```

The user identity is extracted from the token.

### Idempotency

The request requires an idempotency key.

Preferred header:

```http
Idempotency-Key: 9e7c6f2b-9c18-4e7a-91c3-123456789abc
```

The implementation may also accept the key in the request body if required by the assignment test client, but the header is the canonical API representation.

### Request

```json
{
  "seats": [
    "A1",
    "A2"
  ]
}
```

The request does **not** contain `user_id`.

### Example

```http
POST /shows/7c7d9f6b-9f6e-4a5c-8d7b-2c0e7d3d7c11/reserve
Authorization: Bearer user-123
Idempotency-Key: 9e7c6f2b-9c18-4e7a-91c3-123456789abc
Content-Type: application/json
```

```json
{
  "seats": [
    "A1",
    "A2"
  ]
}
```

---

## Reservation Rules

### All-or-nothing

A multi-seat request either reserves every requested seat or none of them.

For:

```json
{
  "seats": ["A1", "A2", "A3"]
}
```

if `A2` is unavailable:

```text
A1 -> remains available
A2 -> remains unavailable
A3 -> remains available
```

The request does not partially succeed.

### Duplicate seats

Duplicate seats in the same request are invalid.

Example:

```json
{
  "seats": ["A1", "A1"]
}
```

returns:

```http
400 Bad Request
```

### Maximum seats per user

The default per-user limit is:

```text
4 seats per show
```

The limit is enforced transactionally under concurrency.

For example, if a user already owns 3 seats and attempts to reserve 2 more:

```text
3 + 2 > 4
```

the entire request fails.

No partial reservation occurs.

---

# 7. Successful Reservation

### Status

```http
201 Created
```

### Response

```json
{
  "id": "d1c3d5e7-5e2a-4a5e-bb20-6a9e2d8f6c11",
  "show_id": "7c7d9f6b-9f6e-4a5c-8d7b-2c0e7d3d7c11",
  "user_id": "user-123",
  "seats": [
    "A1",
    "A2"
  ],
  "amount_paise": 50000,
  "status": "confirmed",
  "created_at": "2026-10-01T10:15:00Z"
}
```

### Amount calculation

```text
amount_paise = number_of_seats × show.price_paise
```

For example:

```text
2 × 25000 = 50000 paise
```

No floating-point arithmetic is used.

---

# 8. Reservation Errors

## Seat unavailable

```http
409 Conflict
```

```json
{
  "error": {
    "code": "SEAT_UNAVAILABLE",
    "message": "One or more requested seats are no longer available.",
    "correlation_id": "01J..."
  }
}
```

This is expected behavior under contention.

A hot-seat concurrency test must produce:

```text
1 successful reservation
N - 1 clean 409 responses
0 unexpected 500 responses
```

---

## Per-user limit exceeded

```http
409 Conflict
```

```json
{
  "error": {
    "code": "USER_LIMIT_EXCEEDED",
    "message": "The reservation exceeds the per-user seat limit.",
    "correlation_id": "01J..."
  }
}
```

The limit must be enforced atomically.

---

## Idempotency conflict

If the same authenticated user sends the same idempotency key with a different request body:

```http
409 Conflict
```

```json
{
  "error": {
    "code": "IDEMPOTENCY_KEY_REUSED",
    "message": "The idempotency key was already used with a different request.",
    "correlation_id": "01J..."
  }
}
```

Example:

First request:

```http
Idempotency-Key: abc-123
```

```json
{
  "seats": ["A1"]
}
```

Second request:

```http
Idempotency-Key: abc-123
```

```json
{
  "seats": ["A2"]
}
```

The second request must not create another reservation.

---

## Invalid request

```http
400 Bad Request
```

Examples:

- empty seat list
- duplicate seats
- unknown/invalid seat format
- missing idempotency key
- invalid JSON
- invalid money value where applicable

---

## Show not found

```http
404 Not Found
```

```json
{
  "error": {
    "code": "SHOW_NOT_FOUND",
    "message": "The requested show does not exist.",
    "correlation_id": "01J..."
  }
}
```

---

# 9. Idempotency Contract

Idempotency is scoped to:

```text
show + authenticated user + idempotency key
```

The database enforces uniqueness for this combination.

## Same key + same request

If the original request succeeded:

```text
Request 1 -> 201
Request 2 -> original reservation
Request 3 -> original reservation
```

The server does not create additional reservations.

The response represents the original reservation.

The reservation ID remains unchanged.

---

## Same key + different request

Example:

First request:

```json
{
  "seats": ["A1"]
}
```

Second request:

```json
{
  "seats": ["A2"]
}
```

using the same idempotency key.

Result:

```http
409 Conflict
```

No second reservation is created.

---

## Idempotency under concurrency

Multiple concurrent requests using:

```text
same user
same show
same idempotency key
same request body
```

must produce exactly one logical reservation.

All successful retries return the same reservation ID.

The idempotency record is persisted in PostgreSQL and therefore survives application restarts.

---

# 10. Cancel Reservation

Cancels an existing confirmed reservation and releases its seats.

## Endpoint

```http
POST /reservations/{reservationId}/cancel
```

### Authentication

Authenticated user required.

The authenticated user must own the reservation.

### Success

```http
200 OK
```

Example:

```json
{
  "id": "d1c3d5e7-5e2a-4a5e-bb20-6a9e2d8f6c11",
  "show_id": "7c7d9f6b-9f6e-4a5c-8d7b-2c0e7d3d7c11",
  "user_id": "user-123",
  "seats": [
    "A1",
    "A2"
  ],
  "amount_paise": 50000,
  "status": "cancelled",
  "cancelled_at": "2026-10-01T11:00:00Z"
}
```

### Cancellation behavior

Cancellation is transactional.

The transaction must:

1. Verify reservation ownership.
2. Verify the reservation is currently confirmed.
3. Release the associated seats.
4. Decrement the user's reserved seat count.
5. Mark the reservation as cancelled.
6. Commit all changes atomically.

If any step fails, the transaction rolls back.

---

# 11. Cancellation Errors

## Reservation not found

```http
404 Not Found
```

```json
{
  "error": {
    "code": "RESERVATION_NOT_FOUND",
    "message": "The requested reservation does not exist.",
    "correlation_id": "01J..."
  }
}
```

## Not the reservation owner

```http
403 Forbidden
```

```json
{
  "error": {
    "code": "RESERVATION_ACCESS_DENIED",
    "message": "You are not allowed to cancel this reservation.",
    "correlation_id": "01J..."
  }
}
```

## Already cancelled

The operation should be safely repeatable.

Recommended behavior:

```http
200 OK
```

returning the existing cancelled reservation.

No additional seat release or user-limit decrement occurs.

---

# 12. Health Endpoints

## Liveness

```http
GET /health/live
```

Purpose:

Determines whether the application process is alive.

The liveness endpoint should not depend on PostgreSQL.

### Success

```http
200 OK
```

```json
{
  "status": "Healthy"
}
```

---

## Readiness

```http
GET /health/ready
```

Purpose:

Determines whether the application can serve traffic.

Readiness checks should include required infrastructure such as PostgreSQL.

### Healthy

```http
200 OK
```

### Dependency unavailable

```http
503 Service Unavailable
```

The readiness endpoint must not report healthy when the required database dependency is unavailable.

---

# 13. Metrics

Prometheus-compatible metrics are exposed through:

```http
GET /metrics
```

The endpoint may be unauthenticated for the take-home deployment.

The implementation should expose at least:

### HTTP metrics

```text
http_requests_total
http_request_duration_seconds
```

### Reservation metrics

```text
reservations_created_total
reservations_cancelled_total
reservation_conflicts_total
reservation_idempotency_replays_total
reservation_idempotency_conflicts_total
```

### Seat metrics

```text
seat_reservation_attempts_total
seat_reservation_conflicts_total
```

### Database metrics

Where available:

```text
database_operation_duration_seconds
database_errors_total
```

Metrics should include useful low-cardinality labels such as:

```text
method
route
status_code
```

Avoid using unbounded values such as:

```text
user_id
reservation_id
idempotency_key
```

as metric labels.

---

# 14. Correlation ID

Every request should have a correlation ID.

Clients may provide:

```http
X-Correlation-ID: <value>
```

If not provided, the API generates one.

The correlation ID should:

- appear in structured logs
- appear in error responses
- be returned in the response header

Example:

```http
X-Correlation-ID: 01JABC123XYZ
```

---

# 15. HTTP Status Code Summary

| Endpoint | Success | Common errors |
|---|---|---|
| `POST /shows` | `201` | `400`, `401`, `403`, `409` |
| `GET /shows/{id}` | `200` | `401`, `404` |
| `POST /shows/{id}/reserve` | `201` | `400`, `401`, `404`, `409` |
| `POST /reservations/{id}/cancel` | `200` | `401`, `403`, `404`, `409` |
| `GET /health/live` | `200` | `503` if process is not healthy |
| `GET /health/ready` | `200` | `503` |
| `GET /metrics` | `200` | `500` only for unexpected infrastructure failures |

Expected concurrency conflicts such as seat contention and user-limit contention must return `409`, not `500`.

---

# 16. API Invariants

The following invariants must always hold.

### INV-01 — No double booking

A confirmed seat belongs to at most one active reservation.

```text
one seat -> maximum one confirmed reservation
```

### INV-02 — Per-user limit

For every show:

```text
user confirmed seats <= per_user_limit
```

Default:

```text
4
```

### INV-03 — Idempotency

For:

```text
show + user + idempotency_key
```

there can be at most one logical reservation.

### INV-04 — Authenticated identity

Reservation ownership comes from the authentication token.

The request body cannot override the authenticated identity.

### INV-05 — Money precision

All money is stored and transmitted as integer paise.

No floating-point money calculations.

### INV-06 — All-or-nothing reservation

A multi-seat reservation either acquires all requested seats or none.

### INV-07 — Seat reconciliation

For every show:

```text
available + held + confirmed = total
```

### INV-08 — Cancellation consistency

Cancelling a confirmed reservation must atomically:

```text
reservation -> cancelled
seats -> available
user reserved count -> decremented
```

### INV-09 — No resurrection

Cancellation must never make a seat available if that seat has already been assigned to another reservation.

All seat state transitions are protected by the database transaction and concurrency controls.

---

# 17. Example End-to-End Flow

## 1. Create show

```http
POST /shows
Authorization: Bearer admin
```

```json
{
  "name": "friday-night",
  "seats": ["A1", "A2", "A3", "A4"],
  "price_paise": 25000
}
```

Result:

```text
Show created
4 available seats
```

---

## 2. User reserves seats

```http
POST /shows/{id}/reserve
Authorization: Bearer user-123
Idempotency-Key: reservation-001
```

```json
{
  "seats": ["A1", "A2"]
}
```

Result:

```text
201 Created
2 confirmed seats
amount = 50000 paise
```

---

## 3. Show state

```http
GET /shows/{id}
```

Result:

```text
total     = 4
available = 2
held      = 0
confirmed = 2
```

Invariant:

```text
2 + 0 + 2 = 4
```

---

## 4. Retry same request

Same:

```text
user
show
idempotency key
request body
```

Result:

```text
same reservation ID
```

No duplicate reservation is created.

---

## 5. Another user attempts A1

```http
POST /shows/{id}/reserve
Authorization: Bearer user-456
Idempotency-Key: reservation-002
```

```json
{
  "seats": ["A1"]
}
```

Result:

```http
409 Conflict
```

The API does not return `500`.

---

## 6. Original user cancels

```http
POST /reservations/{reservationId}/cancel
Authorization: Bearer user-123
```

Result:

```text
reservation -> cancelled
A1 -> available
A2 -> available
```

The user's reserved seat count is decremented atomically.

---

# 18. Concurrency Contract

The API must remain correct when multiple requests operate concurrently.

The primary concurrency boundary is PostgreSQL.

Application-level constructs such as:

```csharp
lock
SemaphoreSlim
ConcurrentDictionary
```

must not be used as the source of correctness for reservation state.

The implementation must rely on:

- database transactions
- conditional updates
- row-level locking where required
- unique constraints
- atomic counter updates
- PostgreSQL as the source of truth

### Hot-seat requirement

For `N` concurrent users attempting to reserve the same seat:

```text
successful reservations = 1
seat conflict responses = N - 1
unexpected 5xx responses = 0
```

### Idempotency storm

For `N` concurrent identical requests using the same:

```text
show
user
idempotency key
request body
```

result:

```text
logical reservations = 1
```

All successful retries reference the same reservation.

---

# 19. API Versioning

The take-home implementation does not require a versioned API.

The initial contract is therefore:

```text
/api
```

If versioning becomes necessary in a production system, the API can evolve to:

```text
/api/v1
/api/v2
```

without changing the underlying reservation invariants.

---

# 20. Design Principles

The API contract follows these principles:

1. **Correctness over availability for reservation writes.**
2. **PostgreSQL is the source of truth.**
3. **Concurrency conflicts are expected business outcomes, not server errors.**
4. **Authentication determines user identity.**
5. **Idempotency is persisted in the database.**
6. **Multi-seat reservations are all-or-nothing.**
7. **Money is represented as integer paise.**
8. **Cancellation is transactional.**
9. **Health and metrics are observable through dedicated endpoints.**
10. **The API must remain correct across multiple application instances.**
11. **Internal implementation details are not exposed through API errors.**
12. **The API contract is testable through automated concurrency and integration tests.**