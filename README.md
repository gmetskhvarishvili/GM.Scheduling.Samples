# GM.Scheduling.Samples

A runnable [GM.Scheduling](https://github.com/gmetskhvarishvili/GM.Scheduling) demo built around the
**billing-cycle + dunning** use case: a recurring job bills subscriptions, records the declined ones
without failing the batch, and a dunning job retries them — with execution history persisted via
`GM.Scheduling.EntityFramework` (SQLite).

> References the sibling source repo by **project path**. Swap the `ProjectReference`s in
> `GM.Scheduling.Sample.API.csproj` for `PackageReference`s once published.

## Run

```bash
dotnet run --project GM.Scheduling.Sample.API
```

Jobs (registered in [`Program.cs`](GM.Scheduling.Sample.API/Program.cs)):

| Job | Schedule | What it shows |
| --- | --- | --- |
| `billing-cycle` | every 60s (after a 2-min delay) | bills 5 subscriptions; 2 decline → moved to dunning, reported as a **partial** run |
| `dunning-retry` | every 90s (after a 3-min delay) | retries the declined subscriptions **per-subscription** (they recover on the 2nd attempt) |
| `flaky-report` | on demand | throws once, then **retries** and succeeds (per-job `RetryPolicy`) |

## Try it

```bash
# Run billing now — one declined card doesn't fail the batch
curl -s -X POST http://localhost:5000/jobs/billing-cycle/trigger
curl -s http://localhost:5000/jobs/billing-cycle/history | jq '.[0]'
# → { "status": "PartiallyFailed", "processedCount": 3, "failedCount": 2, "summary": "Billed 3, 2 to dunning." }

# Recover the declines
curl -s -X POST http://localhost:5000/jobs/dunning-retry/trigger
curl -s http://localhost:5000/jobs/dunning-retry/history | jq '.[0]'
# → { "status": "Succeeded", "processedCount": 2, ... }

# Retry demo
curl -s -X POST http://localhost:5000/jobs/flaky-report/trigger
curl -s http://localhost:5000/jobs/flaky-report/history | jq '.[].status'
# → "Succeeded"  (attempt 1)   "Failed"  (attempt 0)

curl -s http://localhost:5000/subscriptions
curl -s -X POST http://localhost:5000/jobs/billing-cycle/pause
```

`GET /` lists every job with its last-run status.

## What it demonstrates

- **Skip/retry per record, not per batch** — `BillingCycleJob` catches each subscription's failure and
  returns `JobExecutionResult.Partial(...)`; the run completes and the failures are recorded.
- **Persistent history** — `UseEntityFrameworkHistory(o => o.UseSqlite(...))` writes each run to
  `gm_job_executions`, so `/history` and last-run status survive restarts. (Swap `UseSqlite` for
  `UseNpgsql`/`UseSqlServer`, and add `UseQuartzAdoStore(...)` to persist the schedules themselves.)
- **Distributed-safe** — the sample uses the in-memory lock; point `AddGMDistributedLock` at Redis and
  the recurring jobs fire **once across all instances**.

## Tests

```bash
dotnet test
```

`tests/GM.Scheduling.Sample.Tests` boots the app (each with its own SQLite history file) and asserts the
billing run is partial (3 billed / 2 declined), dunning recovers the 2, and the flaky job fails-then-succeeds.
