using Lagerkraft.Integrations.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Integrations.Webhooks;

public sealed class WebhookDeliveryJob : PeriodicJob
{
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(12)
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly WebhookPoster _poster;
    private readonly IClock _clock;

    public WebhookDeliveryJob(
        IServiceScopeFactory scopes,
        WebhookPoster poster,
        IClock clock,
        ILogger<WebhookDeliveryJob> log) : base(clock, log)
    {
        _scopes = scopes;
        _poster = poster;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

    public Task DeliverDueAsync(CancellationToken ct) => RunOnceAsync(ct);

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var now = _clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var due = await db.WebhookDeliveries
            .FromSql($"""
                SELECT * FROM webhook_deliveries
                WHERE dead_lettered_at IS NULL
                  AND (status = 'pending' OR (status = 'failed' AND next_attempt_at <= {now}))
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 50
                """)
            .ToListAsync(cancellationToken);

        foreach (var row in due)
        {
            var endpoint = await db.WebhookEndpoints.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == row.WebhookEndpointId, cancellationToken);
            if (endpoint is null || !endpoint.Active)
            {
                row.Status = "failed";
                row.LastError = "endpoint_inactive";
                row.NextAttemptAt = now + RetryDelays[Math.Min(row.Attempt, RetryDelays.Length - 1)];
                continue;
            }

            var signature = WebhookSignature.Compute(endpoint.Secret, row.Payload);
            var (ok, error) = await _poster.PostAsync(endpoint.Url, row.Payload, signature, cancellationToken);
            if (ok)
            {
                row.Status = "delivered";
                row.LastError = null;
                row.NextAttemptAt = null;
                continue;
            }

            row.Attempt += 1;
            row.LastError = error;
            if (row.Attempt > RetryDelays.Length)
            {
                row.Status = "dead_lettered";
                row.DeadLetteredAt = now;
                row.NextAttemptAt = null;
            }
            else
            {
                row.Status = "failed";
                row.NextAttemptAt = now + RetryDelays[row.Attempt - 1];
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
