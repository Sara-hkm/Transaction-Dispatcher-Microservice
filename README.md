# Transaction Dispatch Service

A .NET 8 microservice that reads XML transaction files from a folder, publishes them to Kafka, and tracks dispatch progress via a REST API.

---

## 🚀 Setup Instructions

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

### Option A — Full Docker stack (recommended)

Runs the API, PostgreSQL, Kafka, Kafka UI, and pgAdmin all together:

```bash
docker compose up --build -d
```

Wait ~60 seconds for all health checks to pass, then access:

| Service | URL |
|---|---|
| API + Swagger | http://localhost:8080/swagger |
| Health check | http://localhost:8080/health |
| Kafka UI | http://localhost:8090 |
| pgAdmin | http://localhost:5050 |

> pgAdmin login — email: `admin@dispatch.com` · password: `admin`

To stop everything:
```bash
docker compose down
```

To stop and remove all data volumes (full reset):
```bash
docker compose down -v
```

---

### Option B — API only (local dev, infrastructure via Docker)

1. Start only the infrastructure services:
   ```bash
   docker compose up postgres kafka -d
   ```

2. Run the API locally:
   ```bash
   cd TransactionDispatch.Api
   dotnet run
   ```

3. Open Swagger:
   ```
   http://localhost:5000/swagger
   ```

---

## 📡 API Usage

### POST `/api/dispatch-transactions`

Submit a folder of XML files for dispatch to Kafka. Returns immediately with a `jobId`.

**Request:**
```json
POST http://localhost:8080/api/dispatch-transactions
Content-Type: application/json

{
  "folderPath": "/transactions",
  "deleteAfterSend": false
}
```

**Responses:**

| Code | Meaning |
|---|---|
| `202 Accepted` | Job created — body contains `{ "jobId": "..." }` |
| `400 Bad Request` | `folderPath` is missing or blank |
| `409 Conflict` | A job for this folder is already running |

---

### GET `/api/dispatch-status/{jobId}`

Poll the status of a dispatch job.

**Example:**
```
GET http://localhost:8080/api/dispatch-status/3fa85f64-5717-4562-b3fc-2c963f66afa6
```

**Responses:**

| Code | Meaning |
|---|---|
| `102 Processing` | Job is still running — body contains current progress counters |
| `200 OK` | Job completed — body contains final `TotalFiles`, `SuccessfulFiles`, `FailedFiles` |
| `404 Not Found` | Unknown `jobId` |

---

### GET `/health`

Returns the health of all dependencies (PostgreSQL + Kafka broker).

```
GET http://localhost:8080/health
```

---

## ⚙️ Configuration

All settings are in `TransactionDispatch.Api/appsettings.json` and can be overridden via environment variables (double-underscore `__` as separator).

### Kafka

| Setting | Environment variable | Default |
|---|---|---|
| Bootstrap servers | `Kafka__BootstrapServers` | `localhost:9092` |
| Topic name | `Kafka__Topic` | `transactions-topic` |
| Number of partitions | `Kafka__NumPartitions` | `6` |
| Replication factor | `Kafka__ReplicationFactor` | `1` |
| Client ID | `Kafka__ClientId` | `transaction-dispatch-service` |
| Compression | `Kafka__CompressionType` | `snappy` |
| Producer linger (ms) | `Kafka__LingerMs` | `20` |
| Idempotent delivery | `Kafka__EnableIdempotence` | `true` |

### Database

| Setting | Environment variable | Default |
|---|---|---|
| Connection string | `Persistence__ConnectionString` | `Host=localhost;Port=5432;Database=TransactionDispatchDB;Username=postgres;Password=P@ssw0rd;` |

### Dispatch

| Setting | Environment variable | Default |
|---|---|---|
| Max parallel workers | `Dispatch__MaxParallelism` | `64` |
| Poll interval | `Dispatch__PollIntervalSeconds` | `5` |
| Max jobs per poll | `Dispatch__MaxPollBatchSize` | `100` |
| Retry count | `Dispatch__RetryCount` | `3` |
| Retry delay (ms) | `Dispatch__RetryDelayMilliseconds` | `100` |
| Progress save frequency | `Dispatch__ProgressSaveEvery` | `200` |
| Supported file types | `Dispatch__SupportedExtensions__0` | `.xml` |

### Idempotency

| Setting | Environment variable | Default |
|---|---|---|
| File-level idempotency | `Idempotency__EnableFileIdempotency` | `true` |
| Folder-level idempotency | `Idempotency__EnableFolderIdempotency` | `true` |
| Folder idempotency window | `Idempotency__FolderIdempotencyWindowMinutes` | `5` |

---

## 📝 Notes

### Large file processing

The API accepts folders of any size. Dispatch runs entirely in a background service — the API returns `202 Accepted` immediately so HTTP clients never time out regardless of folder size.

### Parallel execution

Files within a job are dispatched using `Parallel.ForEachAsync` with a configurable `MaxParallelism` cap. Progress is batched and flushed to the database every `ProgressSaveEvery` files to minimise DB write pressure.

### Retry

Transient Kafka delivery failures are retried up to `RetryCount` times with exponential backoff (`RetryDelayMilliseconds × 2^attempt`). Permanent errors (e.g. message too large) are not retried — the file is marked failed and processing continues.

### Idempotency

- **File-level:** Files already successfully dispatched in a previous run are skipped automatically — safe to re-submit the same folder after a partial failure.
- **Folder-level:** Submitting a folder that already has an active job returns `409 Conflict` immediately.

### Horizontal scaling

Multiple instances of the service can run simultaneously. Before processing a job, each instance atomically claims it by setting `ClaimedBy = {hostname}:{pid}`. Instances that lose the race skip the job — no distributed lock required.

### Testing

```bash
# Unit tests (64 tests, ~2 seconds)
dotnet test TransactionDispatch.Tests/

# Integration tests (requires Docker)
dotnet test TransactionDispatch.IntegrationTests/
```
