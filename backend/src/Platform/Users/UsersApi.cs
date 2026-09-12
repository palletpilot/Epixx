using System.Security.Claims;
using System.Text.Json.Serialization;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Email;
using Lagerkraft.Platform.Signup;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using RoleAssignmentEntity = Lagerkraft.Platform.Data.RoleAssignment;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Users;

public static class UsersApi
{
    private static readonly string[] RoleRank =
    [
        Permissions.Viewer,
        Permissions.FloorWorker,
        Permissions.WarehouseManager,
        Permissions.TenantAdmin
    ];

    public static WebApplication MapUsersApi(this WebApplication app)
    {
        var users = app.MapGroup("/users").RequireAuthorization();
        users.MapGet("/", ListUsers);
        users.MapPost("/invitations", CreateInvitation);
        users.MapPost("/{userId:guid}/roles", AssignRole);

        app.MapPost("/users/invitations/accept", AcceptInvitation); // public
        return app;
    }

    private static async Task<IResult> CreateInvitation(
        [FromBody] CreateInvitationRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        IEmailSender email,
        IConfiguration configuration,
        IClock clock,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var actorRoles = await ActiveRoleNames(db, userId, tenantId, ct);
        if (!actorRoles.Contains(Permissions.TenantAdmin) && !actorRoles.Contains(Permissions.WarehouseManager))
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Role))
        {
            return Results.BadRequest(new { error = "email_and_role_required" });
        }

        var roleName = body.Role.Trim().ToLowerInvariant();
        if (!RoleRank.Contains(roleName))
        {
            return Results.BadRequest(new { error = "unknown_role" });
        }

        if (!CanGrant(actorRoles, roleName))
        {
            return Results.Forbid();
        }

        var role = await db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == null && r.InternalName == roleName, ct);
        if (role is null)
        {
            return Results.BadRequest(new { error = "unknown_role" });
        }

        var normalized = body.Email.Trim().ToLowerInvariant();
        var alreadyMember = await db.Memberships.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenantId && m.User.Email == normalized, ct);
        if (alreadyMember)
        {
            return Results.Conflict(new { error = "already_member" });
        }

        var token = TokenHash.CreateToken();
        var invitation = new Invitation
        {
            Id = Ids.New(),
            TenantId = tenantId,
            Email = normalized,
            RoleId = role.Id,
            WarehouseIds = body.WarehouseIds ?? [],
            TokenHash = TokenHash.Hash(token),
            InvitedBy = userId,
            ExpiresAt = clock.UtcNow.AddDays(7)
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        var publicBase = (configuration["PublicBaseUrl"] ?? "http://localhost:5100").TrimEnd('/');
        await email.SendAsync(
            normalized,
            "You're invited to Lagerkraft",
            $"Accept your invitation: {publicBase}/accept-invite?token={token}",
            ct);

        return Results.Ok(new InvitationCreatedResponse(invitation.Id, invitation.ExpiresAt, token));
    }

    private static async Task<IResult> AcceptInvitation(
        [FromBody] AcceptInvitationRequest body,
        PlatformDbContext db,
        UserManager<AppUser> users,
        IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Token))
        {
            return Results.BadRequest(new { error = "invalid_token" });
        }

        var hash = TokenHash.Hash(body.Token);
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.ExpiresAt <= clock.UtcNow)
        {
            return Results.BadRequest(new { error = "invalid_or_expired_invitation" });
        }

        var role = await db.Roles.AsNoTracking().FirstAsync(r => r.Id == invitation.RoleId, ct);
        var email = invitation.Email;
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(body.Password) || body.Password.Length < 8)
            {
                return Results.BadRequest(new { error = "password_required" });
            }

            user = new AppUser
            {
                Id = Ids.New(),
                Email = email,
                UserName = email,
                EmailConfirmed = true
            };
            var create = await users.CreateAsync(user, body.Password);
            if (!create.Succeeded)
            {
                return Results.BadRequest(new
                {
                    error = "user_create_failed",
                    details = create.Errors.Select(e => e.Code).ToArray()
                });
            }
        }

        if (await db.Memberships.AnyAsync(m => m.UserId == user.Id && m.TenantId == invitation.TenantId, ct))
        {
            invitation.AcceptedAt = clock.UtcNow;
            invitation.AcceptedUserId = user.Id;
            await db.SaveChangesAsync(ct);
            return Results.Conflict(new { error = "already_member" });
        }

        var membership = new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = invitation.TenantId,
            IsOwner = false
        };
        db.Memberships.Add(membership);

        if (invitation.WarehouseIds is { Length: > 0 })
        {
            foreach (var warehouseId in invitation.WarehouseIds)
            {
                db.RoleAssignments.Add(new RoleAssignmentEntity
                {
                    Id = Ids.New(),
                    MembershipId = membership.Id,
                    RoleId = role.Id,
                    WarehouseId = warehouseId,
                    ValidFrom = clock.UtcNow
                });
            }
        }
        else
        {
            db.RoleAssignments.Add(new RoleAssignmentEntity
            {
                Id = Ids.New(),
                MembershipId = membership.Id,
                RoleId = role.Id,
                WarehouseId = null,
                ValidFrom = clock.UtcNow
            });
        }

        invitation.AcceptedAt = clock.UtcNow;
        invitation.AcceptedUserId = user.Id;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AcceptInvitationResponse(user.Id, invitation.TenantId, role.InternalName));
    }

    private static async Task<IResult> ListUsers(
        ClaimsPrincipal principal,
        PlatformDbContext db,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var userId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var actorRoles = await ActiveRoleNames(db, userId, tenantId, ct);
        if (!actorRoles.Contains(Permissions.TenantAdmin) && !actorRoles.Contains(Permissions.WarehouseManager))
        {
            return Results.Forbid();
        }

        var rows = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Select(m => new UserListItem(
                m.UserId,
                m.User.Email ?? "",
                m.IsOwner,
                m.RoleAssignments
                    .Where(a => a.ValidTo == null)
                    .Select(a => new UserRoleItem(a.Role.InternalName, a.WarehouseId))
                    .ToArray()))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> AssignRole(
        Guid userId,
        [FromBody] AssignRoleRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        SessionVersionBump bump,
        IClock clock,
        CancellationToken ct)
    {
        if (!TryUserAndTenant(principal, out var actorId, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var actorRoles = await ActiveRoleNames(db, actorId, tenantId, ct);
        if (!actorRoles.Contains(Permissions.TenantAdmin) && !actorRoles.Contains(Permissions.WarehouseManager))
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(body.Role))
        {
            return Results.BadRequest(new { error = "role_required" });
        }

        var roleName = body.Role.Trim().ToLowerInvariant();
        if (!RoleRank.Contains(roleName) || !CanGrant(actorRoles, roleName))
        {
            return Results.Forbid();
        }

        var role = await db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == null && r.InternalName == roleName, ct);
        if (role is null)
        {
            return Results.BadRequest(new { error = "unknown_role" });
        }

        var membership = await db.Memberships
            .Include(m => m.RoleAssignments)
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId, ct);
        if (membership is null)
        {
            return Results.NotFound();
        }

        var warehouseId = body.WarehouseId;
        var open = membership.RoleAssignments
            .Where(a => a.ValidTo == null && a.WarehouseId == warehouseId)
            .ToList();
        foreach (var row in open)
        {
            row.ValidTo = clock.UtcNow;
        }

        db.RoleAssignments.Add(new RoleAssignmentEntity
        {
            Id = Ids.New(),
            MembershipId = membership.Id,
            RoleId = role.Id,
            WarehouseId = warehouseId,
            ValidFrom = clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await bump.BumpForUserAsync(userId, tenantId, ct);
        return Results.NoContent();
    }

    private static bool CanGrant(HashSet<string> actorRoles, string targetRole)
    {
        var actorRank = actorRoles.Select(RankOf).DefaultIfEmpty(-1).Max();
        return RankOf(targetRole) <= actorRank;
    }

    private static int RankOf(string role)
    {
        var i = Array.IndexOf(RoleRank, role);
        return i < 0 ? -1 : i;
    }

    private static async Task<HashSet<string>> ActiveRoleNames(
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

public sealed record CreateInvitationRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("warehouse_ids")] Guid[]? WarehouseIds);

public sealed record InvitationCreatedResponse(
    [property: JsonPropertyName("invitation_id")] Guid InvitationId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("token")] string Token);

public sealed record AcceptInvitationRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("password")] string? Password);

public sealed record AcceptInvitationResponse(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("role")] string Role);

public sealed record AssignRoleRequest(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("warehouse_id")] Guid? WarehouseId);

public sealed record UserListItem(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("is_owner")] bool IsOwner,
    [property: JsonPropertyName("roles")] UserRoleItem[] Roles);

public sealed record UserRoleItem(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("warehouse_id")] Guid? WarehouseId);
