using System.Security.Claims;
using System.Text.Json.Serialization;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Lifecycle;

public static class LifecycleApi
{
    public static WebApplication MapLifecycleApi(this WebApplication app)
    {
        var group = app.MapGroup("/lifecycle").RequireAuthorization();
        group.MapPost("/convert-trial", ConvertTrial);
        group.MapPost("/maintenance", SetMaintenance);
        return app;
    }

    private static async Task<IResult> ConvertTrial(
        [FromBody] ConvertTrialRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        TenantStateMachine machine,
        IClock clock,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId, ct);
        if (membership is null || !membership.IsOwner)
        {
            return Results.Forbid();
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        if (tenant.LifecycleState is not (LifecycleState.Trialing or LifecycleState.TrialExpired))
        {
            return Results.Conflict(new { error = "not_on_trial", state = tenant.LifecycleState.ToString() });
        }

        var planCode = string.IsNullOrWhiteSpace(body.PlanCode) ? "start" : body.PlanCode.Trim();
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == planCode && p.Active, ct);
        if (plan is null)
        {
            return Results.BadRequest(new { error = "unknown_plan" });
        }

        var billing = await db.BillingAccounts.FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);
        if (billing is null)
        {
            billing = new BillingAccount { Id = Ids.New(), TenantId = tenantId };
            db.BillingAccounts.Add(billing);
        }

        billing.LegalName = NullIfWhite(body.LegalName) ?? billing.LegalName;
        billing.OrgNumber = NullIfWhite(body.OrgNumber) ?? billing.OrgNumber;
        billing.BillingEmail = NullIfWhite(body.BillingEmail) ?? billing.BillingEmail;
        billing.AddressLine1 = NullIfWhite(body.AddressLine1) ?? billing.AddressLine1;
        billing.PostalCode = NullIfWhite(body.PostalCode) ?? billing.PostalCode;
        billing.City = NullIfWhite(body.City) ?? billing.City;
        billing.Country = NullIfWhite(body.Country) ?? billing.Country ?? "SE";
        billing.VatNumber = NullIfWhite(body.VatNumber) ?? billing.VatNumber;
        billing.UpdatedAt = clock.UtcNow;
        if (billing.CreatedAt == default)
        {
            billing.CreatedAt = clock.UtcNow;
        }

        var sub = await db.Subscriptions
            .Where(s => s.TenantId == tenantId && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (sub is null)
        {
            sub = new Subscription
            {
                Id = Ids.New(),
                TenantId = tenantId,
                StartedAt = clock.UtcNow
            };
            db.Subscriptions.Add(sub);
        }

        sub.PlanId = plan.Id;
        sub.Status = "active";
        sub.BillingInterval = string.IsNullOrWhiteSpace(body.BillingInterval) ? "monthly" : body.BillingInterval!;
        sub.CurrentPeriodStart = clock.UtcNow;
        sub.CurrentPeriodEnd = clock.UtcNow.AddMonths(1);
        sub.TrialEndsAt = null;

        await db.SaveChangesAsync(ct);
        await machine.TransitionAsync(
            tenant,
            LifecycleState.Active,
            actor: userId.ToString(),
            reason: "convert_trial",
            detail: new { plan = plan.Code },
            ct: ct);

        return Results.Ok(new { lifecycle_state = tenant.LifecycleState.ToString(), plan = plan.Code });
    }

    private static async Task<IResult> SetMaintenance(
        [FromBody] SetMaintenanceRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        TenantStateMachine machine,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var isOwner = await db.Memberships.AsNoTracking()
            .AnyAsync(m => m.UserId == userId && m.TenantId == tenantId && m.IsOwner, ct);
        if (!isOwner)
        {
            // Founder/ops path for SP0: also allow tenant_admin
            var admin = await db.RoleAssignments.AsNoTracking()
                .AnyAsync(
                    a => a.Membership.UserId == userId
                         && a.Membership.TenantId == tenantId
                         && a.ValidTo == null
                         && a.Role.InternalName == Permissions.TenantAdmin,
                    ct);
            if (!admin)
            {
                return Results.Forbid();
            }
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        await machine.SetMaintenanceAsync(
            tenant,
            body.Maintenance,
            actor: userId.ToString(),
            reason: body.Reason,
            ct: ct);
        return Results.NoContent();
    }

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryUserAndTenant(ClaimsPrincipal principal, out Guid userId, out Guid tenantId)
    {
        userId = default;
        tenantId = default;
        var sub = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tid = principal.FindFirstValue(LagerkraftClaims.TenantId);
        sub = sub?.Trim('"');
        tid = tid?.Trim('"');
        return Guid.TryParse(sub, out userId) && Guid.TryParse(tid, out tenantId);
    }
}

public sealed record ConvertTrialRequest(
    [property: JsonPropertyName("plan_code")] string? PlanCode,
    [property: JsonPropertyName("billing_interval")] string? BillingInterval,
    [property: JsonPropertyName("legal_name")] string? LegalName,
    [property: JsonPropertyName("org_number")] string? OrgNumber,
    [property: JsonPropertyName("billing_email")] string? BillingEmail,
    [property: JsonPropertyName("address_line1")] string? AddressLine1,
    [property: JsonPropertyName("postal_code")] string? PostalCode,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("vat_number")] string? VatNumber);

public sealed record SetMaintenanceRequest(
    [property: JsonPropertyName("maintenance")] bool Maintenance,
    [property: JsonPropertyName("reason")] string? Reason);
