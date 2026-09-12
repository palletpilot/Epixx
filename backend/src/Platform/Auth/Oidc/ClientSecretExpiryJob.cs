using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Email;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Auth.Oidc;

public sealed class ClientSecretExpiryJob : PeriodicJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;

    public ClientSecretExpiryJob(
        IServiceScopeFactory scopes,
        IClock clock,
        ILogger<ClientSecretExpiryJob> logger)
        : base(clock, logger)
    {
        _scopes = scopes;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(12);

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var now = _clock.UtcNow;
        var targets = await db.IdentityProviders.AsNoTracking()
            .Where(p => p.ClientSecretExpiresAt != null)
            .ToListAsync(cancellationToken);

        foreach (var provider in targets)
        {
            var expires = provider.ClientSecretExpiresAt!.Value;
            var days = (expires - now).TotalDays;
            string? kind = days switch
            {
                >= 29 and <= 31 => "oidc_secret_30d",
                >= 6 and <= 8 => "oidc_secret_7d",
                _ => null
            };
            if (kind is null)
            {
                continue;
            }

            var already = await db.AuditLogs.AsNoTracking().AnyAsync(
                a => a.TenantId == provider.TenantId
                     && a.Kind == kind
                     && a.At > now.AddDays(-2),
                cancellationToken);
            if (already)
            {
                continue;
            }

            var owners = await db.Memberships.AsNoTracking()
                .Where(m => m.TenantId == provider.TenantId && m.IsOwner)
                .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Email)
                .Where(e => e != null)
                .ToListAsync(cancellationToken);

            foreach (var to in owners)
            {
                await email.SendAsync(
                    to!,
                    "OIDC client secret expiring",
                    $"Your tenant OIDC client secret expires at {expires:u}. Reminder: {kind}",
                    cancellationToken);
            }

            db.AuditLogs.Add(new AuditLog
            {
                Id = Ids.New(),
                TenantId = provider.TenantId,
                Kind = kind,
                At = now,
                Actor = "client_secret_expiry_job"
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}