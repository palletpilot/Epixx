using System.Security.Claims;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Devices;

public static class DevicesApi
{
    public static WebApplication MapDevicesApi(this WebApplication app)
    {
        var devices = app.MapGroup("/devices").RequireAuthorization();
        devices.MapPost("/enrollment-codes", CreateEnrollmentCode);
        devices.MapGet("/", ListDevices);
        devices.MapPost("/{id:guid}/revoke", RevokeDevice);
        devices.MapDelete("/{id:guid}", RemoveDevice);

        app.MapPost("/devices/enroll", Enroll); // public â€” device has no JWT yet
        return app;
    }

    private static async Task<IResult> CreateEnrollmentCode(
        [FromBody] CreateEnrollmentCodeRequest? body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        if (!await CanEnrollAsync(db, userId, tenantId, ct))
        {
            return Results.Forbid();
        }

        // Invalidate unused prior codes for this tenant (regenerable).
        var open = await db.EnrollmentCodes
            .Where(c => c.TenantId == tenantId && c.UsedAt == null && c.ExpiresAt > clock.UtcNow)
            .ToListAsync(ct);
        foreach (var row in open)
        {
            row.ExpiresAt = clock.UtcNow;
        }

        var code = EnrollmentCodeHasher.CreateCode();
        db.EnrollmentCodes.Add(new EnrollmentCode
        {
            Id = Ids.New(),
            TenantId = tenantId,
            CodeHash = EnrollmentCodeHasher.Hash(code),
            CreatedBy = userId,
            WarehouseIds = body?.WarehouseIds ?? [],
            ExpiresAt = clock.UtcNow.AddMinutes(15)
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new EnrollmentCodeResponse(code, clock.UtcNow.AddMinutes(15)));
    }

    private static async Task<IResult> Enroll(
        [FromBody] EnrollDeviceRequest body,
        PlatformDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Code))
        {
            return Results.BadRequest(new { error = "invalid_code" });
        }

        var hash = EnrollmentCodeHasher.Hash(body.Code);
        var enrollment = await db.EnrollmentCodes
            .Where(c => c.CodeHash == hash)
            .OrderByDescending(c => c.ExpiresAt)
            .FirstOrDefaultAsync(ct);
        if (enrollment is null || enrollment.UsedAt is not null || enrollment.ExpiresAt <= clock.UtcNow)
        {
            return Results.BadRequest(new { error = "invalid_or_expired_code" });
        }

        if (body.DeviceId is Guid existingId)
        {
            var existing = await db.Devices.FirstOrDefaultAsync(d => d.Id == existingId, ct);
            if (existing is not null)
            {
                if (existing.RevokedAt is not null)
                {
                    return Results.StatusCode(StatusCodes.Status410Gone);
                }

                return Results.Conflict(new { error = "device_already_enrolled" });
            }
        }

        var secret = DeviceSecretHasher.CreateSecret();
        var device = new Device
        {
            Id = body.DeviceId ?? Ids.New(),
            TenantId = enrollment.TenantId,
            Name = string.IsNullOrWhiteSpace(body.Name) ? "Device" : body.Name.Trim(),
            WarehouseIds = enrollment.WarehouseIds,
            SecretHash = DeviceSecretHasher.Hash(secret),
            EnrolledBy = enrollment.CreatedBy
        };
        db.Devices.Add(device);
        enrollment.UsedAt = clock.UtcNow;
        enrollment.DeviceId = device.Id;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new EnrollDeviceResponse(device.Id, secret, device.WarehouseIds, device.TenantId));
    }

    private static async Task<IResult> ListDevices(
        ClaimsPrincipal principal,
        PlatformDbContext db,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        if (!await CanManageAsync(db, userId, tenantId, ct))
        {
            return Results.Forbid();
        }

        var rows = await db.Devices.AsNoTracking()
            .Where(d => d.TenantId == tenantId)
            .OrderBy(d => d.Name)
            .Select(d => new DeviceListItem(
                d.Id, d.Name, d.WarehouseIds, d.RevokedAt, d.LastSyncAt, d.PendingCount, d.OldestPendingOccurredAt))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> RevokeDevice(
        Guid id,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        SessionVersionBump bump,
        IClock clock,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        if (!await CanManageAsync(db, userId, tenantId, ct))
        {
            return Results.Forbid();
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        if (device.RevokedAt is null)
        {
            device.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await bump.BumpForDeviceAsync(device.Id, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveDevice(
        Guid id,
        [FromBody] RemoveDeviceRequest? body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        if (!await CanManageAsync(db, userId, tenantId, ct))
        {
            return Results.Forbid();
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        if (device.PendingCount > 0 && body?.ConfirmLoss != true)
        {
            return Results.Conflict(new { error = "pending_commands", pending_count = device.PendingCount });
        }

        var sessions = await db.DeviceSessions.Where(s => s.DeviceId == device.Id).ToListAsync(ct);
        db.DeviceSessions.RemoveRange(sessions);
        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<bool> CanEnrollAsync(PlatformDbContext db, Guid userId, Guid tenantId, CancellationToken ct)
    {
        var roles = await ActiveRoles(db, userId, tenantId, ct);
        return roles.Contains(Permissions.TenantAdmin) || roles.Contains(Permissions.WarehouseManager);
    }

    private static Task<bool> CanManageAsync(PlatformDbContext db, Guid userId, Guid tenantId, CancellationToken ct) =>
        CanEnrollAsync(db, userId, tenantId, ct);

    private static async Task<HashSet<string>> ActiveRoles(
        PlatformDbContext db, Guid userId, Guid tenantId, CancellationToken ct)
    {
        var list = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.Membership.UserId == userId
                        && a.Membership.TenantId == tenantId
                        && a.ValidTo == null)
            .Select(a => a.Role.InternalName)
            .ToListAsync(ct);
        return list.ToHashSet();
    }

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
