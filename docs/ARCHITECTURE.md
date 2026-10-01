# SeatReservation — Architecture

## 1. Architecture Goal

The primary goal of this system is to provide a correct seat reservation service under high concurrency.

The architecture is optimized for:

1. Correctness
2. Concurrency safety
3. Transactional consistency
4. Idempotency
5. Observability
6. Simplicity
7. Testability

The system must remain correct when thousands of users attempt to reserve seats simultaneously.

---

# 2. High-Level Architecture

The system is a single ASP.NET Core application backed by PostgreSQL.

```text
                    ┌──────────────────────┐
                    │      HTTP Clients     │
                    │                      │
                    │ Web / Mobile / Tests │
                    └───────────┬──────────┘
                                │
                                ▼
                    ┌──────────────────────┐
                    │    ASP.NET Core API  │
                    │                      │
                    │ Authentication       │
                    │ Validation           │
                    │ Business Logic       │
                    │ Transactions         │
                    │ Metrics              │
                    │ Structured Logging   │
                    └───────────┬──────────┘
                                │
                                │ SQL / EF Core
                                ▼
                    ┌──────────────────────┐
                    │      PostgreSQL      │
                    │                      │
                    │ Shows                │
                    │ Seats                │
                    │ Reservations         │
                    │ ReservationSeats     │
                    │ IdempotencyKeys      │
                    │ ShowUserLimits       │
                    └──────────────────────┘
```

PostgreSQL is the system of record.

No external cache or message broker is required for the core reservation flow.

---

# 3. Technology Stack

## Application

- C#
- .NET 10
- ASP.NET Core

## Persistence

- PostgreSQL
- Entity Framework Core
- Npgsql

## Testing

- xUnit
- ASP.NET Core integration testing
- PostgreSQL integration tests
- Concurrent HTTP tests

## Observability

- Structured logging
- Prometheus-compatible metrics
- ASP.NET Core health checks

## Deployment

- Public cloud deployment
- Dockerfile provided for reproducible deployment

Docker is not required for local development.

---

# 4. Architectural Style

The application uses a pragmatic **Vertical Slice Architecture**.

Features are organized around business capabilities.

```text
src/SeatReservation.Api/

    Features/

        Shows/
            CreateShow/
            GetShow/

        Reservations/
            Reserve/
            Cancel/

    Infrastructure/
        Persistence/
        Authentication/
        Observability/

    Common/

    Program.cs
```

The architecture intentionally avoids excessive layering.

We do not create separate projects for:

- Domain
- Application
- Infrastructure
- Contracts
- Repository
- Service

unless there is a demonstrated need.

The application should remain easy to understand and modify during the interview.

---

# 5. Core Components

## 5.1 HTTP API

Responsible for:

- HTTP routing
- authentication
- request validation
- response formatting
- HTTP status codes
- correlation/request IDs

The API layer must not make concurrency guarantees using in-memory state.

---

## 5.2 Reservation Application Logic

Responsible for:

- validating reservation requests
- calculating reservation amount
- enforcing business rules
- coordinating database transactions
- translating database outcomes into domain results

The reservation logic is the most concurrency-sensitive component.

---

## 5.3 PostgreSQL

PostgreSQL is responsible for maintaining persistent state and protecting critical invariants.

The database is the concurrency boundary.

The application must assume that multiple API instances can execute reservation requests simultaneously.

---

## 5.4 Observability

Responsible for:

- structured logs
- metrics
- health checks
- request correlation

Observability must not alter reservation correctness.

---

# 6. System of Record

PostgreSQL is the authoritative source of truth for:

- show state
- seat state
- reservation state
- reservation ownership
- idempotency
- per-user reservation limits

The following must NOT be authoritative:

```text
ConcurrentDictionary
static variables
local memory cache
process-level locks
```

These mechanisms may be used for non-critical optimizations later, but never for correctness.

---

# 7. Reservation Request Flow

The reservation flow is conceptually:

```text
HTTP Request
     │
     ▼
Authentication
     │
     ▼
Request Validation
     │
     ▼
Idempotency Check
     │
     ▼
Begin DB Transaction
     │
     ▼
Enforce User Limit
     │
     ▼
Acquire Requested Seats
     │
     ▼
Create Reservation
     │
     ▼
Create ReservationSeat Records
     │
     ▼
Persist Idempotency Result
     │
     ▼
Commit
     │
     ▼
Return 201
```

Any failure during the transactional portion must roll back the complete operation.

---

# 8. Database Transaction Boundary

The reservation operation should be executed inside one PostgreSQL transaction.

Conceptually:

```text
BEGIN

    idempotency handling

    user-limit protection

    seat acquisition

    reservation creation

    reservation-seat creation

    idempotency result persistence

COMMIT
```

If any operation fails:

```text
ROLLBACK
```

This prevents partial state such as:

```text
Seat confirmed
but reservation missing
```

or:

```text
Reservation created
but seat remains available
```

---

# 9. Concurrency Strategy

Concurrency correctness is primarily enforced by PostgreSQL.

The application must not rely on:

```text
SELECT → check in C# → UPDATE
```

for seat acquisition.

The final implementation must use a database-level atomic mechanism.

For example:

```sql
UPDATE seats
SET status = 'confirmed'
WHERE show_id = @showId
  AND seat_number = @seatNumber
  AND status = 'available';
```

The affected row count determines whether the seat was acquired.

```text
1 row affected
    ↓
seat acquired

0 rows affected
    ↓
seat unavailable
```

The exact final mechanism will be documented in:

```text
.ai/CONCURRENCY.md
```

---

# 10. Hot Seat Behavior

Suppose:

```text
500 users
    ↓
A12
```

The expected result is:

```text
Request 1
    ↓
A12 acquired

Requests 2..500
    ↓
A12 no longer available
    ↓
409 Conflict
```

The identity of the winning request is not important.

The following is mandatory:

```text
confirmed(A12) <= 1
```

No request should produce a 500 simply because another request won the seat.

---

# 11. Multi-Seat Reservation

Multi-seat reservations use an **all-or-nothing** model.

Example:

```text
Request:
A12
A13
A14
```

If all are available:

```text
Reservation succeeds
```

If any one is unavailable:

```text
Reservation fails
No requested seat is reserved
```

The operation must occur within the same transaction.

---

# 12. Deterministic Seat Ordering

Requested seats must be:

1. normalized
2. deduplicated
3. deterministically ordered

Example:

```text
Input:

A14
A12
A13

Normalized:

A12
A13
A14
```

This ensures competing multi-seat transactions attempt to acquire resources in a consistent order.

This reduces deadlock risk.

---

# 13. Per-User Limit Architecture

Each show has a:

```text
per_user_limit
```

with default:

```text
4
```

A dedicated `ShowUserLimits` row is used to coordinate concurrent reservations for the same user and show.

Conceptually:

```text
ShowId
UserId
ReservedCount
```

The count must be protected using a database-level atomic operation or row lock inside the reservation transaction.

The unsafe pattern:

```text
SELECT COUNT(...)
    ↓
if count < limit
    ↓
INSERT
```

must not be used as the sole concurrency mechanism.

Example:

```text
User U1
Current count = 3
Limit = 4

Request A → +1
Request B → +1
Request C → +1
```

Only one additional seat may be accepted.

The final mechanism will be specified in:

```text
.ai/CONCURRENCY.md
```

---

# 14. Idempotency Architecture

Idempotency is persisted in PostgreSQL.

The conceptual key is:

```text
(show_id, user_id, idempotency_key)
```

with a unique database constraint.

Each idempotency record stores enough information to determine whether a retry contains the same request.

Conceptually:

```text
show_id
user_id
idempotency_key
request_hash
reservation_id
```

---

# 15. Idempotency Flow

## First Request

```text
key = ABC
seats = A12
```

Flow:

```text
No existing key
     ↓
Process reservation
     ↓
Create reservation R1
     ↓
Store ABC → R1
     ↓
Return R1
```

---

## Same Request Retry

```text
key = ABC
seats = A12
```

Flow:

```text
ABC exists
     ↓
Hash matches
     ↓
Return R1
```

No additional seat operation occurs.

---

## Different Request With Same Key

```text
key = ABC
seats = A13
```

Flow:

```text
ABC exists
     ↓
Hash differs
     ↓
409 Conflict
```

The existing reservation must not be modified.

---

# 16. Authentication Architecture

Authentication provides the authoritative user identity.

Conceptually:

```text
Authorization: Bearer <token>
                │
                ▼
        Authentication Handler
                │
                ▼
           User Claims
                │
                ▼
             user_id
```

Reservation ownership is derived from the authenticated identity.

The request body cannot override the authenticated user.

---

# 17. Cancellation Architecture

Cancellation is performed inside a database transaction.

Conceptually:

```text
POST /reservations/{id}/cancel
          │
          ▼
Authenticate user
          │
          ▼
Load reservation
          │
          ▼
Verify owner
          │
          ▼
Transition reservation
          │
          ▼
Release associated seats
          │
          ▼
Update user reservation count
          │
          ▼
COMMIT
```

If the caller does not own the reservation:

```text
403 Forbidden
```

The cancellation operation must not release another user's reservation.

---

# 18. Seat State Model

A seat can have:

```text
available
held
confirmed
```

For the initial implementation, immediate confirmation may be used instead of a temporary hold if that provides a simpler and more reliable implementation for the assignment.

If temporary holds are introduced, expiry behavior must be explicitly documented and tested.

The important invariant remains:

```text
available + held + confirmed = total
```

---

# 19. Reconciliation

The API should derive or verify show counts from the authoritative seat state.

For a show:

```text
total = available + held + confirmed
```

The reconciliation invariant must be testable.

The system should not maintain independent counters that can drift from actual seat records unless those counters are transactionally maintained and verified.

---

# 20. API Boundary

The API exposes:

```text
POST /shows
POST /shows/{showId}/reserve
POST /reservations/{reservationId}/cancel
GET  /shows/{showId}

GET /health/live
GET /health/ready
GET /metrics
```

The API is intentionally JSON-only.

No UI is required.

---

# 21. Error Model

Business conflicts are represented as normal HTTP responses.

Examples:

```text
Seat unavailable
    → 409

Per-user limit exceeded
    → 409

Idempotency conflict
    → 409

Unauthorized
    → 401

Not reservation owner
    → 403

Show not found
    → 404
```

Unexpected infrastructure or application failures may produce 5xx responses.

Expected contention must not generate 5xx responses.

---

# 22. Observability Architecture

Observability consists of:

```text
                    ┌───────────────┐
                    │ ASP.NET Core  │
                    └───────┬───────┘
                            │
               ┌────────────┼────────────┐
               ▼            ▼            ▼
           Structured     Metrics      Health
             Logs        /metrics     Endpoints
```

Metrics should include at least:

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

---

# 23. Request Correlation

Each HTTP request should have a correlation/request ID.

The ID should be included in structured logs.

Where appropriate it may also be returned in a response header.

The purpose is to trace:

```text
HTTP request
    ↓
reservation operation
    ↓
database operation
    ↓
response
```

---

# 24. Failure Philosophy

The service should fail safely.

Examples:

### Database unavailable

```text
Readiness → 503
Reservation → fail without partial state
```

### Seat already taken

```text
409
```

not:

```text
500
```

### Transaction failure

```text
Rollback
```

### Application restart

Persistent reservation/idempotency state remains in PostgreSQL.

### Multiple API instances

Correctness remains protected by PostgreSQL.

---

# 25. Consistency vs Availability

For reservation state, **consistency takes priority over availability**.

The system must not accept a reservation if it cannot reliably determine and persist:

- seat ownership
- reservation ownership
- idempotency state
- per-user limit

During a database outage, the service should fail closed for reservation writes rather than accepting uncertain reservations.

This is preferable to risking double booking or inconsistent reservation state.

---

# 26. Scaling Model

The initial system is intentionally a single service with a single PostgreSQL database.

It can conceptually scale horizontally:

```text
                 Load Balancer
                      │
          ┌───────────┼───────────┐
          ▼           ▼           ▼
       API #1       API #2      API #3
          │           │           │
          └───────────┼───────────┘
                      ▼
                 PostgreSQL
```

No API instance should hold exclusive reservation state in memory.

This means adding API instances does not change correctness behavior.

---

# 27. Why No Distributed Lock

A distributed lock is not required for the initial design.

PostgreSQL already provides:

- transactions
- row-level locking
- conditional updates
- unique constraints
- isolation

The database is already the authority for the state being protected.

Adding Redis or another distributed locking system would introduce additional failure modes without being necessary for this assignment.

---

# 28. Why No Message Queue

Reservation confirmation is a synchronous correctness-critical operation.

The caller needs an immediate authoritative answer:

```text
201
```

or:

```text
409
```

A message queue is therefore unnecessary for the core reservation decision.

Asynchronous processing may be considered later for non-critical work such as:

- notifications
- analytics
- audit processing

but it is not required for the core assignment.

---

# 29. Performance Strategy

Performance optimizations must not weaken correctness.

Initial optimization priorities:

1. Correct database indexes
2. Efficient SQL
3. Short database transactions
4. Minimal network round trips
5. Appropriate connection pooling
6. Avoid unnecessary entity loading
7. Efficient batch operations where appropriate

Do not optimize prematurely with additional infrastructure.

---

# 30. Transaction Length

Transactions should remain as short as practical.

Do not perform:

- external HTTP calls
- slow file operations
- long-running calculations
- unnecessary logging I/O

inside the critical reservation transaction.

The transaction should primarily contain the database state changes required to make the reservation decision atomic.

---

# 31. Security Boundaries

The system must protect:

- authentication identity
- reservation ownership
- database credentials
- application secrets

Never trust client-provided ownership information.

Never expose internal database errors directly to API consumers.

Never log authentication tokens.

---

# 32. Deployment Architecture

The production deployment consists conceptually of:

```text
Internet
   │
   ▼
Public HTTPS URL
   │
   ▼
ASP.NET Core API
   │
   ▼
PostgreSQL
```

The repository includes a Dockerfile for reproducible deployment.

The application itself should remain portable and should not depend on Docker-specific behavior.

---

# 33. Testing Architecture

Testing is divided into several levels.

```text
Unit Tests
    ↓
Integration Tests
    ↓
Concurrency Tests
    ↓
HTTP Burst Tests
    ↓
Live Deployment Tests
```

## Unit Tests

Validate isolated business behavior.

## Integration Tests

Validate:

- PostgreSQL
- EF Core
- transactions
- constraints
- API behavior

## Concurrency Tests

Validate:

- hot-seat contention
- per-user limits
- idempotency races
- multi-seat contention

## Burst Tests

Validate the actual deployed HTTP service.

---

# 34. AI-Assisted SDLC

AI tools are part of the development workflow.

AI may assist with:

```text
Requirements
Architecture alternatives
Database review
Concurrency analysis
Implementation
Testing
Code review
Documentation
Load testing
Debugging
```

However, architecture decisions remain engineering decisions.

AI-generated code must be validated against:

- requirements
- database behavior
- concurrency behavior
- automated tests
- live burst tests

---

# 35. Architecture Decision Records

Important architectural decisions should be documented.

At minimum:

```text
ADR-001 — PostgreSQL as system of record
ADR-002 — Database-level concurrency control
ADR-003 — All-or-nothing multi-seat reservation
ADR-004 — Database-backed idempotency
ADR-005 — Database-backed per-user limit
ADR-006 — Consistency over availability for reservation writes
```

These may initially remain sections in this document and can be extracted into individual ADR files later if useful.

---

# 36. Current Architecture Decisions

The following decisions are currently accepted:

| Decision | Choice |
|---|---|
| Application | ASP.NET Core |
| Language | C# |
| Database | PostgreSQL |
| ORM | EF Core |
| Architecture | Vertical Slice |
| System of record | PostgreSQL |
| Multi-seat behavior | All-or-nothing |
| Money | Integer paise |
| Idempotency | Database-backed |
| User limit | Database-backed |
| Concurrency boundary | PostgreSQL |
| Reservation consistency | Strong |
| Queue | Not required |
| Distributed lock | Not required |
| Redis | Not required |
| Local Docker requirement | No |
| Dockerfile | Yes |
| Observability | Logs + Prometheus + Health |
| Load testing | Dedicated burst tool |

---

# 37. Open Design Decisions

The following details must be finalized before implementation:

1. Exact seat acquisition SQL/EF Core strategy.
2. Exact PostgreSQL transaction isolation level.
3. Exact locking strategy for `ShowUserLimits`.
4. Exact idempotency race-handling strategy.
5. Whether reservation IDs are generated by the application or database.
6. Exact cancellation state transition.
7. Whether `held` state is implemented or immediate confirmation is used.
8. Exact metrics library.
9. Exact authentication handler.

These decisions must be documented in:

```text
.ai/DATABASE.md
.ai/CONCURRENCY.md
.ai/API.md
```

before final implementation.

---

# 38. Core Architectural Principle

The most important principle of this system is:

> **The database makes the atomic reservation decision.**

The application coordinates the operation.

PostgreSQL guarantees the critical invariants.

The API exposes the result.

This separation ensures that correctness does not depend on:

- request timing
- thread scheduling
- application instance count
- process-local memory
- client behavior

The design must remain correct even when thousands of requests arrive concurrently.