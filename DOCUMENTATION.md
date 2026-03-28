# Transaction Dispatch Service — Candidate Documentation

> **Authorship note:** All architecture, system design, pipeline decisions, and validation strategy documented here were planned and owned by the candidate. GitHub Copilot (Claude Sonnet 4.6) was used exclusively as a coding assistant to translate those plans into working code — it had no influence over what was built or how the system was designed.

---

## 1. Requirements Understanding & Assumptions

**Core requirement:** Build a microservice that reads XML transaction files from a folder, publishes them to Kafka, and tracks dispatch progress via a REST API.

**Key assumptions made:**

- Only `.xml` files should be dispatched (configurable via `SupportedExtensions`).
- A folder can only have one active job at a time — submitting the same folder twice while a job is running returns `409 Conflict` (folder-level idempotency).
- File-level idempotency: files already successfully processed in a prior run are skipped on re-dispatch.
- The service must be horizontally scalable; multiple instances must not double-process the same job — solved via a `ClaimedBy` worker-ID pattern in the database.
- Deletion of source files after dispatch is optional, controlled per-request via `DeleteAfterSend`.
- Files exceeding a configurable `MaxMessageSizeBytes` are permanently skipped (not retried).
- Kafka topic is created automatically on startup if it does not exist.

---

## 2. Design Approach & Decisions

### Architecture

The solution follows **Clean Architecture** with four layers:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `TransactionDispatch.Domain` | `DispatchJob` aggregate, `DispatchJobState` enum |
| Application | `TransactionDispatch.Application` | Interfaces, options, `DispatchRequest`/`DispatchResponse` contracts |
| Infrastructure | `TransactionDispatch.Infrastructure` | EF Core (PostgreSQL), Kafka producer, background worker |
| API | `TransactionDispatch.Api` | REST controllers, health checks, DI composition root |

### Key Design Decisions

All decisions below were made by the candidate during the design phase, before any code was written.

**Async job model:** Dispatch is long-running, so `POST /dispatch-transactions` returns `202 Accepted` immediately with a `jobId`. Clients poll `GET /dispatch-status/{jobId}` which returns HTTP `102 Processing` while in-flight and `200 OK` on completion. This avoids HTTP timeouts for large folders.

**Background service pipeline (candidate-designed — polling → parallelism → idempotency):** The candidate planned a three-stage pipeline inside a `BackgroundService`:
1. **Polling** — the service wakes every `PollIntervalSeconds`, queries PostgreSQL for `Queued` jobs up to `MaxPollBatchSize`, and claims each atomically.
2. **Parallel execution** — claimed jobs are fanned out with `Parallel.ForEachAsync` across up to `MaxParallelism` concurrent workers, keeping throughput proportional to CPU cores.
3. **Idempotency** — before each file is dispatched, the `ProcessedFiles` table is checked; already-successful files are skipped. A folder with an active job immediately returns `409 Conflict`. This three-stage design was deliberately sequenced to separate concerns: discovery, throughput, and correctness.

**Optimistic worker claiming:** Before processing, each worker attempts to atomically set `ClaimedBy = {machine}:{pid}`. If another instance already claimed it, the job is skipped. This enables safe horizontal scale-out without a distributed lock.

**Batched progress writes:** File progress (`ProcessedFiles`, `SuccessfulFiles`, `FailedFiles`) is flushed to the database every `ProgressSaveEvery` files rather than after every file, dramatically reducing DB write pressure at scale.

**Kafka partitioning strategy (candidate-designed for throughput):** The candidate identified that without an explicit message key, Kafka's sticky partitioner concentrates all traffic on one or two partitions regardless of topic partition count, creating a throughput ceiling. The solution — keying each message by `Path.GetFileName(filePath)` — was chosen deliberately: filenames have high cardinality and produce an even murmur2 hash distribution across all partitions, unlocking the full parallelism of the broker cluster. This was a deliberate upfront design decision, not a post-hoc fix.

**PostgreSQL only:**  PostgreSQL via EF Core with `IDbContextFactory` (per-operation lifetime) is the single persistence backend, ensuring correct behavior for parallel jobs.

**Singleton Kafka producer:** The `IProducer<string, byte[]>` is registered as a singleton — building a producer is expensive (TCP connections). All background threads share one producer instance.

---

## 3. Implementation Details

### API Endpoints

| Method | Route | Success | Notes |
|---|---|---|---|
| `POST` | `/dispatch-transactions` | `202 Accepted` + `{ jobId }` | `400` if path empty, `409` if folder already active |
| `GET` | `/dispatch-status/{jobId}` | `200 OK` or `102 Processing` | `404` if unknown jobId |
| `GET` | `/health` | `200 OK` — JSON health report | Checks DB connectivity and Kafka broker reachability |

### Validation Design (candidate-implemented, all layers)

The candidate designed and applied a defence-in-depth validation strategy across every layer of the stack:

| Layer | Mechanism | What is validated |
|---|---|---|
| API (model binding) | `[Required]` + custom `[NotWhiteSpace]` data annotations on `DispatchRequest`; `ModelState.IsValid` → `400 Bad Request` with `ValidationProblemDetails` | `folderPath` is present and not blank |
| Service (`DispatchService`) | `ArgumentNullException.ThrowIfNull`, whitespace guards on `FolderPath`, empty-GUID guard on `jobId` | Prevents null/empty reaching business logic |
| Store (`RelationalDispatchJobStore`) | `ArgumentException` on whitespace `folderPath` and empty `jobId` | Enforces invariants at the persistence boundary |
| Repository (`DispatchJobRepository`) | `ArgumentException` on null entity, blank `FolderPath`, empty `jobId`, blank `claimedBy` | Prevents corrupt DB writes |

The `NotWhiteSpaceAttribute` is a custom `ValidationAttribute` (`TransactionDispatch.Application/Validation/NotWhiteSpaceAttribute.cs`) that rejects strings that pass `string.IsNullOrEmpty` but fail `string.IsNullOrWhiteSpace` — closing a gap that `[Required]` alone does not cover.

### Configuration (`appsettings.json`)

```json
"Dispatch": {
  "MaxParallelism": 64,
  "RetryCount": 3,
  "RetryDelayMilliseconds": 100,
  "ProgressSaveEvery": 200,
  "PollIntervalSeconds": 5,
  "MaxPollBatchSize": 100,
  "SupportedExtensions": [ ".xml" ]
}
```

### Retry Strategy

Transient Kafka failures are retried with exponential backoff up to `RetryCount` times (`RetryDelayMilliseconds * 2^attempt`). A `ProduceException` with a permanent error code (e.g., message too large) is not retried. The job is marked `Failed` only after all retries are exhausted.

### Database Schema

Two tables are created by EF Core migrations:

- `DispatchJobs` — job state, counters, `ClaimedBy`, timestamps
- `ProcessedFiles` — per-file idempotency record (FK to `DispatchJobs`, cascade delete)

---

## 4. Testing Strategy & Validation

### Unit Tests (`TransactionDispatch.Tests`) — 56 tests

| Area | Tests |
|---|---|
| `DispatchBackgroundService` | Poll-to-process flow, fault resilience (poll throws, service survives) |
| `DispatchJobRepository` | CRUD, claiming, progress updates (SQLite in-memory) |
| `KafkaTransactionDispatcher` | Payload delivery, partition key, size limit skip, error propagation |
| `DispatchController` | All HTTP response codes (400/202/409/404/200/102) |
| `DatabaseHealthCheck` | Healthy/unhealthy path |

Coverage is approximately **87%** of reachable lines (infrastructure wiring and real-broker paths excluded via `[ExcludeFromCodeCoverage]`).

### Integration Tests (`TransactionDispatch.IntegrationTests`)

Six end-to-end tests using **Testcontainers** (real PostgreSQL + Kafka containers) validate:

1. XML files are published to Kafka and recorded in the DB
2. Empty folder completes with zero files
3. Non-XML files are ignored
4. `DeleteAfterSend` removes source files after dispatch
5. Folder-level idempotency rejects duplicate submissions
6. File-level idempotency skips already-processed files

---

## 5. Challenges & How They Were Addressed

| Challenge | Resolution |
|---|---|
| Kafka sticky partitioner concentrating messages on 1–2 partitions | Switched `Message<Null, byte[]>` to `Message<string, byte[]>` keyed by filename; enforces even murmur2 hash distribution |
| Double-processing risk with multiple service instances | Atomic `TryClaimJobAsync` sets `ClaimedBy` in a single `ExecuteUpdateAsync`; instances that lose the race skip the job |
| DB write pressure at high file counts | Batched progress flush every `ProgressSaveEvery` files instead of per-file |
| Avoiding HTTP timeouts for large folder dispatch | Async job model: immediately return `202` + jobId, client polls for completion |
| Idempotent file tracking without memory blowup | `ProcessedFiles` table persisted in PostgreSQL; never loaded into memory as a full set |
| Kafka producer being too expensive to create per-request | Registered as `Singleton`; shared across all parallel dispatch threads |
| Testcontainers offset isolation between tests | `IConsumer.QueryWatermarkOffsets()` captures start offsets before each test; consumption is bounded to messages produced within that test |

---

## 6. Use of AI Tools & Agentic Workflows

**GitHub Copilot (Claude Sonnet 4.6)** was used as an agentic coding assistant within VS Code. Its role was strictly limited to **code generation and implementation** — all design, architecture, and engineering decisions were made by the candidate independently before AI assistance was engaged.

### Division of responsibility

| Responsibility | Owner |
|---|---|
| Clean Architecture layer breakdown and project structure | Candidate |
| Async job model design (`202` + polling pattern) | Candidate |
| BackgroundService pipeline: polling → parallelism → idempotency | Candidate |
| Kafka partitioning strategy (filename key for murmur2 distribution) | Candidate |
| Optimistic claiming strategy (`ClaimedBy` atomic update) | Candidate |
| Batched progress-write design (`ProgressSaveEvery`) | Candidate |
| Defence-in-depth validation across all four layers | Candidate |
| Custom `[NotWhiteSpace]` validation attribute design | Candidate |
| Translating designs into C# code (classes, methods, EF migrations) | AI (coding assistant) |
| Cross-cutting multi-file edits (e.g. key-type change across 6 test mocks) | AI (coding assistant) |
| Unit test scaffolding after production code was written | AI (coding assistant) |

### How AI was used

- **Code generation from design:** Once the candidate specified a design decision (e.g., "the BackgroundService must poll, fan out with `Parallel.ForEachAsync`, then check idempotency per file"), the agent translated that into implementation code.
- **Coordinated multi-file edits:** Mechanical but error-prone changes — such as switching `IProducer<Null, byte[]>` to `IProducer<string, byte[]>` and updating all six mock setups simultaneously — were delegated to the agent.
- **Boilerplate reduction:** EF Core migration scaffolding, options class wiring, XML documentation comments, and health check registration were all generated by the agent following patterns specified by the candidate.
- **Test scaffolding:** The agent generated unit tests against interfaces and contracts the candidate had already defined; all test scenarios and assertions were reviewed and approved by the candidate.

### Quality controls applied

- **Every line of AI-generated code was read and reviewed by the candidate** before being accepted — no agent output was merged blindly.
- Every agent edit was followed by `dotnet build -warnaserror` and `dotnet test`; no change was accepted unless all tests passed with zero warnings.
- The agent was required to read each file before editing it — it never modified code it had not first read.
- All design decisions were defined by the candidate before the agent was invoked — the agent never determined *what* to build, only *how* to write the code for it.

### Reflection

Using AI as a coding assistant — rather than a design assistant — kept the architectural integrity of the solution firmly under the candidate's control. The agent's greatest value was eliminating mechanical, repetitive, and cross-cutting coding work (coordinated multi-file edits, boilerplate, test scaffolding). Its boundary was intentional: design decisions such as the polling-to-parallelism-to-idempotency pipeline, filename-based Kafka partitioning, and the four-layer validation strategy required domain reasoning and trade-off analysis that the candidate provided explicitly. The agent executed those decisions faithfully.
