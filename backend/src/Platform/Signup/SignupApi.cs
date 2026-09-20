using System.Security.Claims;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Email;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Tenancy;
using Lagerkraft.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Signup;

public static class SignupApi
{
    public static WebApplication MapSignupApi(this WebApplication app)
    {
        var group = app.MapGroup("/signup");
        group.MapPost("/", RequestSignup);
        group.MapPost("/verify", Verify);
        group.MapPost("/join-requests/approve", ApproveJoin).RequireAuthorization();
        group.MapGet("/slug-available", SlugAvailable);
        return app;
    }

    private static async Task<IResult> RequestSignup(
        [FromBody] SignupRequestBody body,
        PlatformDbContext db,
        IPasswordHasher<AppUser> hasher,
        IEmailSender email,
        IClock clock,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.CompanyName)
            || string.IsNullOrWhiteSpace(body.Email)
            || string.IsNullOrWhiteSpace(body.DisplayName)
            || string.IsNullOrWhiteSpace(body.Password))
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }

        if (!OrgNumber.IsValid(body.OrgNumber))
        {
            return Results.BadRequest(new { error = "invalid_org_number" });
        }

        var org = OrgNumber.Normalize(body.OrgNumber);
        var emailAddr = body.Email.Trim().ToLowerInvariant();
        if (DisposableDomains.IsBlocked(emailAddr))
        {
            return Results.BadRequest(new { error = "disposable_email" });
        }

        var live = await db.Tenants.AsNoTracking()
            .Where(t => t.LifecycleState != LifecycleState.Deleted)
            .Join(db.BillingAccounts, t => t.Id, b => b.TenantId, (t, b) => new { t, b })
            .Where(x => x.b.OrgNumber == org)
            .Select(x => new { x.t.Id, x.t.CompanyName })
            .FirstOrDefaultAsync(ct);

        // Also treat an existing tenant with matching company via billing; org is on BillingAccount.
        // During provisioning org is written to BillingAccount; before that check SignupRequest.
        if (live is null)
        {
            // Tenants may exist without billing yet (test seeds). Match via BillingAccount only per uniqueness rule.
        }

        if (live is not null)
        {
            if (!body.JoinRequest)
            {
                return Results.Conflict(new JoinRequestResponse(Guid.Empty, live.CompanyName));
            }

            var joinToken = TokenHash.CreateToken();
            var join = new SignupRequest
            {
                Id = Ids.New(),
                Email = emailAddr,
                OrgNumber = org,
                CompanyName = live.CompanyName,
                Slug = "",
                DisplayName = body.DisplayName.Trim(),
                PasswordHash = hasher.HashPassword(new AppUser(), body.Password),
                VerificationTokenHash = TokenHash.Hash(joinToken),
                CreatedAt = clock.UtcNow,
                ExpiresAt = clock.UtcNow.AddHours(24),
                JoinRequestForTenantId = live.Id
            };
            db.SignupRequests.Add(join);
            await db.SaveChangesAsync(ct);

            var owners = await db.Memberships.AsNoTracking()
                .Where(m => m.TenantId == live.Id && m.IsOwner)
                .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Email)
                .Where(e => e != null)
                .ToListAsync(ct);
            foreach (var ownerEmail in owners.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                await email.SendAsync(
                    ownerEmail!,
                    "Join request",
                    $"A user requested to join {live.CompanyName}. Signup id: {join.Id}",
                    ct);
            }

            return Results.Ok(new JoinRequestResponse(join.Id, live.CompanyName));
        }

        var baseSlug = string.IsNullOrWhiteSpace(body.Slug)
            ? SlugProposer.Propose(body.CompanyName)
            : body.Slug.Trim().ToLowerInvariant();
        if (!Slug.TryCreate(baseSlug, out var slug))
        {
            return Results.BadRequest(new { error = "invalid_slug" });
        }

        var uniqueSlug = slug.Value;
        for (var i = 0; i < 20; i++)
        {
            var candidate = i == 0 ? uniqueSlug : SlugProposer.WithSuffix(uniqueSlug, i);
            var taken = await db.Tenants.AnyAsync(t => t.Slug == candidate, ct)
                || await db.SignupRequests.AnyAsync(s => s.Slug == candidate && s.JoinRequestForTenantId == null, ct);
            if (!taken)
            {
                uniqueSlug = candidate;
                break;
            }

            if (i == 19)
            {
                return Results.Conflict(new { error = "slug_unavailable" });
            }
        }

        var token = TokenHash.CreateToken();
        var request = new SignupRequest
        {
            Id = Ids.New(),
            Email = emailAddr,
            OrgNumber = org,
            CompanyName = body.CompanyName.Trim(),
            Slug = uniqueSlug,
            DisplayName = body.DisplayName.Trim(),
            PasswordHash = hasher.HashPassword(new AppUser(), body.Password),
            VerificationTokenHash = TokenHash.Hash(token),
            CreatedAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow.AddHours(24)
        };
        db.SignupRequests.Add(request);
        await db.SaveChangesAsync(ct);

        var publicBase = configuration["FrontendPublicUrl"] ?? "http://localhost:5173";
        var link = $"{publicBase.TrimEnd('/')}/verify?token={token}";
        await email.SendAsync(
            emailAddr,
            "Verify your Lagerkraft signup",
            $"Verify within 24 hours: {link}\nToken: {token}",
            ct);

        return Results.Ok(new SignupAcceptedResponse(request.Id, uniqueSlug));
    }

    private static async Task<IResult> Verify(
        [FromBody] VerifySignupRequest body,
        PlatformDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Token))
        {
            return Results.BadRequest(new { error = "invalid_token" });
        }

        var hash = TokenHash.Hash(body.Token);
        var row = await db.SignupRequests.FirstOrDefaultAsync(s => s.VerificationTokenHash == hash, ct);
        if (row is null)
        {
            return Results.NotFound(new { error = "unknown_token" });
        }

        if (row.JoinRequestForTenantId is not null)
        {
            return Results.BadRequest(new { error = "join_request" });
        }

        if (row.ExpiresAt < clock.UtcNow && row.VerifiedAt is null)
        {
            return Results.BadRequest(new { error = "expired" });
        }

        if (row.VerifiedAt is null)
        {
            row.VerifiedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ApproveJoin(
        [FromBody] ApproveJoinRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        IEmailSender email,
        IClock clock,
        CancellationToken ct)
    {
        var userIdRaw = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdRaw, out var userId))
        {
            return Results.Unauthorized();
        }

        var tidRaw = principal.FindFirstValue(LagerkraftClaims.TenantId);
        if (!Guid.TryParse(tidRaw, out var tenantId))
        {
            return Results.Forbid();
        }

        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId && m.IsOwner, ct);
        if (membership is null)
        {
            return Results.Forbid();
        }

        var row = await db.SignupRequests.FirstOrDefaultAsync(s => s.Id == body.SignupId, ct);
        if (row is null || row.JoinRequestForTenantId != tenantId)
        {
            return Results.NotFound();
        }

        if (row.JoinDecidedAt is not null)
        {
            return Results.Conflict(new { error = "already_decided" });
        }

        var role = await db.Roles.AsNoTracking()
            .FirstAsync(r => r.TenantId == null && r.InternalName == Permissions.FloorWorker, ct);
        var invitation = new Invitation
        {
            Id = Ids.New(),
            TenantId = tenantId,
            Email = row.Email,
            RoleId = role.Id,
            TokenHash = TokenHash.Hash(TokenHash.CreateToken()),
            InvitedBy = userId,
            ExpiresAt = clock.UtcNow.AddDays(7)
        };
        db.Invitations.Add(invitation);
        row.JoinDecidedAt = clock.UtcNow;
        row.JoinDecidedBy = userId;
        await db.SaveChangesAsync(ct);

        await email.SendAsync(
            row.Email,
            "You were invited to Lagerkraft",
            $"Your join request for the tenant was approved. Invitation id: {invitation.Id}",
            ct);

        return Results.Ok(new { invitation_id = invitation.Id });
    }

    private static async Task<IResult> SlugAvailable(string slug, PlatformDbContext db, CancellationToken ct)
    {
        if (!Slug.TryCreate(slug, out var parsed))
        {
            return Results.Ok(new { available = false });
        }

        var taken = await db.Tenants.AnyAsync(t => t.Slug == parsed.Value, ct)
            || await db.SignupRequests.AnyAsync(
                s => s.Slug == parsed.Value && s.JoinRequestForTenantId == null, ct);
        return Results.Ok(new { available = !taken, slug = parsed.Value });
    }
}
