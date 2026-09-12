using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Tenancy;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.Platform.Auth.Oidc;

public static class OidcApi
{
    public static WebApplication MapOidcApi(this WebApplication app)
    {
        var auth = app.MapGroup("/auth/oidc");
        auth.MapPost("/start", Start);
        auth.MapPost("/callback", Callback);

        var admin = app.MapGroup("/oidc/provider").RequireAuthorization();
        admin.MapGet("/", GetProvider);
        admin.MapPut("/", UpsertProvider);
        admin.MapPost("/enforced", SetEnforced);
        admin.MapPost("/test-login", TestLogin);
        return app;
    }

    private static async Task<IResult> Start(
        [FromBody] OidcStartRequest body,
        DynamicOidcHandler oidc,
        OidcStateStore states,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var provider = await oidc.ResolveBySlugOrEmailAsync(body.Slug, body.Email, ct);
        if (provider is null)
        {
            return Results.NotFound(new { error = "no_provider" });
        }

        var options = await oidc.BuildOptionsAsync(provider, ct);
        var discovery = await oidc.GetDiscoveryAsync(provider.Issuer, ct);
        var state = states.Create(provider.TenantId, provider.Id);
        var redirect = (configuration["PublicBaseUrl"] ?? "http://localhost:5100").TrimEnd('/')
            + "/auth/oidc/callback";
        var url = discovery.AuthorizationEndpoint
            + "?client_id=" + Uri.EscapeDataString(options.ClientId!)
            + "&response_type=code"
            + "&scope=" + Uri.EscapeDataString("openid profile email")
            + "&redirect_uri=" + Uri.EscapeDataString(redirect)
            + "&state=" + Uri.EscapeDataString(state);
        return Results.Ok(new OidcStartResponse(url, state, provider.TenantId));
    }

    private static async Task<IResult> Callback(
        [FromBody] OidcCallbackRequest body,
        OidcStateStore states,
        PlatformDbContext db,
        DynamicOidcHandler oidc,
        OidcProvisioner provisioner,
        JwtIssuer jwt,
        RefreshTokenStore refresh,
        CancellationToken ct)
    {
        if (!states.TryTake(body.State, out var state))
        {
            return Results.BadRequest(new { error = "invalid_state" });
        }

        var provider = await db.IdentityProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == state.ProviderId && p.TenantId == state.TenantId, ct);
        if (provider is null)
        {
            return Results.BadRequest(new { error = "unknown_provider" });
        }

        JwtSecurityToken idToken;
        try
        {
            idToken = await ValidateIdTokenAsync(oidc, provider, body.IdToken, ct);
        }
        catch (Exception)
        {
            return Results.Unauthorized();
        }

        var (user, membership, error) = await provisioner.ProvisionAsync(provider, idToken, ct);
        if (error is not null || user is null || membership is null)
        {
            return Results.BadRequest(new { error = error ?? "provision_failed" });
        }

        var amr = $"oidc:{provider.Type}";
        return Results.Ok(await AuthApi.IssueAsync(jwt, refresh, membership, amr, deviceId: null, ct));
    }

    private static async Task<IResult> GetProvider(
        ClaimsPrincipal principal,
        PlatformDbContext db,
        CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var provider = await db.IdentityProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        return provider is null
            ? Results.NotFound()
            : Results.Ok(ToResponse(provider));
    }

    private static async Task<IResult> UpsertProvider(
        [FromBody] UpsertOidcProviderRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        ConnectionStringProtector protector,
        CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var jit = (body.JitProvisioning ?? "off").ToLowerInvariant();
        if (jit is not ("off" or "mapped" or "all"))
        {
            return Results.BadRequest(new { error = "invalid_jit" });
        }

        var provider = await db.IdentityProviders.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        if (provider is null)
        {
            provider = new IdentityProvider { Id = Ids.New(), TenantId = tenantId, Type = "oidc" };
            db.IdentityProviders.Add(provider);
        }

        provider.Issuer = body.Issuer.TrimEnd('/');
        provider.ClientId = body.ClientId;
        provider.ClientSecret = protector.Protect(body.ClientSecret);
        provider.ClientSecretExpiresAt = body.ClientSecretExpiresAt;
        provider.DomainHint = body.DomainHint;
        provider.JitProvisioning = jit;
        provider.DefaultRole = body.DefaultRole;
        provider.RoleMappings = body.RoleMappings;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(provider));
    }

    private static async Task<IResult> SetEnforced(
        [FromBody] SetEnforcedRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        UserManager<AppUser> users,
        CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId) || !TryUser(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId && m.IsOwner, ct);
        if (membership is null)
        {
            return Results.Forbid();
        }

        var provider = await db.IdentityProviders.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        if (provider is null)
        {
            return Results.NotFound();
        }

        if (!body.Enforced)
        {
            provider.Enforced = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId, ct);
        if (tenant.LifecycleState != LifecycleState.Active)
        {
            return Results.BadRequest(new { error = "tenant_not_active" });
        }

        var ssoAdmin = await db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.TenantId == tenantId && a.Kind == "sso_login", ct);
        if (!ssoAdmin)
        {
            return Results.BadRequest(new { error = "sso_login_required" });
        }

        var owners = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.IsOwner)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        foreach (var ownerId in owners)
        {
            var owner = await users.FindByIdAsync(ownerId.ToString());
            if (owner is null || !owner.TwoFactorEnabled)
            {
                return Results.BadRequest(new { error = "owners_need_totp" });
            }
        }

        provider.Enforced = true;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestLogin(
        [FromBody] OidcTestLoginRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        DynamicOidcHandler oidc,
        CancellationToken ct)
    {
        if (!TryTenant(principal, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var provider = await db.IdentityProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        if (provider is null)
        {
            return Results.NotFound();
        }

        JwtSecurityToken token;
        try
        {
            token = await ValidateIdTokenAsync(oidc, provider, body.IdToken, ct);
        }
        catch
        {
            return Results.BadRequest(new { error = "invalid_token" });
        }

        var claims = token.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.First().Value);
        return Results.Ok(new OidcTestLoginResponse(claims));
    }

    private static async Task<JwtSecurityToken> ValidateIdTokenAsync(
        DynamicOidcHandler oidc,
        IdentityProvider provider,
        string idToken,
        CancellationToken ct)
    {
        var discovery = await oidc.GetDiscoveryAsync(provider.Issuer, ct);
        var parms = new TokenValidationParameters
        {
            ValidIssuer = provider.Issuer.TrimEnd('/'),
            ValidAudience = provider.ClientId,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(idToken, parms, out var validated);
        return (JwtSecurityToken)validated;
    }

    private static OidcProviderResponse ToResponse(IdentityProvider p) => new(
        p.Id, p.Issuer, p.ClientId, p.DomainHint, p.JitProvisioning, p.DefaultRole,
        p.Enforced, p.ClientSecretExpiresAt, p.RoleMappings);

    private static bool TryTenant(ClaimsPrincipal principal, out Guid tenantId)
    {
        tenantId = default;
        var raw = principal.FindFirstValue(LagerkraftClaims.TenantId);
        // JWT handler may JSON-serialize claim values with quotes
        raw = raw?.Trim('"');
        return Guid.TryParse(raw, out tenantId);
    }

    private static bool TryUser(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var raw = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        raw = raw?.Trim('"');
        return Guid.TryParse(raw, out userId);
    }
}
