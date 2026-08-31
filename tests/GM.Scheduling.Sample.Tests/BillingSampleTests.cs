using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GM.Scheduling.Sample.Tests;

// Boots the real sample (Quartz + GM.DistributedLock + EF-Sqlite history) and drives the billing/dunning flow.
public sealed class BillingSampleTests : IClassFixture<BillingSampleTests.Factory>, IDisposable
{
    private readonly HttpClient _client;

    public BillingSampleTests(Factory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private static async Task<JsonElement> WaitForLastRunAsync(HttpClient client, string job, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{job}/history");
            if (history.GetArrayLength() > 0)
                return history[0];
            await Task.Delay(100);
        }
        return default;
    }

    [Fact]
    public async Task BillingRun_PartiallyFails_ThenDunningRecovers()
    {
        // Billing: 5 due, 2 decline → PartiallyFailed with 3 processed / 2 failed.
        await _client.PostAsync("/api/v1/jobs/billing-cycle/trigger", null);
        var billing = await WaitForLastRunAsync(_client, "billing-cycle", TimeSpan.FromSeconds(15));

        Assert.Equal("PartiallyFailed", billing.GetProperty("status").GetString());
        Assert.Equal(3, billing.GetProperty("processedCount").GetInt32());
        Assert.Equal(2, billing.GetProperty("failedCount").GetInt32());

        // Dunning: retries the 2 declined subscriptions, which succeed on their second attempt.
        await _client.PostAsync("/api/v1/jobs/dunning-retry/trigger", null);
        var dunning = await WaitForLastRunAsync(_client, "dunning-retry", TimeSpan.FromSeconds(15));

        Assert.Equal("Succeeded", dunning.GetProperty("status").GetString());
        Assert.Equal(2, dunning.GetProperty("processedCount").GetInt32());
    }

    [Fact]
    public async Task FlakyJob_Retries_ThenSucceeds()
    {
        await _client.PostAsync("/api/v1/jobs/flaky-report/trigger", null);

        // Wait until both the failed attempt and the successful retry are recorded.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        JsonElement history = default;
        while (DateTime.UtcNow < deadline)
        {
            history = await _client.GetFromJsonAsync<JsonElement>("/api/v1/jobs/flaky-report/history");
            if (history.GetArrayLength() >= 2) break;
            await Task.Delay(100);
        }

        var statuses = history.EnumerateArray().Select(e => e.GetProperty("status").GetString()).ToList();
        Assert.Contains("Failed", statuses);
        Assert.Contains("Succeeded", statuses);
    }

    // Gives each test class run its own SQLite history file so assertions aren't polluted by prior runs.
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"gm-sched-sample-{Guid.NewGuid():N}.db");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SchedulingDb"] = $"Data Source={_dbPath}",
            }));
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                try { File.Delete(_dbPath); } catch { /* best effort */ }
        }
    }
}
