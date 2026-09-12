using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Email;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Lagerkraft.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Lifecycle;

public sealed class TrialExpiryJob : PeriodicJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;

    public TrialExpiryJob(
        IServiceScopeFactory scopes,
        IClock clock,
        ILogger<TrialExpiryJob> logger)
        : base(clock, logger)
    {
        _scopes = scopes;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var machine = scope.ServiceProvider.GetRequiredService<TenantStateMachine>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var now = _clock.UtcNow;

        var rows = await db.Subscriptions
            .Include(s => s.Tenant)
            .Where(s => s.EndedAt == null
                        && s.Tenant.LifecycleState == LifecycleState.Trialing
                        && s.TrialEndsAt != null)
            .ToListAsync(cancellationToken);

        foreach (var sub in rows)
        {
            var tenant = sub.Tenant;
            var effectiveEnd = tenant.TrialExtendedUntil is { } ext && ext > sub.TrialEndsAt
                ? ext
                : sub.TrialEndsAt!.Value;
            if (now < effectiveEnd)
            {
                continue;
            }

            var hours = OperatingHours.Parse(tenant.OperatingHours, tenant.NightShift);
            var flipAt = OperatingHours.NextClosedWindowStart(hours, effectiveEnd);
            var warnAt = flipAt - TimeSpan.FromHours(48);

            if (now >= flipAt)
            {
                await machine.TransitionAsync(
                    tenant,
                    LifecycleState.TrialExpired,
                    actor: "trial_expiry_job",
                    reason: "trial_ended",
                    detail: new { flip_at = flipAt, trial_ends_at = effectiveEnd },
                    ct: cancellationToken);
                continue;
            }

            if (now < warnAt)
            {
                continue;
            }

            var already = await db.AuditLogs.AsNoTracking().AnyAsync(
                a => a.TenantId == tenant.Id
                     && a.Kind == "trial_expiry_warning"
                     && a.At > flipAt.AddHours(-50),
                cancellationToken);
            if (already)
            {
                continue;
            }

            var owners = await db.Memberships.AsNoTracking()
                .Where(m => m.TenantId == tenant.Id && m.IsOwner)
                .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Email)
                .Where(e => e != null)
                .ToListAsync(cancellationToken);

            foreach (var to in owners)
            {
                await email.SendAsync(
                    to!,
                    "Your Lagerkraft trial ends soon",
                    $"Access will switch to read-only at {flipAt:u} (next closed window after trial end). Convert your trial to keep operating.",
                    cancellationToken);
            }

            db.AuditLogs.Add(new AuditLog
            {
                Id = Ids.New(),
                TenantId = tenant.Id,
                Kind = "trial_expiry_warning",
                At = now,
                Actor = "trial_expiry_job",
                Detail = System.Text.Json.JsonSerializer.Serialize(new { flip_at = flipAt })
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
