# AI Usage

## 1. Overview

This project was developed using an AI-assisted software development workflow.

AI tools were used throughout the software development lifecycle for:

- requirements analysis
- architecture exploration
- database design
- concurrency design
- API contract design
- implementation assistance
- test generation
- code review
- documentation
- observability review
- deployment troubleshooting

The final design and implementation decisions remain the responsibility of the developer.

AI-generated suggestions were reviewed, adapted, tested, and validated against the assignment requirements.

---

# 2. AI-Assisted SDLC

The development process followed this general workflow:

```text
Assignment
    ↓
Requirements extraction
    ↓
Architecture decisions
    ↓
Database/concurrency design
    ↓
API contract
    ↓
Implementation
    ↓
Automated tests
    ↓
Concurrency testing
    ↓
Observability
    ↓
Deployment
    ↓
Review and refinement
```

AI was used at multiple stages, rather than generating the entire application in one step.

---

# 3. Requirements Analysis

AI was used to help convert the assignment into explicit engineering requirements and invariants.

Examples include:

- no double-selling
- exactly one winner for a hot seat
- per-user seat limit
- idempotency
- authenticated user identity
- all-or-nothing multi-seat reservations
- cancellation ownership
- seat reconciliation
- health/readiness
- metrics
- structured logging
- correlation IDs
- concurrency testing

The requirements were documented in:

```text
docs/REQUIREMENTS.md
```

The purpose of this step was to establish testable acceptance criteria before implementation.

---

# 4. Architecture Design

AI was used to evaluate multiple architecture approaches.

The final architecture intentionally keeps the system simple:

```text
ASP.NET Core API
        │
        ▼
   Application
     Services
        │
        ▼
   EF Core / SQL
        │
        ▼
    PostgreSQL
```

The application is designed as a modular/vertical-slice style monolith rather than a distributed microservice architecture.

The primary reason is that the assignment's hardest problem is **correct concurrent state mutation**, not service-to-service communication.

Adding Kafka, Redis, distributed locks, or multiple services without a demonstrated requirement would increase operational complexity without improving the core correctness guarantees.

The architecture is documented in:

```text
docs/ARCHITECTURE.md
```

---

# 5. Concurrency Design

AI was particularly useful for exploring concurrency failure modes.

The design was evaluated against scenarios such as:

### Hot seat

```text
20,000 users
       ↓
same seat
       ↓
exactly one winner
       ↓
remaining requests -> 409
```

### Same-user limit

```text
multiple concurrent requests
       ↓
same user + same show
       ↓
atomic seat-count enforcement
```

### Idempotency storm

```text
many identical requests
       ↓
same user + show + idempotency key
       ↓
one logical reservation
```

### Cancellation race

```text
reserve/cancel operations
       ↓
concurrent database transactions
       ↓
consistent seat state
```

The final concurrency strategy uses PostgreSQL as the concurrency boundary.

The detailed design is documented in:

```text
docs/CONCURRENCY.md
```

---

# 6. Database Design

AI was used to review the database model and identify constraints required for correctness.

The database design includes:

- shows
- seats
- reservations
- reservation seats
- per-show/user reservation counters
- idempotency records

Important database constraints include:

```text
UNIQUE(show_id, seat_number)
UNIQUE(show_id, user_id, idempotency_key)
CHECK(price_paise >= 0)
CHECK(per_user_limit > 0)
CHECK(reserved_count >= 0)
```

The database is treated as the source of truth for reservation state.

Schema decisions are documented in:

```text
docs/DATABASE.md
```

---

# 7. API Design

AI was used to help define a consistent API contract before implementation.

The API contract covers:

- authentication
- request/response models
- status codes
- validation
- idempotency
- reservation behavior
- cancellation
- health endpoints
- metrics
- correlation IDs
- error responses

The contract is documented in:

```text
docs/API.md
```

The implementation should follow the API contract rather than allowing the API to emerge accidentally from implementation details.

---

# 8. Code Generation

AI may be used to generate or assist with:

- C# classes
- ASP.NET Core endpoints
- EF Core configurations
- database queries
- validation logic
- test cases
- logging code
- metrics instrumentation
- Docker configuration
- deployment configuration
- documentation

Generated code is treated as a draft.

Before accepting code, it is reviewed for:

- correctness
- concurrency behavior
- transaction boundaries
- error handling
- security
- performance
- maintainability
- consistency with the documented architecture

---

# 9. Testing

AI is used to help identify important test scenarios, but tests are executed against the actual implementation.

Tests include both normal and adversarial scenarios.

Examples:

### Functional tests

```text
create show
get show
reserve seats
cancel reservation
```

### Validation tests

```text
empty seat list
duplicate seats
invalid show
invalid authentication
missing idempotency key
```

### Concurrency tests

```text
hot-seat contention
per-user limit contention
idempotency storm
same idempotency key with different body
multi-seat contention
cancellation race
```

### Invariant tests

After concurrent operations:

```text
available + held + confirmed = total
```

must remain true.

AI-generated tests are not considered proof of correctness by themselves. Test results and database state are used to validate the implementation.

---

# 10. Code Review

AI is also used as a review assistant.

Review prompts focus on questions such as:

- Can two users reserve the same seat?
- Can a failed transaction leave inconsistent state?
- Can the user exceed the per-show limit under concurrency?
- Can the same idempotency key create two reservations?
- Can a user cancel another user's reservation?
- Can cancellation resurrect an incorrectly assigned seat?
- Can an expected conflict become a `500`?
- Are database constraints sufficient?
- Are transaction boundaries correct?
- Are metrics low-cardinality?
- Are logs safe to expose?

AI review comments are treated as suggestions and are verified against the actual implementation.

---

# 11. Observability Review

AI was used to review observability requirements around:

- structured logging
- correlation IDs
- HTTP request metrics
- reservation success/failure metrics
- idempotency metrics
- database health
- liveness
- readiness

The objective is to make failures observable without logging sensitive or high-cardinality data unnecessarily.

For example, the implementation should avoid using values such as:

```text
user_id
reservation_id
idempotency_key
```

as Prometheus metric labels.

---

# 12. Performance and Load Testing

AI was used to help design the burst/concurrency test strategy.

The important workload is not simply average throughput.

The key workload is contention:

```text
many concurrent requests
        ↓
same show
        ↓
same hot seat
```

The expected behavior is:

```text
1 success
N - 1 expected conflicts
0 unexpected server errors
```

Load testing is used to validate both correctness and operational behavior.

Performance results are treated as measurements of the deployed implementation, not predictions generated by AI.

---

# 13. Deployment Assistance

AI may be used for:

- Dockerfile generation/review
- environment configuration
- PostgreSQL connection configuration
- health check configuration
- deployment troubleshooting
- metrics exposure
- production configuration review

The application remains deployable as a standard ASP.NET Core application.

The Dockerfile exists to provide a reproducible deployment artifact even if Docker is not required for the local development workflow.

---

# 14. Security Review

AI was used to identify common security risks relevant to this assignment.

Examples include:

- trusting `user_id` from request bodies
- exposing database exceptions
- missing authorization on cancellation
- insecure administrative endpoints
- logging sensitive information
- unsafe connection string handling
- excessive metric cardinality
- accepting invalid monetary representations

The final implementation must validate these concerns through code and tests.

---

# 15. What AI Does Not Decide

The following decisions remain engineering decisions for the project:

- final architecture
- database as the source of truth
- transaction boundaries
- concurrency strategy
- idempotency semantics
- all-or-nothing reservation behavior
- cancellation semantics
- API contract
- security behavior
- deployment configuration
- acceptance of generated code

AI recommendations are evaluated against:

1. assignment requirements
2. documented invariants
3. database behavior
4. automated tests
5. concurrency tests
6. operational requirements

---

# 16. AI Verification Principle

A generated solution is not considered correct simply because an AI model suggested it.

The project follows:

```text
AI suggestion
      ↓
Engineering review
      ↓
Implementation
      ↓
Automated test
      ↓
Concurrency test
      ↓
Runtime verification
```

For concurrency-sensitive code, database behavior and reproducible tests are the final source of truth.

---

# 17. Example AI-Assisted Development Prompt

Prompts used during development follow a constraint-first approach.

Example:

```text
Review this reservation implementation against the following invariants:

1. A seat can only have one confirmed reservation.
2. Per-user limit is enforced under concurrency.
3. Same idempotency key + same request returns the original reservation.
4. Same idempotency key + different request returns 409.
5. Multi-seat reservation is all-or-nothing.
6. Authenticated user identity cannot be spoofed.
7. Expected concurrency conflicts must return 409, never 500.
8. Cancellation must be transactional.
9. available + held + confirmed must equal total seats.

Identify race conditions, transaction boundary problems,
database constraint gaps, and failure scenarios.

Do not propose Redis or distributed locks unless there is
a demonstrated requirement for them.
```

This approach keeps AI focused on the project's actual invariants rather than generating unnecessary infrastructure.

---

# 18. Human Responsibility

AI-assisted development does not remove engineering responsibility.

The developer is responsible for:

- understanding the generated code
- validating assumptions
- reviewing database behavior
- running tests
- testing concurrency
- reviewing security
- reviewing deployment configuration
- validating production behavior
- making final architectural decisions

The purpose of using AI in this project is to improve development speed and increase the breadth of review while keeping correctness and engineering judgment human-controlled.

---

# 19. Summary

AI was used as an engineering assistant across the SDLC:

```text
Requirements
Architecture
Database
Concurrency
API
Implementation
Testing
Review
Observability
Deployment
Documentation
```

The project deliberately uses AI in an iterative workflow rather than treating AI-generated code as the final implementation.

The most important correctness properties are validated through:

```text
PostgreSQL constraints
+
Transactions
+
Automated tests
+
Concurrency tests
+
Runtime observability
```

The final implementation and its behavior are the responsibility of the developer.