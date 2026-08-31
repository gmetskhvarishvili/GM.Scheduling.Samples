using System.Collections.Concurrent;
using GM.Scheduling;

namespace GM.Scheduling.Sample.API;

/// <summary>A toy subscription. <see cref="DeclinesOnce"/> fails its first charge and succeeds on retry — to show dunning.</summary>
public sealed class Subscription
{
    public required string Id { get; init; }
    public decimal Amount { get; init; }
    public bool DeclinesOnce { get; init; }
    public int ChargeAttempts { get; set; }
    public string Status { get; set; } = "active"; // active → billed | dunning
    public DateTimeOffset? LastBilledAtUtc { get; set; }
}

/// <summary>An in-memory stand-in for GM.Subscriptions.</summary>
public sealed class SubscriptionStore
{
    private readonly ConcurrentDictionary<string, Subscription> _subs = new(StringComparer.Ordinal);

    public SubscriptionStore()
    {
        // 5 due; 2 will decline their first charge (then recover under dunning retry).
        foreach (var (id, amount, declines) in new[]
                 {
                     ("sub-1", 9.99m, false), ("sub-2", 19.99m, true), ("sub-3", 9.99m, false),
                     ("sub-4", 49.99m, true), ("sub-5", 9.99m, false),
                 })
        {
            _subs[id] = new Subscription { Id = id, Amount = amount, DeclinesOnce = declines };
        }
    }

    public IReadOnlyCollection<Subscription> GetAll() => _subs.Values.OrderBy(s => s.Id).ToList();
    public IEnumerable<Subscription> DueForBilling() => _subs.Values.Where(s => s.Status == "active");
    public IEnumerable<Subscription> InDunning() => _subs.Values.Where(s => s.Status == "dunning");

    /// <summary>Charges a subscription. Throws for a subscription that "declines" on this attempt.</summary>
    public static void Charge(Subscription sub)
    {
        sub.ChargeAttempts++;
        if (sub.DeclinesOnce && sub.ChargeAttempts == 1)
            throw new InvalidOperationException("card declined");
        sub.Status = "billed";
        sub.LastBilledAtUtc = DateTimeOffset.UtcNow;
    }

    public static void MoveToDunning(Subscription sub) => sub.Status = "dunning";
}

/// <summary>
/// The recurring billing run. Bills every due subscription <b>independently</b> — one declined card
/// doesn't fail the batch; it's moved to dunning and reported via <see cref="JobExecutionResult.Partial"/>.
/// </summary>
public sealed class BillingCycleJob : IScheduledJob
{
    private readonly SubscriptionStore _store;
    private readonly ILogger<BillingCycleJob> _logger;

    public BillingCycleJob(SubscriptionStore store, ILogger<BillingCycleJob> logger) => (_store, _logger) = (store, logger);

    public Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var processed = 0;

        foreach (var sub in _store.DueForBilling())
        {
            try
            {
                SubscriptionStore.Charge(sub);
                processed++;
            }
            catch (Exception ex)
            {
                SubscriptionStore.MoveToDunning(sub);
                failures.Add($"{sub.Id}: {ex.Message}");
                _logger.LogWarning(ex, "Billing declined for {Sub}; moved to dunning.", sub.Id);
            }
        }

        var result = failures.Count == 0
            ? JobExecutionResult.Success(processed, summary: $"Billed {processed}.")
            : JobExecutionResult.Partial(processed, failures, summary: $"Billed {processed}, {failures.Count} to dunning.");
        return Task.FromResult(result);
    }
}

/// <summary>Retries subscriptions in dunning — again per-subscription, so one still-failing card doesn't stop the rest.</summary>
public sealed class DunningRetryJob : IScheduledJob
{
    private readonly SubscriptionStore _store;

    public DunningRetryJob(SubscriptionStore store) => _store = store;

    public Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var recovered = 0;

        foreach (var sub in _store.InDunning())
        {
            try
            {
                SubscriptionStore.Charge(sub);
                recovered++;
            }
            catch (Exception ex)
            {
                failures.Add($"{sub.Id}: {ex.Message}");
            }
        }

        return Task.FromResult(failures.Count == 0
            ? JobExecutionResult.Success(recovered, summary: $"Recovered {recovered} from dunning.")
            : JobExecutionResult.Partial(recovered, failures));
    }
}

/// <summary>A job that fails transiently on its first attempt — to demonstrate the per-job <see cref="RetryPolicy"/>.</summary>
public sealed class FlakyReportJob : IScheduledJob
{
    public Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
        context.Attempt == 0
            ? throw new InvalidOperationException("upstream timeout")
            : Task.FromResult(JobExecutionResult.Success(summary: $"Report generated on retry #{context.Attempt}."));
}
