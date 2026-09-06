using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Internal;

public static class InternalApi
{
    public static WebApplication MapInternalApi(this WebApplication app)
    {
        var group = app.MapGroup("/internal").AddEndpointFilter<InternalTokenFilter>();

        group.MapGet("/tenants/{id:guid}", GetTenant);
        group.MapGet("/tenants/{id:guid}/connection", GetConnection);
        group.MapGet("/tenants/{id:guid}/entitlement", GetEntitlement);
        group.MapPut("/tenants/{id:guid}/migration-status", PutMigrationStatus);
        group.MapGet("/memberships/{tenantId:guid}/history", GetMembershipHistory);
        return app;
    }

    private static async Task<IResult> GetTenant(Guid id, TenantCatalog catalog, CancellationToken ct)
    {
        var tenant = await catalog.FindAsync(id, ct);
        return tenant is null ? Results.NotFound() : Results.Ok(TenantCatalogResponse.From(tenant));
    }

    private static async Task<IResult> GetConnection(Guid id, TenantCatalog catalog, CancellationToken ct)
    {
        var connection = await catalog.GetConnectionAsync(id, ct);
        return connection is null
            ? Results.NotFound()
            : Results.Ok(new TenantConnectionResponse(connection));
    }

    private static async Task<IResult> GetEntitlement(
        Guid id,
        PlatformDbContext db,
        CancellationToken ct)
    {
        var row = await db.Subscriptions
            .AsNoTracking()
            .Where(s => s.TenantId == id && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new { s.Tenant, s.Plan })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        using var features = JsonDocument.Parse(row.Plan.Features);
        return Results.Ok(new EntitlementResponse(
            row.Tenant.Id,
            row.Tenant.LifecycleState.ToString(),
            row.Tenant.Maintenance,
            row.Plan.Code,
            row.Plan.HardCapUnits,
            features.RootElement.Clone()));
    }

    private static async Task<IResult> PutMigrationStatus(
        Guid id,
        MigrationStatusRequest body,
        TenantCatalog catalog,
        CancellationToken ct)
    {
        var updated = await catalog.UpdateMigrationStatusAsync(
            id,
            body.SchemaVersion,
            body.MigrationStatus,
            body.LastError,
            ct);
        return updated ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetMembershipHistory(
        Guid tenantId,
        Guid? userId,
        PlatformDbContext db,
        CancellationToken ct)
    {
        var query = db.RoleAssignments
            .AsNoTracking()
            .Where(a => a.Membership.TenantId == tenantId);
        if (userId is { } uid)
        {
            query = query.Where(a => a.Membership.UserId == uid);
        }

        var rows = await query
            .OrderBy(a => a.ValidFrom)
            .Select(a => new MembershipHistoryResponse(
                a.Id,
                a.Membership.UserId,
                a.Role.InternalName,
                a.WarehouseId,
                a.ValidFrom,
                a.ValidTo))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }
}

public sealed class InternalTokenFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = configuration["Internal:Token"] ?? "";
        var actual = context.HttpContext.Request.Headers["Lagerkraft-Internal-Token"].ToString();
        if (expected.Length == 0 || !FixedEquals(actual, expected))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static bool FixedEquals(string actual, string expected)
    {
        var a = Encoding.UTF8.GetBytes(actual);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed record TenantCatalogResponse(
    Guid Id,
    string Slug,
    string CompanyName,
    string LifecycleState,
    bool Maintenance,
    string? SchemaVersion,
    string MigrationStatus,
    string? LastError,
    DateTimeOffset? LastAttemptAt)
{
    public static TenantCatalogResponse From(Tenant tenant) => new(
        tenant.Id,
        tenant.Slug,
        tenant.CompanyName,
        tenant.LifecycleState.ToString(),
        tenant.Maintenance,
        tenant.SchemaVersion,
        tenant.MigrationStatus,
        tenant.LastError,
        tenant.LastAttemptAt);
}

public sealed record TenantConnectionResponse(string ConnectionString);

public sealed record EntitlementResponse(
    Guid TenantId,
    string LifecycleState,
    bool Maintenance,
    string PlanCode,
    int? HardCapUnits,
    JsonElement Features);

public sealed record MigrationStatusRequest(string? SchemaVersion, string MigrationStatus, string? LastError);

public sealed record MembershipHistoryResponse(
    Guid Id,
    Guid UserId,
    string Role,
    Guid? WarehouseId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);
