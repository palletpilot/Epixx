using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Tenancy;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lagerkraft.Platform.Provisioning;

public sealed class ProvisioningJob(
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<ProvisioningJob> logger) : PeriodicJob(clock, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // Recover lost publish: already Trialing is a no-op (attempts stay).
        var pending = await db.SignupRequests
            .Where(s => s.VerifiedAt != null
                        && s.JoinRequestForTenantId == null
                        && s.ProvisioningAttempts < 3
                        && (s.TenantId == null
                            || db.Tenants.Any(t =>
                                t.Id == s.TenantId
                                && t.LifecycleState != LifecycleState.Trialing)))
            .OrderBy(s => s.VerifiedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var signup in pending)
        {
            await ProvisionOneAsync(scope.ServiceProvider, signup.Id, cancellationToken);
        }
    }

    private async Task ProvisionOneAsync(IServiceProvider sp, Guid signupId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<PlatformDbContext>();
        var clockLocal = sp.GetRequiredService<IClock>();
        var protector = sp.GetRequiredService<ConnectionStringProtector>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var dbCreator = sp.GetRequiredService<ITenantDatabaseCreator>();
        var migrate = sp.GetRequiredService<IWmsCoreMigrateClient>();
        var events = sp.GetRequiredService<IPlatformEventPublisher>();
        var configuration = sp.GetRequiredService<IConfiguration>();

        var row = await db.SignupRequests.FirstAsync(s => s.Id == signupId, ct);
        if (row.TenantId is Guid existingId)
        {
            var existing = await db.Tenants.FirstOrDefaultAsync(t => t.Id == existingId, ct);
            if (existing?.LifecycleState == LifecycleState.Trialing)
            {
                return;
            }
        }

        if (row.ProvisioningAttempts >= 3
            && row.TenantId is Guid failedId
            && await db.Tenants.AnyAsync(
                t => t.Id == failedId && t.LifecycleState == LifecycleState.ProvisioningFailed, ct))
        {
            return;
        }

        row.ProvisioningAttempts += 1;
        await db.SaveChangesAsync(ct);

        Tenant tenant;
        if (row.TenantId is Guid tid)
        {
            tenant = await db.Tenants.FirstAsync(t => t.Id == tid, ct);
            if (tenant.LifecycleState == LifecycleState.ProvisioningFailed)
            {
                tenant.LifecycleState = LifecycleState.Provisioning;
                tenant.StateChangedAt = clockLocal.UtcNow;
                tenant.LastError = null;
            }
        }
        else
        {
            tenant = new Tenant
            {
                Id = Ids.New(),
                Slug = row.Slug,
                CompanyName = row.CompanyName,
                SignupChannel = "self_serve",
                LifecycleState = LifecycleState.Provisioning,
                StateChangedAt = clockLocal.UtcNow,
                MigrationStatus = "pending"
            };
            db.Tenants.Add(tenant);
            db.AuditLogs.Add(new AuditLog
            {
                Id = Ids.New(),
                TenantId = tenant.Id,
                Kind = "lifecycle",
                ToState = nameof(LifecycleState.Provisioning),
                At = clockLocal.UtcNow,
                Actor = "provisioning_job"
            });
            row.TenantId = tenant.Id;
            await db.SaveChangesAsync(ct);
        }

        var databaseName = "tenant_" + tenant.Id.ToString("N");
        try
        {
            await dbCreator.CreateAsync(tenant.Id, databaseName, ct);

            var platformCs = configuration.GetConnectionString("platform")
                ?? throw new InvalidOperationException("ConnectionStrings:platform missing");
            var tenantCs = new NpgsqlConnectionStringBuilder(platformCs) { Database = databaseName }.ConnectionString;
            var (wrappedDek, ciphertext) = protector.Encrypt(tenantCs);
            tenant.WrappedDek = wrappedDek;
            tenant.ConnectionCiphertext = ciphertext;
            await db.SaveChangesAsync(ct);

            await migrate.MigrateAsync(tenant.Id, ct);

            var plan = await db.Plans.SingleAsync(p => p.Code == "pro", ct);
            var adminRole = await db.Roles.SingleAsync(
                r => r.TenantId == null && r.InternalName == Permissions.TenantAdmin, ct);

            var user = await users.FindByEmailAsync(row.Email);
            if (user is null)
            {
                user = new AppUser
                {
                    Id = Ids.New(),
                    Email = row.Email,
                    UserName = row.Email,
                    EmailConfirmed = true,
                    PasswordHash = row.PasswordHash
                };
                var create = await users.CreateAsync(user);
                if (!create.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Failed to create user: " + string.Join(", ", create.Errors.Select(e => e.Code)));
                }
            }

            if (!await db.Memberships.AnyAsync(m => m.UserId == user.Id && m.TenantId == tenant.Id, ct))
            {
                var membership = new Membership
                {
                    Id = Ids.New(),
                    UserId = user.Id,
                    TenantId = tenant.Id,
                    IsOwner = true
                };
                db.Memberships.Add(membership);
                db.RoleAssignments.Add(new RoleAssignment
                {
                    Id = Ids.New(),
                    MembershipId = membership.Id,
                    RoleId = adminRole.Id,
                    ValidFrom = clockLocal.UtcNow
                });
            }

            tenant.OwnerUserId = user.Id;

            if (!await db.BillingAccounts.AnyAsync(b => b.TenantId == tenant.Id, ct))
            {
                db.BillingAccounts.Add(new BillingAccount
                {
                    Id = Ids.New(),
                    TenantId = tenant.Id,
                    LegalName = row.CompanyName,
                    OrgNumber = row.OrgNumber,
                    Country = "SE",
                    Currency = "SEK"
                });
            }

            if (!await db.Subscriptions.AnyAsync(s => s.TenantId == tenant.Id && s.EndedAt == null, ct))
            {
                db.Subscriptions.Add(new Subscription
                {
                    Id = Ids.New(),
                    TenantId = tenant.Id,
                    PlanId = plan.Id,
                    Status = "trialing",
                    TrialEndsAt = clockLocal.UtcNow.AddDays(30),
                    StartedAt = clockLocal.UtcNow
                });
            }

            tenant.LifecycleState = LifecycleState.Trialing;
            tenant.StateChangedAt = clockLocal.UtcNow;
            tenant.LastError = null;
            db.AuditLogs.Add(new AuditLog
            {
                Id = Ids.New(),
                TenantId = tenant.Id,
                Kind = "lifecycle",
                FromState = nameof(LifecycleState.Provisioning),
                ToState = nameof(LifecycleState.Trialing),
                At = clockLocal.UtcNow,
                Actor = "provisioning_job"
            });
            await db.SaveChangesAsync(ct);

            await events.PublishAsync(
                tenant.Id,
                "tenant",
                "provisioned",
                new { tenant_id = tenant.Id, slug = tenant.Slug },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Provisioning failed for signup {SignupId} tenant {TenantId} attempt {Attempt}",
                row.Id,
                tenant.Id,
                row.ProvisioningAttempts);

            await db.Entry(row).ReloadAsync(ct);
            await db.Entry(tenant).ReloadAsync(ct);

            if (row.ProvisioningAttempts >= 3)
            {
                tenant.LifecycleState = LifecycleState.ProvisioningFailed;
                tenant.StateChangedAt = clockLocal.UtcNow;
                tenant.LastError = ex.Message;
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Ids.New(),
                    TenantId = tenant.Id,
                    Kind = "lifecycle",
                    FromState = nameof(LifecycleState.Provisioning),
                    ToState = nameof(LifecycleState.ProvisioningFailed),
                    At = clockLocal.UtcNow,
                    Actor = "provisioning_job",
                    Reason = ex.Message
                });
                await db.SaveChangesAsync(ct);
            }
        }
    }
}