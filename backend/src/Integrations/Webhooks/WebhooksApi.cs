using System.Security.Claims;
using System.Security.Cryptography;
using Lagerkraft.Integrations.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Integrations.Webhooks;

public static class WebhooksApi
{
    public static WebApplication MapWebhooksApi(this WebApplication app)
    {
        var group = app.MapGroup("/webhooks")
            .RequireAuthorization()
            .RequirePermission(Permissions.IntegrationsManage);
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}", Get);
        group.MapPatch("/{id:guid}", Patch);
        group.MapDelete("/{id:guid}", Delete);
        return app;
    }

    private static async Task<IResult> List(ClaimsPrincipal principal, IntegrationsDbContext db, CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var rows = await db.WebhookEndpoints.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Id)
            .Select(e => new WebhookEndpointResponse(e.Id, e.Url, e.Events, e.Active))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Get(Guid id, ClaimsPrincipal principal, IntegrationsDbContext db, CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var row = await db.WebhookEndpoints.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        return row is null
            ? Results.NotFound()
            : Results.Ok(new WebhookEndpointResponse(row.Id, row.Url, row.Events, row.Active));
    }

    private static async Task<IResult> Create(
        [FromBody] UpsertWebhookRequest body,
        ClaimsPrincipal principal,
        IntegrationsDbContext db,
        CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(body.Url) || body.Events is not { Length: > 0 })
        {
            return Results.BadRequest();
        }

        var row = new WebhookEndpoint
        {
            Id = Ids.New(),
            TenantId = tenantId,
            Url = body.Url,
            Secret = string.IsNullOrWhiteSpace(body.Secret) ? NewSecret() : body.Secret,
            Events = body.Events,
            Active = body.Active ?? true
        };
        db.WebhookEndpoints.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new WebhookEndpointCreatedResponse(row.Id, row.Url, row.Events, row.Active, row.Secret));
    }

    private static async Task<IResult> Patch(
        Guid id,
        [FromBody] UpsertWebhookRequest body,
        ClaimsPrincipal principal,
        IntegrationsDbContext db,
        CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var row = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(body.Url))
        {
            row.Url = body.Url;
        }

        if (body.Events is { Length: > 0 })
        {
            row.Events = body.Events;
        }

        if (body.Active is { } active)
        {
            row.Active = active;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new WebhookEndpointResponse(row.Id, row.Url, row.Events, row.Active));
    }

    private static async Task<IResult> Delete(Guid id, ClaimsPrincipal principal, IntegrationsDbContext db, CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var row = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        db.WebhookEndpoints.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static bool TryTenant(ClaimsPrincipal principal, out Guid tenantId)
    {
        tenantId = default;
        var tid = principal.FindFirst(LagerkraftClaims.TenantId)?.Value?.Trim().Trim('"');
        return Guid.TryParse(tid, out tenantId);
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}

public sealed record UpsertWebhookRequest(string? Url, string[]? Events, string? Secret, bool? Active);
public sealed record WebhookEndpointResponse(Guid Id, string Url, string[] Events, bool Active);
public sealed record WebhookEndpointCreatedResponse(Guid Id, string Url, string[] Events, bool Active, string Secret);
