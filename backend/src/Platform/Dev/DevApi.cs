using System.Text.Json.Serialization;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Dev;

/// <summary>
/// Throwaway Dev/Testing-only helpers for the SP0 ugly floor shell.
/// MUST NOT be mapped in Production - gated in <see cref="MapDevApi"/>.
/// Real floor auth remains <c>POST /auth/pin-unlock</c>.
/// </summary>
public static class DevApi
{
    public static WebApplication MapDevApi(this WebApplication app)
    {
        var env = app.Environment;
        if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
        {
            return app;
        }

        var group = app.MapGroup("/dev");
        // Dev-only: keep out of contracts/openapi/platform.json even if OpenAPI runs as Development.
        group.MapPost("/mint-floor-token", MintFloorToken)
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> MintFloorToken(
        [FromBody] MintFloorTokenRequest body,
        PlatformDbContext db,
        JwtIssuer jwt,
        RefreshTokenStore refresh,
        IClock clock,
        CancellationToken ct)
    {
        if (body.UserId == Guid.Empty || body.TenantId == Guid.Empty || body.DeviceId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "user_id_tenant_id_device_id_required" });
        }

        var membership = await db.Memberships
            .Include(m => m.Tenant)
            .Include(m => m.RoleAssignments).ThenInclude(a => a.Role)
            .FirstOrDefaultAsync(m => m.UserId == body.UserId && m.TenantId == body.TenantId, ct);
        if (membership is null)
        {
            return Results.NotFound(new { error = "membership_not_found" });
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == body.DeviceId, ct);
        if (device is null)
        {
            device = new Device
            {
                Id = body.DeviceId,
                TenantId = body.TenantId,
                Name = body.DeviceName ?? "dev-shell",
                EnrolledBy = body.UserId
            };
            db.Devices.Add(device);
        }
        else if (device.TenantId != body.TenantId)
        {
            return Results.BadRequest(new { error = "device_tenant_mismatch" });
        }
        else if (device.RevokedAt is not null)
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        if (!await db.DeviceSessions.AnyAsync(
                s => s.DeviceId == device.Id && s.MembershipId == membership.Id, ct))
        {
            db.DeviceSessions.Add(new DeviceSession
            {
                Id = Ids.New(),
                DeviceId = device.Id,
                MembershipId = membership.Id,
                CreatedAt = clock.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
        var tokens = await AuthApi.IssueAsync(jwt, refresh, membership, "pin", device.Id, ct);
        return Results.Ok(tokens);
    }
}

public sealed record MintFloorTokenRequest(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("device_name")] string? DeviceName);
