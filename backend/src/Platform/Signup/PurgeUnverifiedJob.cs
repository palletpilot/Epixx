using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Signup;

public sealed class PurgeUnverifiedJob : PeriodicJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;

    public PurgeUnverifiedJob(
        IServiceScopeFactory scopes,
        IClock clock,
        ILogger<PurgeUnverifiedJob> logger)
        : base(clock, logger)
    {
        _scopes = scopes;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var cutoff = _clock.UtcNow.AddDays(-7);
        var stale = await db.SignupRequests
            .Where(s => s.VerifiedAt == null && s.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return;
        }

        db.SignupRequests.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
    }
}