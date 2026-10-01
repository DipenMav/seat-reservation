# SeatReservation — Database Design

## 1. Database

The system uses:

- PostgreSQL as the primary database
- Entity Framework Core for application persistence
- Npgsql as the PostgreSQL provider

PostgreSQL is the **system of record** for all reservation-related state.

The database is responsible for protecting critical business invariants under concurrency.

---

# 2. Database Design Goals

The database must guarantee:

1. A seat cannot be sold twice.
2. A seat cannot exist twice within the same show.
3. A user cannot exceed the per-show reservation limit.
4. An idempotency key cannot create multiple logical reservations.
5. A multi-seat reservation is atomic.
6. Reservation and seat state remain consistent.
7. Cancellation cannot accidentally release another user's seat.
8. Money is stored as integer paise.
9. Foreign-key relationships remain valid.
10. Database constraints protect important invariants even if application code contains a bug.

---

# 3. Tables

The initial database contains:

```text
Shows
Seats
Reservations
ReservationSeats
ShowUserLimits
IdempotencyKeys
```

Relationship overview:

```text
                     ┌──────────────┐
                     │    Shows     │
                     └──────┬───────┘
                            │
             ┌──────────────┼───────────────┐
             │              │               │
             ▼              ▼               ▼
        ┌─────────┐   ┌──────────────┐  ┌────────────────┐
        │  Seats  │   │ Reservations │  │ ShowUserLimits │
        └────┬────┘   └──────┬───────┘  └────────────────┘
             │               │
             │               ▼
             │       ┌──────────────────┐
             └──────►│ ReservationSeats │
                     └──────────────────┘

                     ┌──────────────────┐
                     │ IdempotencyKeys  │
                     └──────────────────┘
```

---

# 4. Shows

The `shows` table represents an event that contains a fixed set of assigned seats.

## Columns

| Column | Type | Nullable | Description |
|---|---|---:|---|
| id | UUID | No | Primary key |
| name | VARCHAR(200) | No | Show name |
| price_paise | BIGINT | No | Price per seat in paise |
| per_user_limit | INTEGER | No | Maximum seats a user may reserve |
| created_at | TIMESTAMPTZ | No | Creation timestamp |

---

## Constraints

```text
PRIMARY KEY(id)

price_paise >= 0

per_user_limit > 0
```

---

## Example

```text
id:
8b1d...

name:
friday-night

price_paise:
25000

per_user_limit:
4
```

---

# 5. Seats

The `seats` table represents the inventory for a show.

Every seat belongs to exactly one show.

## Columns

| Column | Type | Nullable | Description |
|---|---|---:|---|
| id | UUID | No | Primary key |
| show_id | UUID | No | Parent show |
| seat_number | VARCHAR(50) | No | Assigned seat identifier |
| status | VARCHAR(20) | No | Current inventory state |
| created_at | TIMESTAMPTZ | No | Creation timestamp |
| updated_at | TIMESTAMPTZ | No | Last state change |

---

# 6. Seat Status

Allowed values:

```text
available
held
confirmed
```

The initial state for every newly-created seat is:

```text
available
```

---

# 7. Seat Uniqueness

The following database constraint is mandatory:

```text
UNIQUE(show_id, seat_number)
```

This guarantees that a show cannot contain duplicate seat identifiers.

For example:

```text
Show A
A12
A13
A14
```

is valid.

But:

```text
Show A
A12
A12
```

must be rejected by the database.

The same seat number may exist in another show.

Example:

```text
Show A → A12
Show B → A12
```

is valid.

---

# 8. Seat Indexes

At minimum:

```text
INDEX(show_id)
```

A composite lookup should be efficient for:

```text
show_id + seat_number
```

The unique constraint on:

```text
(show_id, seat_number)
```

also provides an appropriate index for this lookup.

---

# 9. Reservations

The `reservations` table represents the logical booking created by a user.

A reservation may contain one or more seats.

## Columns

| Column | Type | Nullable | Description |
|---|---|---:|---|
| id | UUID | No | Reservation identifier |
| show_id | UUID | No | Show being reserved |
| user_id | VARCHAR(100) | No | Authenticated user |
| amount_paise | BIGINT | No | Total reservation amount |
| status | VARCHAR(20) | No | Reservation state |
| created_at | TIMESTAMPTZ | No | Creation timestamp |
| cancelled_at | TIMESTAMPTZ | Yes | Cancellation timestamp |

---

# 10. Reservation Status

Initial supported states:

```text
confirmed
cancelled
```

A confirmed reservation represents a successful booking.

A cancelled reservation represents a previously-confirmed reservation that has been released.

---

# 11. Reservation Amount

`amount_paise` is stored as:

```text
BIGINT
```

Never:

```text
FLOAT
DOUBLE
```

Example:

```text
₹250.00
=
25000 paise
```

The amount should be calculated using integer arithmetic.

---

# 12. Reservation Ownership

`user_id` represents the authenticated identity that created the reservation.

The value must come from authentication claims.

It must never be trusted from the reservation request body.

The database stores the resulting ownership so that authorization checks can be performed later.

---

# 13. ReservationSeats

A reservation can contain multiple seats.

The `reservation_seats` table represents the many-to-many-style association between reservations and seats.

For this system, a seat should belong to only one active reservation at a time.

## Columns

| Column | Type | Nullable | Description |
|---|---|---:|---|
| reservation_id | UUID | No | Reservation |
| seat_id | UUID | No | Seat |

Primary key:

```text
PRIMARY KEY(reservation_id, seat_id)
```

Foreign keys:

```text
reservation_id → reservations.id

seat_id → seats.id
```

---

# 14. Reservation/Seat Relationship

Example:

```text
Reservation R1
    │
    ├── A12
    ├── A13
    └── A14
```

Database representation:

```text
reservation_id    seat_id
---------------   --------
R1                A12
R1                A13
R1                A14
```

---

# 15. Multi-Seat Reservation

The application uses an **all-or-nothing** model.

For:

```text
A12
A13
A14
```

either all requested seats are successfully acquired or none are.

The database transaction must contain the complete operation.

There must never be a committed state where only part of a requested reservation exists.

---

# 16. ShowUserLimits

The `show_user_limits` table coordinates the reservation count for a user within a show.

## Columns

| Column | Type | Nullable | Description |
|---|---|---:|---|
| show_id | UUID | No | Show |
| user_id | VARCHAR(100) | No | Authenticated user |
| reserved_count | INTEGER | No | Current active/confirmed seat count |

Primary key:

```text
PRIMARY KEY(show_id, user_id)
```

---

# 17. Why ShowUserLimits Exists

Do not rely only on:

```sql
SELECT COUNT(*)
FROM reservations ...
```

followed by:

```text
if count < limit
    create reservation
```

Under concurrency:

```text
Current count = 3
Limit = 4

Request A → sees 3
Request B → sees 3
Request C → sees 3
```

All three could incorrectly decide that capacity remains.

`ShowUserLimits` provides a database row that can be protected/updated atomically.

---

# 18. Per-User Limit Strategy

The final reservation transaction will protect the `(show_id, user_id)` row.

Conceptually:

```text
ShowUserLimits
---------------------------
Show       User       Count
S1         U1         3
```

If U1 requests one seat:

```text
3 + 1 <= 4
```

the count can become:

```text
4
```

If U1 requests another seat:

```text
4 + 1 <= 4
```

is false.

The request must be rejected.

The exact PostgreSQL locking/conditional-update mechanism is documented separately in:

```text
docs/CONCURRENCY.md
```

---

# 19. IdempotencyKeys

The `idempotency_keys` table prevents duplicate logical reservations.

## Columns

| Column | Type | Nullable | Description |
|---|---|---:|---|
| id | UUID | No | Primary key |
| show_id | UUID | No | Show |
| user_id | VARCHAR(100) | No | Authenticated user |
| idempotency_key | VARCHAR(200) | No | Client-provided key |
| request_hash | VARCHAR(64) | No | Hash of normalized request |
| reservation_id | UUID | No | Resulting reservation |
| created_at | TIMESTAMPTZ | No | Creation timestamp |

---

# 20. Idempotency Uniqueness

The following constraint is mandatory:

```text
UNIQUE(show_id, user_id, idempotency_key)
```

This means two concurrent requests cannot create two idempotency records for the same logical request.

---

# 21. Idempotency Request Hash

The system stores a deterministic hash of the reservation request.

Example:

```text
Idempotency Key:
ABC123

Normalized request:
A12,A13

SHA-256:
<hash>
```

The exact canonicalization strategy must be deterministic.

For example, seat identifiers should be:

1. normalized
2. deduplicated
3. sorted
4. serialized consistently

This prevents logically identical requests from producing different hashes because of array ordering.

Example:

```json
["A13", "A12"]
```

and:

```json
["A12", "A13"]
```

should represent the same logical request after normalization.

---

# 22. Idempotency Scenarios

## Scenario A — First Request

```text
Key = ABC
Seats = A12
```

No existing key.

Reservation is created:

```text
R1
```

Idempotency record:

```text
ABC → R1
```

---

## Scenario B — Retry

```text
Key = ABC
Seats = A12
```

Existing record found.

Request hash matches.

Return:

```text
R1
```

No new reservation.

---

## Scenario C — Different Body

```text
Key = ABC
Seats = A13
```

Existing record found.

Request hash differs.

Return:

```text
409 Conflict
```

No reservation state changes.

---

# 23. Idempotency and Transactions

Idempotency handling must be part of the reservation transaction.

The implementation must correctly handle two identical requests arriving simultaneously.

Example:

```text
Request A ─────┐
               ├── same key
Request B ─────┘
```

The database must ensure that only one logical reservation is created.

The losing request must resolve to the same reservation/result rather than creating another booking.

The exact race-handling strategy is documented in:

```text
docs/CONCURRENCY.md
```

---

# 24. Foreign Keys

Required relationships:

```text
seats.show_id
    → shows.id

reservations.show_id
    → shows.id

reservation_seats.reservation_id
    → reservations.id

reservation_seats.seat_id
    → seats.id

show_user_limits.show_id
    → shows.id

idempotency_keys.show_id
    → shows.id

idempotency_keys.reservation_id
    → reservations.id
```

Foreign keys should be enabled and enforced.

---

# 25. Delete Behavior

Shows should not be casually deleted after reservations exist.

The initial implementation should avoid exposing a delete-show endpoint.

This prevents unnecessary cascading/deletion complexity.

Reservations and seat history should not disappear accidentally.

---

# 26. Cancellation and Seat State

When a reservation is cancelled:

```text
Reservation:
confirmed → cancelled
```

Associated seats become:

```text
confirmed → available
```

and the user's active reservation count decreases.

These changes must happen in the same transaction.

Example:

```text
BEGIN

verify reservation ownership

verify reservation is confirmed

mark reservation cancelled

release associated seats

decrement ShowUserLimits.reserved_count

COMMIT
```

If anything fails:

```text
ROLLBACK
```

---

# 27. Cancellation Race Safety

Cancellation must be designed so it cannot resurrect a seat incorrectly.

Example:

```text
User A owns A12
```

User A cancels while another request attempts to reserve A12.

The database transaction must ensure that the final state is one of:

```text
A12 → available
```

or:

```text
A12 → confirmed for User B
```

but never an inconsistent state where:

```text
A12 → available
```

after User B has already acquired it.

The exact locking order will be documented in:

```text
docs/CONCURRENCY.md
```

---

# 28. Seat State and Reservation State

Seat inventory state is stored on `Seats`.

Reservation lifecycle is stored on `Reservations`.

This separation allows us to answer:

```text
What is the current state of A12?
```

and:

```text
Who reserved A12?
```

without making reservation history the sole source of current inventory state.

---

# 29. Reconciliation

The source of truth for current seat counts is the `Seats` table.

For a show:

```text
COUNT(status = 'available')
+
COUNT(status = 'held')
+
COUNT(status = 'confirmed')
=
COUNT(all seats)
```

The API should calculate or verify these counts from the current seat records.

---

# 30. Recommended Indexes

Initial indexes:

### Seats

```text
UNIQUE(show_id, seat_number)
```

This supports:

```text
show + seat lookup
```

and prevents duplicates.

---

### Reservations

```text
INDEX(show_id, user_id)
```

Useful for:

- per-user reservation queries
- reservation history
- authorization

---

### ReservationSeats

```text
PRIMARY KEY(reservation_id, seat_id)
```

Additional indexing on:

```text
seat_id
```

may be useful for reverse lookup:

```text
Which reservation contains this seat?
```

---

### IdempotencyKeys

```text
UNIQUE(show_id, user_id, idempotency_key)
```

This supports lookup and uniqueness.

---

### ShowUserLimits

```text
PRIMARY KEY(show_id, user_id)
```

This provides efficient access to the concurrency coordination row.

---

# 31. Database Constraints Summary

The following constraints are considered critical:

```text
shows
-----
PRIMARY KEY(id)
CHECK(price_paise >= 0)
CHECK(per_user_limit > 0)


seats
-----
PRIMARY KEY(id)
FOREIGN KEY(show_id)
UNIQUE(show_id, seat_number)
CHECK(status IN ('available', 'held', 'confirmed'))


reservations
------------
PRIMARY KEY(id)
FOREIGN KEY(show_id)
CHECK(amount_paise >= 0)
CHECK(status IN ('confirmed', 'cancelled'))


reservation_seats
-----------------
PRIMARY KEY(reservation_id, seat_id)
FOREIGN KEY(reservation_id)
FOREIGN KEY(seat_id)


show_user_limits
----------------
PRIMARY KEY(show_id, user_id)
CHECK(reserved_count >= 0)


idempotency_keys
----------------
PRIMARY KEY(id)
UNIQUE(show_id, user_id, idempotency_key)
FOREIGN KEY(show_id)
FOREIGN KEY(reservation_id)
```

---

# 32. UUID Strategy

Application-generated UUIDs will be used for primary identifiers.

The application can generate IDs before beginning the transaction where appropriate.

Primary IDs:

```text
Show.id
Seat.id
Reservation.id
IdempotencyKey.id
```

The exact UUID generation implementation will be decided during EF Core implementation.

---

# 33. Timestamps

Use PostgreSQL:

```text
TIMESTAMPTZ
```

for timestamps.

Store timestamps in UTC.

Application code should use:

```text
DateTimeOffset
```

rather than local machine time for persisted timestamps.

---

# 34. Migration Strategy

Database schema changes must be managed through Entity Framework Core migrations.

Example:

```bash
dotnet ef migrations add InitialCreate
```

Then:

```bash
dotnet ef database update
```

Production deployments should apply migrations in a controlled deployment step.

Do not manually modify production tables without corresponding migration history.

---

# 35. Seed Data

The application should not require production seed data.

Development/test data may be created through:

- test fixtures
- integration-test setup
- a development seed mechanism

Production correctness must not depend on seeded users or reservations.

---

# 36. Transaction Isolation

The default PostgreSQL isolation level should be used initially unless testing demonstrates a specific requirement for a stronger level.

The design should prefer targeted row-level locking and atomic conditional operations rather than globally increasing transaction isolation.

The final isolation and locking decisions will be documented in:

```text
docs/CONCURRENCY.md
```

---

# 37. No Application-Level Reservation Lock

Do not implement:

```csharp
lock (seat)
{
    // reserve
}
```

as the correctness mechanism.

This only protects threads inside one application process.

The deployed service may have multiple instances.

Database-level concurrency control is required.

---

# 38. No In-Memory Reservation State

Do not maintain authoritative state such as:

```csharp
Dictionary<string, SeatStatus>
```

or:

```csharp
ConcurrentDictionary<string, Reservation>
```

The application must be able to restart without losing reservation state.

All authoritative state must survive application restarts because it is stored in PostgreSQL.

---

# 39. Data Integrity Principle

The application layer validates business rules.

The database enforces persistence-level invariants.

Therefore:

```text
Application validation
        +
Database constraints
        +
Database transactions
        +
Database concurrency control
```

together provide the reservation correctness model.

---

# 40. Initial Schema Summary

The final initial schema is:

```text
┌───────────────────────┐
│        shows          │
├───────────────────────┤
│ id PK                 │
│ name                  │
│ price_paise           │
│ per_user_limit        │
│ created_at            │
└───────────┬───────────┘
            │
     ┌──────┼───────────────┐
     │      │               │
     ▼      ▼               ▼
┌────────┐ ┌──────────────┐ ┌──────────────────┐
│ seats  │ │ reservations │ │ show_user_limits │
├────────┤ ├──────────────┤ ├──────────────────┤
│ id PK  │ │ id PK        │ │ show_id PK       │
│show_id │ │ show_id      │ │ user_id PK       │
│number  │ │ user_id      │ │ reserved_count   │
│status  │ │ amount_paise │ └──────────────────┘
└────┬───┘ │ status       │
     │     └──────┬───────┘
     │            │
     │            ▼
     │     ┌──────────────────┐
     └────►│ reservation_seats│
           ├──────────────────┤
           │ reservation_id   │
           │ seat_id          │
           └──────────────────┘

┌──────────────────────┐
│   idempotency_keys   │
├──────────────────────┤
│ id PK                │
│ show_id              │
│ user_id              │
│ idempotency_key      │
│ request_hash         │
│ reservation_id       │
│ created_at           │
└──────────────────────┘
```

---

# 41. Database Design Principle

The most important database principle is:

> **Critical reservation invariants must be protected by PostgreSQL, not only by application code.**

The application can receive thousands of concurrent requests.

PostgreSQL must be capable of determining safely:

```text
Who gets the seat?
```

and:

```text
Is this user still within the reservation limit?
```

and:

```text
Is this idempotency key already associated with a reservation?
```

without relying on request timing or process-local state.

---

# 42. Next Design Document

Before implementing the EF Core model, the concurrency strategy must be finalized in:

```text
docs/CONCURRENCY.md
```

That document will define the exact database operations and transaction order for:

1. Hot-seat reservation
2. Multi-seat reservation
3. Per-user limit
4. Idempotency races
5. Cancellation races
6. Deadlock prevention
7. Rollback behavior
8. PostgreSQL locking/conditional updates

No concurrency-sensitive implementation should be considered final until those decisions are documented.