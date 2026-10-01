# SeatReservation — Concurrency Strategy

## 1. Purpose

This document defines how SeatReservation maintains correctness when many requests execute concurrently.

The primary concurrency requirement is:

> **The database must make the atomic decision about who owns a seat.**

Application-level checks alone are not sufficient.

The system must remain correct when:

- thousands of users reserve simultaneously
- hundreds of users target the same seat
- one user sends many concurrent requests
- multiple requests use the same idempotency key
- cancellation races with a new reservation
- multiple API instances process requests simultaneously

---

# 2. Concurrency Goals

The system must guarantee:

```text
1. No double booking
2. Per-user reservation limit is never exceeded
3. Idempotency creates only one logical reservation
4. Multi-seat reservations are atomic
5. Cancellation is race-safe
6. Domain conflicts return 409, not 500
7. Reconciliation invariant remains valid
8. Correctness does not depend on application instance count
```

---

# 3. Core Principle

The application must not use:

```text
READ
  ↓
CHECK IN C#
  ↓
WRITE
```

as the concurrency mechanism.

Unsafe example:

```text
SELECT status
FROM seats
WHERE show_id = @showId
AND seat_number = @seat;
```

Then:

```csharp
if (seat.Status == Available)
{
    seat.Status = Confirmed;
    await db.SaveChangesAsync();
}
```

Two concurrent requests can both observe:

```text
available
```

before either writes.

This can result in double booking.

---

# 4. Database as Concurrency Boundary

PostgreSQL is the concurrency boundary.

The application coordinates the operation, but PostgreSQL decides whether a seat can actually be acquired.

This allows multiple application instances:

```text
                     Load Balancer
                          │
              ┌───────────┼───────────┐
              ▼           ▼           ▼
           API #1       API #2       API #3
              │           │           │
              └───────────┼───────────┘
                          ▼
                     PostgreSQL
```

All instances operate against the same authoritative state.

---

# 5. Reservation Transaction

A reservation request uses one database transaction for the state changes required to create the reservation.

Conceptually:

```text
BEGIN
  │
  ├── Normalize request
  │
  ├── Handle idempotency
  │
  ├── Protect per-user limit
  │
  ├── Acquire requested seats
  │
  ├── Create reservation
  │
  ├── Create reservation-seat records
  │
  ├── Persist idempotency result
  │
COMMIT
```

If any required operation fails:

```text
ROLLBACK
```

No partial reservation may be committed.

---

# 6. Transaction Boundary

The critical reservation state changes must occur inside the same transaction.

The transaction must cover:

```text
idempotency
+
per-user limit
+
seat acquisition
+
reservation creation
+
reservation-seat creation
```

The transaction must not contain:

- external HTTP requests
- network calls
- long-running operations
- notifications
- email delivery
- unnecessary I/O

The goal is to keep the critical section short.

---

# 7. Hot Seat Reservation

Consider:

```text
500 users
    ↓
A12
```

The system must produce:

```text
1 × successful reservation
499 × 409 SeatTaken
0 × unexpected 5xx
```

The winning request is determined by PostgreSQL transaction scheduling.

The application must not attempt to select a winner itself.

---

# 8. Atomic Seat Acquisition

Seat acquisition should use an atomic conditional database operation.

Conceptually:

```sql
UPDATE seats
SET status = 'confirmed',
    updated_at = NOW()
WHERE show_id = @showId
  AND seat_number = @seatNumber
  AND status = 'available';
```

The application checks the affected row count.

### One row affected

```text
Seat successfully acquired.
```

### Zero rows affected

```text
Seat was not available.
```

This must be translated to:

```text
409 Conflict
```

not:

```text
500 Internal Server Error
```

---

# 9. Why Conditional UPDATE Is Safe

Suppose two transactions execute:

```text
Request A:
UPDATE A12 WHERE status = available

Request B:
UPDATE A12 WHERE status = available
```

PostgreSQL serializes the conflicting update.

One transaction successfully changes:

```text
available → confirmed
```

The other transaction evaluates the condition against the current row state and affects zero rows.

Therefore:

```text
confirmed(A12) <= 1
```

The database becomes the decision point.

---

# 10. Multi-Seat Reservation

Multi-seat reservations use an all-or-nothing model.

Example:

```text
A12
A13
A14
```

All seats must be acquired successfully.

If:

```text
A12 → available
A13 → available
A14 → taken
```

the transaction must roll back the previous successful acquisitions.

Final result:

```text
A12 → available
A13 → available
A14 → taken
```

No partial reservation is committed.

---

# 11. Deterministic Seat Ordering

Before acquiring seats, requested seat identifiers must be:

1. normalized
2. deduplicated
3. sorted deterministically

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

Every multi-seat reservation attempts seat acquisition in the same order.

This reduces the possibility of deadlocks.

---

# 12. Why Deterministic Ordering Matters

Consider two transactions.

Without deterministic ordering:

```text
Transaction A:
lock A12
lock A13

Transaction B:
lock A13
lock A12
```

Possible result:

```text
A waits for B
B waits for A
```

This is a deadlock.

With deterministic ordering:

```text
Transaction A:
A12 → A13

Transaction B:
A12 → A13
```

Both transactions request resources in the same order.

One waits for the other rather than forming a circular wait.

---

# 13. Per-User Limit

The default limit is:

```text
4 seats per user per show
```

The system must enforce this limit under concurrency.

Unsafe implementation:

```sql
SELECT COUNT(*)
FROM reservations
WHERE show_id = @showId
AND user_id = @userId;
```

followed by:

```text
if count < 4
    create reservation
```

This is unsafe because multiple requests can observe the same count.

---

# 14. ShowUserLimits Row

Use:

```text
show_user_limits
```

as a coordination row.

Conceptually:

```text
show_id
user_id
reserved_count
```

Primary key:

```text
(show_id, user_id)
```

This provides a single database row that can serialize concurrent reservation capacity decisions for a user/show pair.

---

# 15. Atomic User Limit Update

The preferred approach is an atomic conditional update.

Conceptually:

```sql
UPDATE show_user_limits
SET reserved_count = reserved_count + @requestedSeatCount
WHERE show_id = @showId
  AND user_id = @userId
  AND reserved_count + @requestedSeatCount <= @perUserLimit;
```

Then:

```text
1 row affected
    ↓
User has successfully acquired reservation capacity.
```

or:

```text
0 rows affected
    ↓
Per-user limit exceeded.
```

The second case becomes:

```text
409 Conflict
```

---

# 16. Creating ShowUserLimits

A `ShowUserLimits` row must exist before it can be atomically updated.

The preferred design is to create the row when it is first needed or when the show/user relationship is initialized.

If multiple concurrent requests can attempt to create the same row, creation must itself be protected by a database uniqueness constraint:

```text
PRIMARY KEY(show_id, user_id)
```

The implementation must handle a concurrent insert safely.

---

# 17. Reservation Order

The reservation transaction should establish the per-user capacity decision before acquiring seats.

Conceptually:

```text
BEGIN
  │
  ├── Idempotency handling
  │
  ├── User limit
  │
  ├── Seats
  │
  ├── Reservation
  │
  ├── Idempotency result
  │
COMMIT
```

This gives the system a deterministic high-level order.

The exact SQL/EF Core implementation must preserve the transaction semantics.

---

# 18. Idempotency Concurrency

Idempotency must be safe when multiple requests with the same key arrive simultaneously.

Example:

```text
Request A ───────┐
                 │
Request B ───────┼── key = ABC
                 │
Request C ───────┘
```

All requests contain:

```text
same user
same show
same idempotency key
same body
```

Expected result:

```text
one reservation
```

All requests must resolve to the same logical result.

---

# 19. Idempotency Database Constraint

The database must enforce:

```text
UNIQUE(show_id, user_id, idempotency_key)
```

This is critical.

Even if two requests execute simultaneously, PostgreSQL must not allow two idempotency records for the same logical key.

---

# 20. Idempotency Race

Conceptually:

```text
Request A
    ↓
No idempotency record
    ↓
Creates reservation
    ↓
Creates idempotency record
```

At the same time:

```text
Request B
    ↓
Attempts same idempotency key
    ↓
Unique constraint conflict / existing record
```

The implementation must then resolve Request B to the reservation created by Request A.

The database conflict must not become an unexpected 500.

---

# 21. Idempotency Same-Key / Different-Body

If an existing key is found:

```text
ABC
```

the stored request hash must be compared with the incoming request hash.

### Same hash

```text
Return original reservation/result.
```

### Different hash

```text
409 Conflict
```

The second request must not modify any reservation state.

---

# 22. Idempotency Must Survive Restarts

Idempotency state must be stored in PostgreSQL.

Do not use:

```csharp
Dictionary<string, Reservation>
```

or:

```csharp
IMemoryCache
```

as the authoritative idempotency store.

After application restart:

```text
same key
    ↓
existing database record
    ↓
same reservation
```

must still work.

---

# 23. Idempotency and Seat Acquisition

A replay of an already-successful idempotency key must not attempt to acquire the seat again.

Flow:

```text
Request
  ↓
Find idempotency record
  ↓
Hash matches
  ↓
Return original reservation
```

No additional seat update should be performed.

This avoids unnecessary database contention.

---

# 24. Transaction Rollback

Suppose a request wants:

```text
A12
A13
A14
```

and the application:

```text
acquires A12
acquires A13
fails to acquire A14
```

The transaction must roll back.

Final state:

```text
A12 → available
A13 → available
A14 → unavailable
```

There must be no partial reservation.

---

# 25. Cancellation

Cancellation must be transactional.

Conceptually:

```text
BEGIN
  │
  ├── Load reservation
  │
  ├── Verify authenticated owner
  │
  ├── Verify reservation state
  │
  ├── Transition reservation → cancelled
  │
  ├── Release associated seats
  │
  ├── Decrement user reservation count
  │
COMMIT
```

---

# 26. Cancellation Ownership

A user can only cancel their own reservation.

Example:

```text
Reservation R1
Owner = User A
```

User B attempts:

```text
POST /reservations/R1/cancel
```

Result:

```text
403 Forbidden
```

The reservation must remain unchanged.

---

# 27. Cancellation Race

Consider:

```text
A12 is confirmed for User A
```

At approximately the same time:

```text
Transaction A:
User A cancels A12

Transaction B:
User B reserves A12
```

The database transaction/locking strategy must ensure a valid serialized outcome.

Valid outcomes include:

```text
A cancels first
    ↓
A12 becomes available
    ↓
B acquires A12
```

or:

```text
B acquires first
    ↓
A12 remains owned by B
    ↓
A cannot release B's reservation
```

Invalid outcome:

```text
B acquires A12
    ↓
A cancellation later marks A12 available
```

This must never happen.

---

# 28. Lock Ordering

Where row locks are required, the application must use a consistent lock order.

Recommended conceptual order:

```text
1. Idempotency
2. ShowUserLimits
3. Seats in sorted order
4. Reservation state
```

The exact implementation may differ if conditional updates eliminate the need for explicit locks.

The important rule is:

> Concurrent transactions must acquire conflicting resources in a deterministic order.

---

# 29. PostgreSQL Isolation

The initial implementation should use PostgreSQL's default:

```text
READ COMMITTED
```

unless testing demonstrates a concrete need for stronger isolation.

The design should prefer:

- row-level locks
- atomic conditional updates
- unique constraints
- short transactions

rather than unnecessarily using:

```text
SERIALIZABLE
```

for every request.

Higher isolation may increase serialization failures and retry complexity without providing a necessary benefit for this design.

---

# 30. Deadlock Handling

Even with deterministic ordering, database deadlocks should be treated as possible infrastructure-level failures.

The system should:

1. Keep transactions short.
2. Acquire resources consistently.
3. Avoid unnecessary locks.
4. Monitor deadlock/transaction errors.
5. Retry only when the operation is known to be safely retryable.

Do not blindly retry reservation transactions indefinitely.

Retries must not violate idempotency.

---

# 31. No Application-Level Locks

Do not use:

```csharp
lock(...)
```

for reservation correctness.

Do not use:

```csharp
SemaphoreSlim
```

as the authoritative seat lock.

Do not use:

```csharp
ConcurrentDictionary
```

as the reservation state.

These mechanisms only coordinate within one process.

The deployment may have multiple API instances.

---

# 32. No Distributed Lock

Redis or another distributed locking system is not required.

PostgreSQL already owns the rows being modified.

Introducing a second lock authority would create additional failure scenarios:

```text
Redis says locked
PostgreSQL says available
```

or:

```text
lock acquired
application crashes
lock remains
```

For this assignment, PostgreSQL transaction semantics are sufficient.

---

# 33. Reconciliation Invariant

The following must always hold:

```text
available
+
held
+
confirmed
=
total_seats
```

The seat table is the source of truth for current seat state.

The API must calculate counts from seat state or maintain them transactionally with equivalent guarantees.

The system must never expose a state where:

```text
available + held + confirmed != total
```

after a committed transaction.

---

# 34. Failure Scenarios

## Database Unavailable

Reservation writes must fail safely.

Do not accept a reservation without durable persistence.

Readiness:

```text
503
```

---

## Seat Already Taken

Expected result:

```text
409
```

Not:

```text
500
```

---

## Per-User Limit Exceeded

Expected result:

```text
409
```

Not:

```text
500
```

---

## Idempotency Conflict

Expected result:

```text
409
```

Not:

```text
500
```

---

## Transaction Failure

Expected result:

```text
ROLLBACK
```

No partial reservation state may remain.

---

# 35. 20K Concurrency Test

The primary acceptance scenario is:

```text
20,000 concurrent reservation requests
```

with many requests targeting the same small set of hot seats.

For a single hot seat:

```text
20,000 requests
       ↓
      A12
```

Expected:

```text
confirmed = 1

seat_taken = approximately 19,999

unexpected_5xx = 0
```

The exact result may vary if some requests use invalid input or duplicate idempotency keys, but no seat may be confirmed twice.

---

# 36. Concurrent Per-User Test

Given:

```text
per_user_limit = 4
```

Send:

```text
10 concurrent requests
```

for the same user.

Expected:

```text
confirmed seats <= 4
```

The test must verify the persisted database state, not only HTTP responses.

---

# 37. Concurrent Idempotency Test

Send:

```text
100 concurrent requests
```

with:

```text
same show
same user
same idempotency key
same request body
```

Expected:

```text
one logical reservation
```

All successful/replayed responses must reference the same reservation ID.

---

# 38. Idempotency Conflict Test

Send:

```text
Request A:
key = ABC
seats = A12

Request B:
key = ABC
seats = A13
```

Expected:

```text
Request A → success
Request B → 409
```

No second reservation must be created.

---

# 39. Multi-Seat Concurrency Test

Example:

```text
Request A:
A12, A13

Request B:
A13, A14
```

Only one request can acquire `A13`.

Because multi-seat requests are all-or-nothing:

```text
One request may succeed.
The other must fail.
```

The losing request must not retain its other seat.

---

# 40. Concurrency Test Verification

Concurrency tests must verify more than HTTP response counts.

After each significant test:

1. Query the show state.
2. Query reservations.
3. Verify seat ownership.
4. Verify per-user counts.
5. Verify idempotency records.
6. Verify reconciliation.

Example:

```text
available + held + confirmed == total
```

and:

```text
No seat appears in more than one active reservation.
```

---

# 41. Database Constraints as Final Defense

Even if application code contains a race condition, database constraints should prevent invalid persistent state wherever possible.

Critical constraints include:

```text
UNIQUE(show_id, seat_number)

UNIQUE(show_id, user_id, idempotency_key)

PRIMARY KEY(show_id, user_id)
```

Application logic and database constraints work together.

---

# 42. Performance Considerations

Concurrency correctness comes before raw throughput.

Initial optimization priorities:

```text
1. Correct indexes
2. Short transactions
3. Minimal SQL round trips
4. Efficient seat lookup
5. Efficient connection pooling
6. Batch operations where appropriate
```

Do not add Redis or other infrastructure simply to improve benchmark numbers.

The system should first pass the correctness tests.

---

# 43. Observability for Concurrency

The following metrics should help identify concurrency problems:

```text
reservations_confirmed_total

reservations_declined_total{reason="seat_taken"}

reservations_declined_total{reason="per_user_limit"}

reservations_declined_total{reason="idempotent_replay"}

reservations_declined_total{reason="idempotency_conflict"}

reservation_duration_seconds
```

Useful logs include:

```text
request_id
show_id
user_id
reservation_id
seat
result
decline_reason
duration_ms
```

Do not log authentication tokens.

---

# 44. What We Must Prove

The implementation is not considered concurrency-safe merely because the code looks correct.

We must prove it through tests.

Minimum proof:

```text
Hot seat test
      ↓
No double booking

Same user concurrency
      ↓
Limit respected

Idempotency storm
      ↓
One logical reservation

Multi-seat race
      ↓
All-or-nothing

Cancellation race
      ↓
No resurrection

Reconciliation
      ↓
Counts remain correct
```

---

# 45. Final Concurrency Model

The final mental model is:

```text
                     HTTP Requests
                          │
             ┌────────────┼────────────┐
             ▼            ▼            ▼
          Request A    Request B    Request C
             │            │            │
             └────────────┼────────────┘
                          ▼
                  PostgreSQL Transaction
                          │
             ┌────────────┼─────────────┐
             │            │             │
             ▼            ▼             ▼
        Idempotency   User Limit      Seats
             │            │             │
             └────────────┼─────────────┘
                          ▼
                    Reservation
                          │
                          ▼
                       COMMIT
```

The application does not decide:

> "I checked that the seat was free, therefore I can sell it."

Instead:

> "The database atomically accepted my state transition, therefore I own the seat."

That distinction is the core correctness principle of this system.

---

# 46. Implementation Rule

Before implementing the reservation service, the developer must be able to explain:

1. Why two requests cannot both acquire the same seat.
2. Why 10 concurrent requests cannot bypass the per-user limit.
3. Why 100 retries cannot create 100 reservations.
4. Why the same idempotency key with a different body returns 409.
5. Why multi-seat reservations cannot partially commit.
6. Why cancellation cannot release another user's seat.
7. Why the system remains correct with multiple API instances.
8. Why application-level locks are insufficient.
9. Why PostgreSQL is the concurrency boundary.
10. How the database transaction rolls back on failure.

If any of these cannot be clearly explained, the concurrency design is not ready for implementation.