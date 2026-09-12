using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.Platform.Auth.Oidc;

public sealed class OidcProvisioner(PlatformDbContext db, UserManager<AppUser> users, IClock clock)
{
    public const string GroupsOverflowMessage =
        "IdP groups claim overflowed. Configure app roles instead of large group lists.";

    public async Task<(AppUser? User, Membership? Membership, string? Error)> ProvisionAsync(
        IdentityProvider provider,
        JwtSecurityToken idToken,
        CancellationToken ct)
    {
        var claims = idToken.Claims.ToList();
        if (claims.Any(c => c.Type == "_claim_names"))
        {
            return (null, null, GroupsOverflowMessage);
        }

        var email = claims.FirstOrDefault(c => c.Type is "email" or ClaimTypes.Email)?.Value
            ?? claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value;
        var emailVerified = claims.FirstOrDefault(c => c.Type == "email_verified")?.Value;
        var subject = claims.FirstOrDefault(c => c.Type is "sub" or JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(subject))
        {
            return (null, null, "missing_claims");
        }

        email = email.Trim().ToLowerInvariant();
        var verified = string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase)
            || emailVerified == "True"
            || emailVerified == "1";
        if (!verified)
        {
            return (null, null, "email_unverified");
        }

        if (!string.IsNullOrWhiteSpace(provider.DomainHint))
        {
            var at = email.LastIndexOf('@');
            var domain = at > 0 ? email[(at + 1)..] : "";
            if (!string.Equals(domain, provider.DomainHint, StringComparison.OrdinalIgnoreCase))
            {
                return (null, null, "domain_mismatch");
            }
        }

        var roles = claims.Where(c => c.Type is "roles" or "role").Select(c => c.Value).ToList();
        var groups = claims.Where(c => c.Type == "groups").Select(c => c.Value).ToList();
        // Also support JSON array in a single roles claim
        foreach (var c in claims.Where(c => c.Type == "roles"))
        {
            if (c.Value.StartsWith('['))
            {
                try
                {
                    roles.AddRange(JsonSerializer.Deserialize<string[]>(c.Value) ?? []);
                }
                catch
                {
                    // ignore
                }
            }
        }

        var loginProvider = $"oidc:{provider.TenantId:N}";
        var existingLogin = await users.FindByLoginAsync(loginProvider, subject);
        if (existingLogin is not null)
        {
            var membership = await db.Memberships
                .Include(m => m.Tenant)
                .Include(m => m.RoleAssignments).ThenInclude(a => a.Role)
                .FirstOrDefaultAsync(m => m.UserId == existingLogin.Id && m.TenantId == provider.TenantId, ct);
            if (membership is null)
            {
                return (null, null, "no_membership");
            }

            await WriteSsoAudit(provider.TenantId, existingLogin.Id, ct);
            return (existingLogin, membership, null);
        }

        var local = await users.FindByEmailAsync(email);
        if (local is not null)
        {
            // Strict link: same tenant provider + verified email + domain (already checked)
            var membership = await db.Memberships
                .Include(m => m.Tenant)
                .Include(m => m.RoleAssignments).ThenInclude(a => a.Role)
                .FirstOrDefaultAsync(m => m.UserId == local.Id && m.TenantId == provider.TenantId, ct);
            if (membership is null)
            {
                return (null, null, "invite_required");
            }

            await users.AddLoginAsync(local, new UserLoginInfo(loginProvider, subject, provider.Issuer));
            await WriteSsoAudit(provider.TenantId, local.Id, ct);
            return (local, membership, null);
        }

        // JIT
        var mode = provider.JitProvisioning?.ToLowerInvariant() ?? "off";
        if (mode == "off")
        {
            return (null, null, "jit_off");
        }

        string? roleName = null;
        if (mode == "mapped")
        {
            roleName = DynamicOidcHandler.MapRole(provider, roles, groups);
            if (string.IsNullOrWhiteSpace(roleName))
            {
                return (null, null, "jit_mapped_no_role");
            }
        }
        else if (mode == "all")
        {
            roleName = provider.DefaultRole ?? Permissions.Viewer;
        }
        else
        {
            return (null, null, "invalid_jit_mode");
        }

        var role = await db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == null && r.InternalName == roleName, ct);
        if (role is null)
        {
            return (null, null, "unknown_role");
        }

        var user = new AppUser
        {
            Id = Ids.New(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        var create = await users.CreateAsync(user);
        if (!create.Succeeded)
        {
            return (null, null, "user_create_failed");
        }

        await users.AddLoginAsync(user, new UserLoginInfo(loginProvider, subject, provider.Issuer));

        var membershipNew = new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = provider.TenantId,
            IsOwner = false
        };
        db.Memberships.Add(membershipNew);
        db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Ids.New(),
            MembershipId = membershipNew.Id,
            RoleId = role.Id,
            ValidFrom = clock.UtcNow
        });
        await db.SaveChangesAsync(ct);

        membershipNew = await db.Memberships
            .Include(m => m.Tenant)
            .Include(m => m.RoleAssignments).ThenInclude(a => a.Role)
            .FirstAsync(m => m.Id == membershipNew.Id, ct);

        await WriteSsoAudit(provider.TenantId, user.Id, ct);
        return (user, membershipNew, null);
    }

    private async Task WriteSsoAudit(Guid tenantId, Guid userId, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ids.New(),
            TenantId = tenantId,
            Kind = "sso_login",
            Actor = userId.ToString(),
            At = clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
