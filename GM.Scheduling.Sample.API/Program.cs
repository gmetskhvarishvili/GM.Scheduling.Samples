using GM.DistributedLock;
using GM.Scheduling;
using GM.Scheduling.EntityFramework;
using GM.Scheduling.Sample.API;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// In-memory lock keeps the sample self-contained. Swap for GM.DistributedLock.Redis in production —
// that's what makes a recurring job fire once across every instance instead of once per instance.
builder.Services.AddGMDistributedLock();

// Serialize JobRunStatus (and other enums) as readable strings rather than numbers.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton<SubscriptionStore>();

var historyConnection = builder.Configuration.GetConnectionString("SchedulingDb")
    ?? "Data Source=scheduling-sample.db";

builder.Services.AddGMScheduling(s =>
{
    // Billing runs on a cadence; dunning retries the declines a bit later. A start-delay avoids an
    // immediate fire at startup (so you can trigger them by hand first); a real deployment would use
    // cron like "0 0 2 * * ?".
    s.AddJob<BillingCycleJob>("billing-cycle", JobSchedule.EveryInterval(TimeSpan.FromSeconds(60), startDelay: TimeSpan.FromMinutes(2)));
    s.AddJob<DunningRetryJob>("dunning-retry", JobSchedule.EveryInterval(TimeSpan.FromSeconds(90), startDelay: TimeSpan.FromMinutes(3)));

    // A transient-failure job to show the per-job retry policy (fails once, then succeeds).
    s.AddJob<FlakyReportJob>("flaky-report", retryPolicy: RetryPolicy.Fixed(2, TimeSpan.FromSeconds(2)));

    // Persist execution history so last-run status survives restarts (GM.Scheduling.EntityFramework).
    s.UseEntityFrameworkHistory(o => o.UseSqlite(historyConnection));
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness must not depend on downstream dependencies, so it runs no checks; readiness runs
// every registered health check (none here yet). See engineering baseline §11.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

// Create the history table (gm_job_executions).
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

string[] jobNames = ["billing-cycle", "dunning-retry", "flaky-report"];

string[] tryIt =
[
    "POST /api/v1/jobs/billing-cycle/trigger    then  GET /api/v1/jobs/billing-cycle/history",
    "POST /api/v1/jobs/dunning-retry/trigger    (recovers the declined subscriptions)",
    "POST /api/v1/jobs/flaky-report/trigger     (fails once, retries, succeeds)",
    "GET  /api/v1/subscriptions",
];

app.MapGet("/", async (IJobScheduler scheduler) =>
{
    var jobs = new List<object>();
    foreach (var name in jobNames)
    {
        var last = await scheduler.GetLastExecutionAsync(name);
        jobs.Add(new { name, lastRun = last is null ? null : new { last.Status, last.StartedAtUtc, last.ProcessedCount, last.FailedCount, last.Summary } });
    }
    return Results.Ok(new
    {
        message = "GM.Scheduling sample — billing cycle + dunning",
        try_it = tryIt,
        jobs,
    });
});

app.MapPost("/api/v1/jobs/{name}/trigger", async (string name, IJobScheduler scheduler) =>
{
    await scheduler.TriggerNowAsync(name);
    return Results.Accepted($"/api/v1/jobs/{name}/history");
});

app.MapPost("/api/v1/jobs/{name}/pause", async (string name, IJobScheduler scheduler) =>
{
    await scheduler.PauseAsync(name);
    return Results.Ok(new { name, state = "paused" });
});

app.MapPost("/api/v1/jobs/{name}/resume", async (string name, IJobScheduler scheduler) =>
{
    await scheduler.ResumeAsync(name);
    return Results.Ok(new { name, state = "resumed" });
});

app.MapGet("/api/v1/jobs/{name}/history", async (string name, IJobScheduler scheduler) =>
    Results.Ok(await scheduler.GetHistoryAsync(name, 20)));

app.MapGet("/api/v1/subscriptions", (SubscriptionStore store) => Results.Ok(store.GetAll()));

await app.RunAsync();

// Exposed so the test project can spin the app up with WebApplicationFactory.
public partial class Program
{
    protected Program()
    {
    }
}
