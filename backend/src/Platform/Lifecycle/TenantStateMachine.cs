using System.Text.Json;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Tenancy;

namespace Lagerkraft.Platform.Lifecycle;

public sealed class TenantStateMachine(
    PlatformDbContext db,
    IPlatformEventPublisher events,
    IClock clock)
{
    public static readonly (LifecycleState From, LifecycleState To)[] Sp0Transitions =
    [
        (LifecycleState.Provisioning, LifecycleState.Trialing),
        (LifecycleState.Provisioning, LifecycleState.ProvisioningFailed),
        (LifecycleState.ProvisioningFailed, LifecycleState.Provisioning),
        (LifecycleState.Trialing, LifecycleState.Active),
        (LifecycleState.Trialing, LifecycleState.TrialExpired),
        (LifecycleState.TrialExpired, LifecycleState.Active),
    ];

    /// <summary>Full spec diagram; anything here but not in Sp0Transitions throws NotImplemented.</summary>
    public static readonly (LifecycleState From, LifecycleState To)[] SpecTransitions =
    [
        ..Sp0Transitions,
        (LifecycleState.TrialExpired, LifecycleState.Offboarding),
        (LifecycleState.Active, LifecycleState.PastDue),
        (LifecycleState.PastDue, LifecycleState.Active),
        (LifecycleState.PastDue, LifecycleState.Restricted),
        (LifecycleState.Restricted, LifecycleState.Active),
        (LifecycleState.Restricted, LifecycleState.Suspended),
        (LifecycleState.Suspended, LifecycleState.Active),
        (LifecycleState.Suspended, LifecycleState.Offboarding),
        (LifecycleState.Active, LifecycleState.CancelPending),
        (LifecycleState.CancelPending, LifecycleState.Active),
        (LifecycleState.CancelPending, LifecycleState.Offboarding),
        (LifecycleState.Active, LifecycleState.Offboarding),
        (LifecycleState.Offboarding, LifecycleState.Active),
        (LifecycleState.Offboarding, LifecycleState.Deleting),
        (LifecycleState.Deleting, LifecycleState.Deleted),
    ];

    public static bool IsAllowed(LifecycleState from, LifecycleState to) =>
        Sp0Transitions.Any(t => t.From == from && t.To == to);

    public static void EnsureAllowed(LifecycleState from, LifecycleState to)
    {
        if (IsAllowed(from, to))
        {
            return;
        }

        if (SpecTransitions.Any(t => t.From == from && t.To == to))
        {
            throw new NotImplementedException(
                $"Lifecycle transition {from} -> {to} is defined in the spec but not wired in SP0.");
        }

        throw new InvalidOperationException($"Unknown or forbidden lifecycle transition {from} -> {to}.");
    }

    public async Task TransitionAsync(
        Tenant tenant,
        LifecycleState to,
        string actor,
        string? reason = null,
        object? detail = null,
        CancellationToken ct = default)
    {
        var from = tenant.LifecycleState;
        if (from == to)
        {
            return;
        }

        EnsureAllowed(from, to);
        tenant.LifecycleState = to;
        tenant.StateChangedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            Kind = "state_transition",
            FromState = from.ToString(),
            ToState = to.ToString(),
            Reason = reason,
            Actor = actor,
            At = clock.UtcNow,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail)
        });
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(
            tenant.Id,
            "tenant",
            "state_changed",
            new
            {
                tenant_id = tenant.Id,
                from = from.ToString(),
                to = to.ToString(),
                maintenance = tenant.Maintenance,
                at = clock.UtcNow,
                reason
            },
            ct);
    }

    /// <summary>Initial Provisioning row (no from-state). Idempotent if already Provisioning.</summary>
    public async Task EnterProvisioningAsync(Tenant tenant, string actor, CancellationToken ct = default)
    {
        if (tenant.LifecycleState != LifecycleState.Provisioning)
        {
            await TransitionAsync(tenant, LifecycleState.Provisioning, actor, ct: ct);
            return;
        }

        tenant.StateChangedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            Kind = "state_transition",
            ToState = nameof(LifecycleState.Provisioning),
            Actor = actor,
            At = clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(
            tenant.Id,
            "tenant",
            "state_changed",
            new
            {
                tenant_id = tenant.Id,
                from = (string?)null,
                to = nameof(LifecycleState.Provisioning),
                maintenance = tenant.Maintenance,
                at = clock.UtcNow
            },
            ct);
    }

    public async Task SetMaintenanceAsync(
        Tenant tenant,
        bool maintenance,
        string actor,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (tenant.Maintenance == maintenance)
        {
            return;
        }

        tenant.Maintenance = maintenance;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            Kind = "maintenance",
            Reason = reason,
            Actor = actor,
            At = clock.UtcNow,
            Detail = JsonSerializer.Serialize(new { maintenance })
        });
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(
            tenant.Id,
            "tenant",
            "state_changed",
            new
            {
                tenant_id = tenant.Id,
                lifecycle_state = tenant.LifecycleState.ToString(),
                maintenance,
                at = clock.UtcNow,
                reason
            },
            ct);
    }
}
